using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Planning;
using ExchangeRbacStudio.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.Infrastructure.Exchange;

public sealed record ExchangeHostOptions(Func<string?> PowerShellPath, string ScriptPath, string WorkingDirectory)
{
    public static ExchangeHostOptions Default(AppPaths paths, Func<string?> powerShellPath) =>
        new(powerShellPath, Path.Combine(AppContext.BaseDirectory, "PowerShell", "ExchangeHost.ps1"), paths.Root);
}

/// <summary>
/// <see cref="IExchangeRbacGateway"/> backed by an isolated, persistent PowerShell 7 host process running the
/// ExchangeOnlineManagement module. Reads are retried on transient failures; writes are executed exactly once.
/// </summary>
public sealed partial class PowerShellExchangeGateway(ExchangeHostOptions options, ILogger<PowerShellExchangeGateway> logger) : IExchangeRbacGateway
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(15)];

    private readonly SemaphoreSlim _startLock = new(1, 1);
    private PowerShellHostProcess? _host;

    public event EventHandler<ExchangeSessionProblemEventArgs>? SessionProblemDetected;

    public bool IsHostRunning => _host?.IsRunning == true;

    /// <summary>Overridable delay used between read retries (tests replace it).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public async Task<ExchangeHostInfo> StartAsync(CancellationToken cancellationToken)
    {
        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_host is { IsRunning: false } stale)
            {
                await stale.DisposeAsync().ConfigureAwait(false);
                _host = null;
            }

            if (_host is null)
            {
                var pwsh = PowerShellLocator.Locate(options.PowerShellPath());
                Directory.CreateDirectory(options.WorkingDirectory);
                var host = PowerShellHostProcess.Start(pwsh, options.ScriptPath, options.WorkingDirectory, logger);
                host.Exited += OnHostExited;
                _host = host;
            }
        }
        finally
        {
            _startLock.Release();
        }

        var response = await SendReadAsync("Host.Hello", NoParameters, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        var record = response.GetRecords().FirstOrDefault()
            ?? throw new StudioOperationException(FailureKind.HostUnavailable, "The PowerShell host did not return its version.");

        var version = record.RequiredString("PSVersion", "unknown");
        if (Version.TryParse(version.Split('-')[0], out var parsed) && parsed < new Version(7, 4))
            throw new StudioOperationException(FailureKind.HostUnavailable, $"PowerShell {version} is not supported. PowerShell 7.4 or later is required.",
                remediation: "Update PowerShell 7 (winget upgrade Microsoft.PowerShell).");

        return new ExchangeHostInfo(version, record.RequiredString("PSEdition", "Core"), record.Int("ProcessId") ?? 0, record.Strings("ModuleVersions"));
    }

    public async Task<ExchangeModuleInfo> ImportModuleAsync(Version minimumVersion, CancellationToken cancellationToken)
    {
        var response = await SendReadAsync("Host.ImportModule",
            new Dictionary<string, object?> { ["MinimumVersion"] = minimumVersion.ToString() }, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
        var record = response.GetRecords().First();
        return new ExchangeModuleInfo(record.RequiredString("Version"), record.String("ModuleBase"));
    }

    public async Task<ExchangeConnectionInfo> ConnectAsync(ExchangeConnectOptions connectOptions, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["ExchangeEnvironmentName"] = connectOptions.ExchangeEnvironmentName,
            ["UseWam"] = connectOptions.UseWebAccountManager,
            ["CommandName"] = connectOptions.CommandNames.ToArray(),
        };
        if (!string.IsNullOrWhiteSpace(connectOptions.UserPrincipalName))
            parameters["UserPrincipalName"] = connectOptions.UserPrincipalName;

        // Interactive sign-in cannot be interrupted inside the host; cancelling terminates the host process instead.
        await using var registration = cancellationToken.Register(() => _host?.Kill()).ConfigureAwait(false);
        HostResponse response;
        try
        {
            response = await SendOnceAsync("Host.Connect", parameters, ConnectTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (StudioOperationException ex) when (cancellationToken.IsCancellationRequested && ex.Kind == FailureKind.HostUnavailable)
        {
            throw new OperationCanceledException("Exchange Online sign-in was cancelled.", ex, cancellationToken);
        }

        EnsureSuccess(response, "Connect-ExchangeOnline");
        var record = response.GetRecords().FirstOrDefault()
            ?? throw new StudioOperationException(FailureKind.AuthenticationFailed, "Connect-ExchangeOnline completed, but no active connection was reported.");
        return ToConnectionInfo(record);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (!IsHostRunning) return;
        try
        {
            var response = await SendOnceAsync("Host.Disconnect", NoParameters, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, "Disconnect-ExchangeOnline");
        }
        catch (StudioOperationException ex) when (ex.Kind is FailureKind.Timeout or FailureKind.HostUnavailable)
        {
            logger.LogWarning("Disconnect did not complete ({Kind}); terminating the PowerShell host", ex.Kind);
            _host?.Kill();
        }
    }

    public async Task<ExchangeConnectionInfo?> GetConnectionInfoAsync(CancellationToken cancellationToken)
    {
        if (!IsHostRunning) return null;
        var response = await SendReadAsync("Host.ConnectionInformation", NoParameters, ReadTimeout, cancellationToken).ConfigureAwait(false);
        var record = response.GetRecords().FirstOrDefault();
        return record is null ? null : ToConnectionInfo(record);
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetCommandAvailabilityAsync(IReadOnlyList<string> commandNames, CancellationToken cancellationToken)
    {
        var response = await SendReadAsync("Host.CommandAvailability",
            new Dictionary<string, object?> { ["CommandName"] = commandNames.ToArray() }, ReadTimeout, cancellationToken).ConfigureAwait(false);
        return response.GetRecords().ToDictionary(r => r.RequiredString("Name"), r => r.Bool("Available") == true, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<ExchangeServicePrincipal>> GetServicePrincipalsAsync(CancellationToken cancellationToken)
    {
        var response = await SendReadAsync("Get-ServicePrincipal", NoParameters, ReadTimeout, cancellationToken).ConfigureAwait(false);
        return response.GetRecords().Select(ToServicePrincipal).ToList();
    }

    public async Task<IReadOnlyList<ManagementScope>> GetManagementScopesAsync(CancellationToken cancellationToken)
    {
        var response = await SendReadAsync("Get-ManagementScope", NoParameters, ReadTimeout, cancellationToken).ConfigureAwait(false);
        return response.GetRecords().Select(ToScope).ToList();
    }

    public async Task<ManagementScope?> GetManagementScopeAsync(string identity, CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendReadAsync("Get-ManagementScope", new Dictionary<string, object?> { ["Identity"] = identity }, ReadTimeout, cancellationToken).ConfigureAwait(false);
            return response.GetRecords().Select(ToScope).FirstOrDefault(s => s.Name.Equals(identity, StringComparison.OrdinalIgnoreCase) || s.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase));
        }
        catch (ExchangeOperationException ex) when (ex.Kind == FailureKind.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ApplicationRoleInfo>> GetApplicationRolesAsync(CancellationToken cancellationToken)
    {
        var response = await SendReadAsync("Host.GetApplicationRoles", NoParameters, ReadTimeout, cancellationToken).ConfigureAwait(false);
        return response.GetRecords()
            .Select(r => new ApplicationRoleInfo(r.RequiredString("Name"), r.String("Description"), r.String("RoleType")))
            .Where(r => r.Name.Length > 0)
            .ToList();
    }

    public async Task<IReadOnlyList<ManagementRoleAssignment>> GetApplicationRoleAssignmentsAsync(IReadOnlyList<string> roleNames, CancellationToken cancellationToken)
    {
        if (roleNames.Count == 0) return [];
        var response = await SendReadAsync("Host.GetApplicationRoleAssignments",
            new Dictionary<string, object?> { ["Role"] = roleNames.ToArray() }, ReadTimeout, cancellationToken).ConfigureAwait(false);
        return response.GetRecords().Select(ToAssignment).DistinctBy(a => a.Identity).ToList();
    }

    public async Task<IReadOnlyList<ScopeReference>> GetScopeReferencesAsync(string scopeName, CancellationToken cancellationToken)
    {
        var response = await SendReadAsync("Host.GetScopeReferences",
            new Dictionary<string, object?> { ["ScopeName"] = scopeName }, ReadTimeout, cancellationToken).ConfigureAwait(false);
        return response.GetRecords().Select(r => new ScopeReference(
            r.RequiredString("AssignmentName"), r.RequiredString("Role"), r.String("RoleAssigneeName"), r.String("RoleAssigneeType"), r.RequiredString("ReferencingProperty"))).ToList();
    }

    public async Task<IReadOnlyList<ExchangeRecipient>> SearchRecipientsAsync(string text, int maxResults, CancellationToken cancellationToken)
    {
        var response = await SendReadAsync("Get-Recipient", new Dictionary<string, object?>
        {
            ["Anr"] = text,
            ["ResultSize"] = Math.Clamp(maxResults, 1, 1000).ToString(CultureInfo.InvariantCulture),
        }, ReadTimeout, cancellationToken).ConfigureAwait(false);
        return response.GetRecords().Select(ToRecipient).ToList();
    }

    public async Task<ExchangeRecipient?> GetRecipientAsync(string identity, CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendReadAsync("Get-Recipient", new Dictionary<string, object?> { ["Identity"] = identity }, ReadTimeout, cancellationToken).ConfigureAwait(false);
            var records = response.GetRecords();
            if (records.Count > 1)
                throw new StudioOperationException(FailureKind.InvalidInput, $"'{identity}' matches {records.Count} recipients. Use the primary SMTP address or GUID.");
            return records.Select(ToRecipient).FirstOrDefault();
        }
        catch (ExchangeOperationException ex) when (ex.Kind == FailureKind.NotFound)
        {
            return null;
        }
    }

    public async Task<RecipientPreviewResult> PreviewRecipientFilterAsync(string filter, int maxResults, CancellationToken cancellationToken)
    {
        var response = await SendReadAsync("Host.PreviewRecipientFilter", new Dictionary<string, object?>
        {
            ["Filter"] = filter,
            ["ResultSize"] = Math.Clamp(maxResults, 1, 5000),
        }, ReadTimeout, cancellationToken).ConfigureAwait(false);
        return new RecipientPreviewResult(response.GetRecords().Select(ToRecipient).ToList(), response.MetaFlag("truncated"));
    }

    public async Task<IReadOnlyList<AuthorizationTestRow>> TestServicePrincipalAuthorizationAsync(string servicePrincipalIdentity, string resource, CancellationToken cancellationToken)
    {
        var response = await SendReadAsync("Test-ServicePrincipalAuthorization", new Dictionary<string, object?>
        {
            ["Identity"] = servicePrincipalIdentity,
            ["Resource"] = resource,
        }, ReadTimeout, cancellationToken).ConfigureAwait(false);

        return response.GetRecords().Select(r => new AuthorizationTestRow(
            r.RequiredString("RoleName"),
            r.String("GrantedPermissions"),
            r.String("AllowedResourceScope"),
            r.String("ScopeType"),
            AuthorizationTestRow.ParseInScope(r.String("InScope")))).ToList();
    }

    public async Task<IReadOnlyList<ApplicationAccessPolicy>> GetApplicationAccessPoliciesAsync(CancellationToken cancellationToken)
    {
        var response = await SendReadAsync("Get-ApplicationAccessPolicy", NoParameters, ReadTimeout, cancellationToken).ConfigureAwait(false);
        return response.GetRecords().Select(r => new ApplicationAccessPolicy(
            r.RequiredString("Identity"), r.String("AppId"), r.String("ScopeName"), r.String("AccessRight"), r.String("Description"))).ToList();
    }

    public async Task<ExchangeCommandResult> ExecuteAsync(ExchangeCommand command, CancellationToken cancellationToken)
    {
        if (!ExchangeCommands.WriteCommandNames.Contains(command.Name))
            throw new StudioOperationException(FailureKind.InvalidInput, $"'{command.Name}' is not an allowed write command.");

        var parameters = command.Parameters.ToDictionary(p => p.Name, p => (object?)p.WireValue, StringComparer.Ordinal);
        logger.LogInformation("Executing {Command} ({ParameterNames})", command.Name, string.Join(", ", parameters.Keys));
        var stopwatch = Stopwatch.StartNew();
        var response = await SendOnceAsync(command.Name, parameters, WriteTimeout, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, command.Name);
        return new ExchangeCommandResult(response.Warnings ?? [], response.Records?.Count ?? 0, stopwatch.Elapsed);
    }

    public async ValueTask DisposeAsync()
    {
        var host = _host;
        _host = null;
        if (host is not null)
        {
            host.Exited -= OnHostExited;
            await host.DisposeAsync().ConfigureAwait(false);
        }

        _startLock.Dispose();
    }

    private static readonly IReadOnlyDictionary<string, object?> NoParameters = new Dictionary<string, object?>();

    /// <summary>Sends a read-only request, retrying throttling and transient network failures with backoff.</summary>
    private async Task<HostResponse> SendReadAsync(string command, IReadOnlyDictionary<string, object?> parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await SendOnceAsync(command, parameters, timeout, cancellationToken).ConfigureAwait(false);
            if (response.Ok) return response;

            var error = ExchangeErrorClassifier.Classify(response.Error ?? new HostError { Message = "Unknown error." }, command);
            if (error.IsTransient && error.Kind != FailureKind.Timeout && attempt < RetryDelays.Length)
            {
                var delay = RetryAfter(response.Error?.Message) ?? RetryDelays[attempt];
                logger.LogWarning("{Command} failed with {Kind}; retrying in {Delay} (attempt {Attempt})", command, error.Kind, delay, attempt + 1);
                await Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            Raise(error);
            throw error;
        }
    }

    private async Task<HostResponse> SendOnceAsync(string command, IReadOnlyDictionary<string, object?> parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var host = _host;
        if (host is null || !host.IsRunning)
        {
            var error = new StudioOperationException(FailureKind.HostUnavailable, "The PowerShell host is not running. Connect to Exchange Online first.");
            throw error;
        }

        try
        {
            return await host.SendAsync(command, parameters, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (StudioOperationException ex) when (ex.Kind == FailureKind.HostUnavailable)
        {
            Raise(ex);
            throw;
        }
    }

    private void EnsureSuccess(HostResponse response, string command)
    {
        if (response.Ok) return;
        var error = ExchangeErrorClassifier.Classify(response.Error ?? new HostError { Message = "Unknown error." }, command);
        Raise(error);
        throw error;
    }

    private void Raise(StudioOperationException error)
    {
        if (error.IsSessionProblem)
            SessionProblemDetected?.Invoke(this, new ExchangeSessionProblemEventArgs(error));
    }

    private void OnHostExited(object? sender, EventArgs e) =>
        SessionProblemDetected?.Invoke(this, new ExchangeSessionProblemEventArgs(
            new StudioOperationException(FailureKind.HostUnavailable, "The PowerShell host process exited.")));

    private static TimeSpan? RetryAfter(string? message)
    {
        if (message is null) return null;
        var match = RetryAfterPattern().Match(message);
        return match.Success && int.TryParse(match.Groups[1].Value, out var seconds) ? TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 120)) : null;
    }

    internal static ExchangeConnectionInfo ToConnectionInfo(HostRecord r) => new(
        r.RequiredString("State", "Unknown"), r.String("TokenStatus"), r.Guid("TenantID"), r.String("UserPrincipalName"),
        r.String("Organization"), r.Date("TokenExpiryTimeUTC"), r.String("ConnectionUri"));

    internal static ExchangeServicePrincipal ToServicePrincipal(HostRecord r) => new(
        r.RequiredString("Identity", r.RequiredString("Name")), r.String("Name"), r.RequiredString("DisplayName", r.RequiredString("Name")),
        r.Guid("AppId"), r.Guid("ObjectId") ?? r.Guid("ServiceId"));

    internal static ManagementScope ToScope(HostRecord r) => new(
        r.RequiredString("Name"), r.RequiredString("Identity", r.RequiredString("Name")), r.Guid("Guid"), r.String("RecipientFilter"),
        r.String("RecipientRoot"), r.String("ScopeRestrictionType"), r.Bool("Exclusive") ?? false, r.Date("WhenChangedUTC"));

    internal static ManagementRoleAssignment ToAssignment(HostRecord r) => new(
        r.RequiredString("Name"), r.RequiredString("Identity", r.RequiredString("Name")), r.RequiredString("Role"),
        r.String("RoleAssignee"), r.String("RoleAssigneeName"), r.String("RoleAssigneeType"),
        r.String("CustomResourceScope"), r.String("RecipientWriteScope"), r.String("CustomRecipientWriteScope"),
        r.String("RecipientAdministrativeUnitScope"), r.String("RecipientGroupScope"), r.Bool("Enabled") ?? true, r.Date("WhenCreatedUTC"));

    internal static ExchangeRecipient ToRecipient(HostRecord r) => new(
        r.RequiredString("Identity", r.RequiredString("Name")), r.RequiredString("DisplayName", r.RequiredString("Name")),
        r.String("PrimarySmtpAddress"), r.String("Alias"), r.String("RecipientType"), r.String("RecipientTypeDetails"),
        r.Guid("ExchangeGuid"), r.Guid("Guid"), r.String("ExternalDirectoryObjectId"));

    [GeneratedRegex(@"(?i)(?:after|in)\s+(\d{1,3})\s+second")]
    private static partial Regex RetryAfterPattern();
}
