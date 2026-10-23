using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Auditing;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Domain.Auditing;
using ExchangeRbacStudio.Domain.Clouds;
using ExchangeRbacStudio.Domain.Identity;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.Application.Sessions;

/// <summary>
/// Owns the administration session: interactive sign-in to Microsoft Graph and Exchange Online, tenant binding and
/// validation, privilege discovery, health checks and clean disconnection.
/// </summary>
public sealed class ConnectionManager
{
    private readonly IEntraDirectory _entra;
    private readonly IExchangeRbacGateway _exchange;
    private readonly ISettingsStore _settings;
    private readonly AuditRecorder _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<ConnectionManager> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _exchangeTransition = new(1, 1);
    private readonly SemaphoreSlim _graphTransition = new(1, 1);
    private SessionSnapshot _current;

    public ConnectionManager(IEntraDirectory entra, IExchangeRbacGateway exchange, ISettingsStore settings, AuditRecorder audit, TimeProvider time, ILogger<ConnectionManager> logger)
    {
        _entra = entra;
        _exchange = exchange;
        _settings = settings;
        _audit = audit;
        _time = time;
        _logger = logger;
        _current = SessionSnapshot.Disconnected(CloudEnvironments.Get(settings.Current.Environment));
        _exchange.SessionProblemDetected += OnExchangeSessionProblem;
    }

    public event EventHandler<SessionSnapshot>? SessionChanged;

    public SessionSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await ConnectGraphAsync(cancellationToken).ConfigureAwait(false);
        await ConnectExchangeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ConnectGraphAsync(CancellationToken cancellationToken)
    {
        await _graphTransition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _settings.Current;
            var clientId = settings.EffectiveGraphClientId
                ?? throw new StudioOperationException(FailureKind.InvalidInput,
                    "No sign-in client is configured for Microsoft Graph.",
                    remediation: "Choose the Microsoft Graph Command Line Tools client or enter your organization's app registration in the sign-in options.");

            var environment = CloudEnvironments.Get(settings.Environment);
            Update(s => s with { Environment = s.IsExchangeConnected ? s.Environment : environment, Graph = ServiceConnectionState.Connecting, GraphStatusMessage = "Waiting for sign-in in your browser…" });

            EntraSignInResult result;
            try
            {
                var hint = Current.ExchangeConnection?.UserPrincipalName;
                result = await _entra.SignInAsync(new EntraSignInOptions(environment, clientId, settings.TenantHint, hint), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var failure = ex is OperationCanceledException ? "Sign-in was cancelled." : UserFacingError.From(ex).Message;
                Update(s => s with { Graph = ServiceConnectionState.Failed, GraphStatusMessage = failure });
                await _audit.RecordAsync(Current, AuditCategories.Session, "Connect Microsoft Graph", AuditOutcome.Failed, failure).ConfigureAwait(false);
                throw;
            }

            var exchangeTenant = Current.ExchangeConnection?.TenantId;
            if (Current.IsExchangeConnected && exchangeTenant is not null && exchangeTenant != result.Tenant.TenantId)
            {
                _entra.SignOut();
                var message = $"Microsoft Graph signed in to tenant {result.Tenant.TenantId} but Exchange Online is connected to tenant {exchangeTenant}. The Graph sign-in was discarded.";
                Update(s => s with { Graph = ServiceConnectionState.Failed, GraphStatusMessage = "Tenant mismatch with Exchange Online." });
                await _audit.RecordAsync(Current, AuditCategories.Session, "Connect Microsoft Graph", AuditOutcome.Failed, message).ConfigureAwait(false);
                throw new StudioOperationException(FailureKind.TenantMismatch, message);
            }

            Update(s =>
            {
                var tenantChanged = s.TenantId is not null && s.TenantId != result.Tenant.TenantId;
                return s with
                {
                    SessionId = tenantChanged ? Guid.NewGuid() : s.SessionId,
                    Environment = environment,
                    Graph = ServiceConnectionState.Connected,
                    GraphStatusMessage = $"Signed in as {result.Administrator.UserPrincipalName}",
                    Tenant = result.Tenant,
                    Administrator = result.Administrator,
                };
            });

            _logger.LogInformation("Microsoft Graph connected to tenant {TenantId}", result.Tenant.TenantId);
            await _audit.RecordAsync(Current, AuditCategories.Session, "Connect Microsoft Graph", AuditOutcome.Succeeded,
                $"Signed in to {result.Tenant.DisplayName} ({result.Tenant.TenantId}) as {result.Administrator.UserPrincipalName}.").ConfigureAwait(false);
        }
        finally
        {
            _graphTransition.Release();
        }
    }

    public async Task ConnectExchangeAsync(CancellationToken cancellationToken)
    {
        await _exchangeTransition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ConnectExchangeCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _exchangeTransition.Release();
        }
    }

    /// <summary>Disconnects and reconnects Exchange Online so newly activated (PIM) roles and cmdlets are picked up.</summary>
    public async Task ReconnectExchangeAsync(CancellationToken cancellationToken)
    {
        await _exchangeTransition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectExchangeCoreAsync().ConfigureAwait(false);
            await ConnectExchangeCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _exchangeTransition.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _exchangeTransition.WaitAsync().ConfigureAwait(false);
        try
        {
            var before = Current;
            await DisconnectExchangeCoreAsync().ConfigureAwait(false);
            _entra.SignOut();
            Update(_ => SessionSnapshot.Disconnected(CloudEnvironments.Get(_settings.Current.Environment)));
            if (before.TenantId is not null)
                await _audit.RecordAsync(before, AuditCategories.Session, "Disconnect", AuditOutcome.Succeeded, "Disconnected from Microsoft Graph and Exchange Online.").ConfigureAwait(false);
        }
        finally
        {
            _exchangeTransition.Release();
        }
    }

    /// <summary>Re-reads Get-ConnectionInformation to detect expired Exchange sessions.</summary>
    public async Task<SessionSnapshot> RefreshHealthAsync(CancellationToken cancellationToken)
    {
        if (!Current.IsExchangeConnected) return Current;
        try
        {
            var info = await _exchange.GetConnectionInfoAsync(cancellationToken).ConfigureAwait(false);
            if (info is null || !info.IsConnected)
            {
                Update(s => s with { Exchange = ServiceConnectionState.Expired, ExchangeStatusMessage = "The Exchange Online session is no longer active. Reconnect to continue.", ExchangeConnection = info ?? s.ExchangeConnection, LastHealthCheckUtc = _time.GetUtcNow() });
            }
            else
            {
                Update(s => s with { ExchangeConnection = info, LastHealthCheckUtc = _time.GetUtcNow() });
            }
        }
        catch (StudioOperationException ex) when (ex.IsSessionProblem)
        {
            Update(s => s with { Exchange = ServiceConnectionState.Expired, ExchangeStatusMessage = ex.Message, LastHealthCheckUtc = _time.GetUtcNow() });
        }

        return Current;
    }

    public SessionSnapshot RequireGraph()
    {
        var current = Current;
        if (!current.IsGraphConnected)
            throw new StudioOperationException(FailureKind.NotConnected, "Microsoft Entra ID (Microsoft Graph) is not connected.");
        return current;
    }

    public SessionSnapshot RequireExchange()
    {
        var current = Current;
        if (!current.IsExchangeConnected)
            throw new StudioOperationException(FailureKind.NotConnected,
                current.Exchange == ServiceConnectionState.Expired ? "The Exchange Online session has expired." : "Exchange Online is not connected.");
        return current;
    }

    /// <summary>Requires both connections bound to the same tenant, optionally a specific tenant.</summary>
    public SessionSnapshot RequireFullSession(Guid? expectedTenantId = null)
    {
        var current = Current;
        if (!current.IsGraphConnected || !current.IsExchangeConnected)
            throw new StudioOperationException(FailureKind.NotConnected, "Both Microsoft Entra ID and Exchange Online must be connected.");
        if (!current.TenantConsistent)
            throw new StudioOperationException(FailureKind.TenantMismatch, "Microsoft Graph and Exchange Online are connected to different tenants.");
        if (expectedTenantId is not null && current.TenantId != expectedTenantId)
            throw new StudioOperationException(FailureKind.TenantMismatch,
                $"This change was prepared for tenant {expectedTenantId} but the active session is bound to tenant {current.TenantId}.");
        return current;
    }

    private async Task ConnectExchangeCoreAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        var environment = Current.IsGraphConnected ? Current.Environment : CloudEnvironments.Get(settings.Environment);
        Update(s => s with { Environment = environment, Exchange = ServiceConnectionState.Connecting, ExchangeStatusMessage = "Starting PowerShell 7 host…" });

        try
        {
            string? psVersion = Current.PowerShellVersion;
            if (!_exchange.IsHostRunning)
            {
                var host = await _exchange.StartAsync(cancellationToken).ConfigureAwait(false);
                psVersion = host.PowerShellVersion;
                Update(s => s with { PowerShellVersion = host.PowerShellVersion });
            }

            Update(s => s with { ExchangeStatusMessage = "Loading ExchangeOnlineManagement…" });
            var minimum = Version.TryParse(settings.MinimumExchangeModuleVersion, out var v) ? v : new Version(3, 7, 0);
            var module = await _exchange.ImportModuleAsync(minimum, cancellationToken).ConfigureAwait(false);

            Update(s => s with { ExchangeStatusMessage = "Waiting for Exchange Online sign-in…", ExchangeModuleVersion = module.Version });
            var options = new ExchangeConnectOptions(Current.Administrator?.UserPrincipalName, environment.ExchangeEnvironmentName,
                settings.UseWebAccountManagerForExchange, ExchangeCommandSets.All);
            var info = await _exchange.ConnectAsync(options, cancellationToken).ConfigureAwait(false);

            if (!info.IsConnected)
                throw new StudioOperationException(FailureKind.AuthenticationFailed, $"Exchange Online reported connection state '{info.State}' (token: {info.TokenStatus ?? "unknown"}).");

            var graphTenant = Current.Tenant?.TenantId;
            if (Current.IsGraphConnected && info.TenantId is not null && graphTenant is not null && info.TenantId != graphTenant)
            {
                await SafeDisconnectExchangeAsync().ConfigureAwait(false);
                throw new StudioOperationException(FailureKind.TenantMismatch,
                    $"Exchange Online signed in to tenant {info.TenantId} but Microsoft Graph is connected to tenant {graphTenant}. The Exchange session was closed.");
            }

            Update(s => s with { ExchangeStatusMessage = "Checking assigned Exchange permissions…" });
            var availability = await _exchange.GetCommandAvailabilityAsync(ExchangeCommandSets.All, cancellationToken).ConfigureAwait(false);
            var missing = ExchangeCommandSets.All.Where(c => !availability.TryGetValue(c, out var ok) || !ok).ToList();

            Update(s =>
            {
                var tenant = s.Tenant;
                if (tenant is null && info.TenantId is { } tid)
                    tenant = new TenantIdentity(tid, info.Organization ?? tid.ToString(), info.Organization, environment.Environment);
                return s with
                {
                    Exchange = ServiceConnectionState.Connected,
                    ExchangeStatusMessage = missing.Count == 0
                        ? $"Connected as {info.UserPrincipalName}"
                        : $"Connected as {info.UserPrincipalName}; {missing.Count} required cmdlet(s) are not available with your current roles.",
                    ExchangeConnection = info,
                    Tenant = tenant,
                    MissingExchangeCommands = missing,
                    PowerShellVersion = psVersion,
                    LastHealthCheckUtc = _time.GetUtcNow(),
                };
            });

            _logger.LogInformation("Exchange Online connected to tenant {TenantId} (module {ModuleVersion}); missing cmdlets: {Missing}", info.TenantId, module.Version, missing.Count);
            await _audit.RecordAsync(Current, AuditCategories.Session, "Connect Exchange Online", AuditOutcome.Succeeded,
                $"Connected as {info.UserPrincipalName} using ExchangeOnlineManagement {module.Version}." + (missing.Count > 0 ? " Unavailable cmdlets: " + string.Join(", ", missing) : string.Empty)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "Connection was cancelled." : UserFacingError.From(ex).Message;
            Update(s => s with { Exchange = ServiceConnectionState.Failed, ExchangeStatusMessage = message, ExchangeConnection = null, MissingExchangeCommands = [] });
            await _audit.RecordAsync(Current, AuditCategories.Session, "Connect Exchange Online", AuditOutcome.Failed, message).ConfigureAwait(false);
            throw;
        }
    }

    private async Task DisconnectExchangeCoreAsync()
    {
        await SafeDisconnectExchangeAsync().ConfigureAwait(false);
        Update(s => s with { Exchange = ServiceConnectionState.Disconnected, ExchangeStatusMessage = null, ExchangeConnection = null, MissingExchangeCommands = [] });
    }

    private async Task SafeDisconnectExchangeAsync()
    {
        if (!_exchange.IsHostRunning) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _exchange.DisconnectAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exchange Online disconnect did not complete cleanly");
        }
    }

    private void OnExchangeSessionProblem(object? sender, ExchangeSessionProblemEventArgs e)
    {
        if (!Current.IsExchangeConnected) return;
        _logger.LogWarning("Exchange session problem detected: {Kind}", e.Error.Kind);
        Update(s => s with
        {
            Exchange = ServiceConnectionState.Expired,
            ExchangeStatusMessage = e.Error.Kind == FailureKind.HostUnavailable
                ? "The PowerShell host stopped. Reconnect to continue."
                : "The Exchange Online session expired or was interrupted. Reconnect to continue.",
        });
    }

    private void Update(Func<SessionSnapshot, SessionSnapshot> change)
    {
        SessionSnapshot updated;
        lock (_gate)
        {
            updated = change(_current);
            _current = updated;
        }

        try
        {
            SessionChanged?.Invoke(this, updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SessionChanged handler failed");
        }
    }
}
