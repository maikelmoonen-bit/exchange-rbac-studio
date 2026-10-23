using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Permissions;
using ExchangeRbacStudio.Domain.Planning;
using ExchangeRbacStudio.Domain.Scoping;

namespace ExchangeRbacStudio.TestSupport;

/// <summary>Injected failure for a write command. With <see cref="ApplyChange"/> the change is applied before the error is thrown.</summary>
public sealed record FailureInjection(StudioOperationException Error, bool ApplyChange = false, int Occurrence = 1);

/// <summary>
/// In-memory model of the Exchange Online application RBAC surface with realistic semantics: uniqueness, dependencies,
/// exact-identity scope evaluation and Test-ServicePrincipalAuthorization rows. Supports failure injection.
/// </summary>
public sealed class FakeExchangeGateway : IExchangeRbacGateway
{
    private readonly Dictionary<string, int> _occurrences = new(StringComparer.OrdinalIgnoreCase);

    public List<ExchangeServicePrincipal> ServicePrincipals { get; } = [];
    public List<ManagementScope> Scopes { get; } = [];
    public List<ApplicationRoleInfo> Roles { get; } = Fixtures.DiscoveredApplicationRoles();
    public List<ManagementRoleAssignment> Assignments { get; } = [];
    public List<ScopeReference> AdministratorScopeReferences { get; } = [];
    public List<ExchangeRecipient> Recipients { get; } = [.. Fixtures.AllRecipients];
    public List<ApplicationAccessPolicy> AccessPolicies { get; } = [];
    public HashSet<string> UnavailableCommands { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FailureInjection> WriteFailures { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, StudioOperationException> ReadFailures { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ExchangeCommand> ExecutedCommands { get; } = [];
    public ExchangeConnectionInfo? Connection { get; set; } = Fixtures.Connection();

    /// <summary>Overrides the recipients returned by Get-Recipient -RecipientPreviewFilter (to simulate overmatching).</summary>
    public Func<string, IReadOnlyList<ExchangeRecipient>?>? PreviewOverride { get; set; }

    /// <summary>Called after each successful write; lets tests simulate concurrent changes.</summary>
    public Action<ExchangeCommand>? AfterWrite { get; set; }

    public event EventHandler<ExchangeSessionProblemEventArgs>? SessionProblemDetected;

    public bool IsHostRunning { get; private set; }

    public void RaiseSessionProblem(StudioOperationException error) => SessionProblemDetected?.Invoke(this, new ExchangeSessionProblemEventArgs(error));

    public Task<ExchangeHostInfo> StartAsync(CancellationToken cancellationToken)
    {
        IsHostRunning = true;
        return Task.FromResult(new ExchangeHostInfo("7.6.6", "Core", 4242, ["3.10.0"]));
    }

    public Task<ExchangeModuleInfo> ImportModuleAsync(Version minimumVersion, CancellationToken cancellationToken) =>
        Task.FromResult(new ExchangeModuleInfo("3.10.0", @"C:\Modules\ExchangeOnlineManagement\3.10.0"));

    public Task<ExchangeConnectionInfo> ConnectAsync(ExchangeConnectOptions options, CancellationToken cancellationToken) =>
        Connection is null
            ? throw new StudioOperationException(FailureKind.AuthenticationFailed, "Sign-in failed.")
            : Task.FromResult(Connection);

    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<ExchangeConnectionInfo?> GetConnectionInfoAsync(CancellationToken cancellationToken) => Read(nameof(GetConnectionInfoAsync), () => Connection);

    public Task<IReadOnlyDictionary<string, bool>> GetCommandAvailabilityAsync(IReadOnlyList<string> commandNames, CancellationToken cancellationToken) =>
        Read<IReadOnlyDictionary<string, bool>>(nameof(GetCommandAvailabilityAsync),
            () => commandNames.ToDictionary(n => n, n => !UnavailableCommands.Contains(n), StringComparer.OrdinalIgnoreCase));

    public Task<IReadOnlyList<ExchangeServicePrincipal>> GetServicePrincipalsAsync(CancellationToken cancellationToken) =>
        Read<IReadOnlyList<ExchangeServicePrincipal>>(nameof(GetServicePrincipalsAsync), () => ServicePrincipals.ToList());

    public Task<IReadOnlyList<ManagementScope>> GetManagementScopesAsync(CancellationToken cancellationToken) =>
        Read<IReadOnlyList<ManagementScope>>(nameof(GetManagementScopesAsync), () => Scopes.ToList());

    public Task<ManagementScope?> GetManagementScopeAsync(string identity, CancellationToken cancellationToken) =>
        Read(nameof(GetManagementScopeAsync), () => Scopes.FirstOrDefault(s => s.Name.Equals(identity, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<ApplicationRoleInfo>> GetApplicationRolesAsync(CancellationToken cancellationToken) =>
        Read<IReadOnlyList<ApplicationRoleInfo>>(nameof(GetApplicationRolesAsync), () => Roles.ToList());

    public Task<IReadOnlyList<ManagementRoleAssignment>> GetApplicationRoleAssignmentsAsync(IReadOnlyList<string> roleNames, CancellationToken cancellationToken) =>
        Read<IReadOnlyList<ManagementRoleAssignment>>(nameof(GetApplicationRoleAssignmentsAsync),
            () => Assignments.Where(a => roleNames.Contains(a.Role, StringComparer.OrdinalIgnoreCase)).ToList());

    public Task<IReadOnlyList<ScopeReference>> GetScopeReferencesAsync(string scopeName, CancellationToken cancellationToken) =>
        Read<IReadOnlyList<ScopeReference>>(nameof(GetScopeReferencesAsync), () =>
            Assignments.Where(a => string.Equals(a.CustomResourceScope, scopeName, StringComparison.OrdinalIgnoreCase))
                .Select(a => new ScopeReference(a.Name, a.Role, a.RoleAssigneeName, a.RoleAssigneeType, "CustomResourceScope"))
                .Concat(AdministratorScopeReferences.Where(r => r.ReferencingProperty.Length > 0 && r.AssignmentName.Length > 0 && ReferencesScope(r, scopeName)))
                .ToList());

    public Task<IReadOnlyList<ExchangeRecipient>> SearchRecipientsAsync(string text, int maxResults, CancellationToken cancellationToken) =>
        Read<IReadOnlyList<ExchangeRecipient>>(nameof(SearchRecipientsAsync), () => Recipients
            .Where(r => r.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || (r.Alias?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (r.PrimarySmtpAddress?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(maxResults).ToList());

    public Task<ExchangeRecipient?> GetRecipientAsync(string identity, CancellationToken cancellationToken) =>
        Read(nameof(GetRecipientAsync), () => Recipients.FirstOrDefault(r =>
            string.Equals(r.Guid?.ToString(), identity, StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.PrimarySmtpAddress, identity, StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.Alias, identity, StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.ExternalDirectoryObjectId, identity, StringComparison.OrdinalIgnoreCase)));

    public Task<RecipientPreviewResult> PreviewRecipientFilterAsync(string filter, int maxResults, CancellationToken cancellationToken) =>
        Read(nameof(PreviewRecipientFilterAsync), () =>
        {
            var matches = PreviewOverride?.Invoke(filter) ?? Evaluate(filter);
            return new RecipientPreviewResult(matches.Take(maxResults).ToList(), matches.Count >= maxResults);
        });

    public Task<IReadOnlyList<AuthorizationTestRow>> TestServicePrincipalAuthorizationAsync(string servicePrincipalIdentity, string resource, CancellationToken cancellationToken) =>
        Read<IReadOnlyList<AuthorizationTestRow>>(nameof(TestServicePrincipalAuthorizationAsync), () =>
        {
            var pointer = ServicePrincipals.FirstOrDefault(p => p.ReferenceValues().Contains(servicePrincipalIdentity, StringComparer.OrdinalIgnoreCase)
                                                                || string.Equals(p.AppId?.ToString(), servicePrincipalIdentity, StringComparison.OrdinalIgnoreCase))
                          ?? throw new StudioOperationException(FailureKind.NotFound, $"Service principal {servicePrincipalIdentity} couldn't be found.");
            var target = Recipients.FirstOrDefault(r => string.Equals(r.Guid?.ToString(), resource, StringComparison.OrdinalIgnoreCase)
                                                        || string.Equals(r.PrimarySmtpAddress, resource, StringComparison.OrdinalIgnoreCase))
                         ?? throw new StudioOperationException(FailureKind.NotFound, $"Mailbox {resource} couldn't be found.");

            return Assignments.Where(a => a.IsAssignedTo(pointer) && a.Enabled).Select(a =>
            {
                if (a.ScopeKind == AssignmentScopeKind.Organization)
                    return new AuthorizationTestRow(a.Role, PermissionsOf(a.Role), "Organization", "Organization", InScopeResult.InScope);
                var scope = Scopes.FirstOrDefault(s => s.IsReferencedBy(a.ScopeReference));
                var inScope = scope is not null && Evaluate(scope.RecipientFilter ?? string.Empty).Any(m => m.IdentityKey == target.IdentityKey);
                return new AuthorizationTestRow(a.Role, PermissionsOf(a.Role), a.ScopeReference, "CustomRecipientScope", inScope ? InScopeResult.InScope : InScopeResult.NotInScope);
            }).ToList();
        });

    public Task<IReadOnlyList<ApplicationAccessPolicy>> GetApplicationAccessPoliciesAsync(CancellationToken cancellationToken) =>
        Read<IReadOnlyList<ApplicationAccessPolicy>>(nameof(GetApplicationAccessPoliciesAsync), () => AccessPolicies.ToList());

    public Task<ExchangeCommandResult> ExecuteAsync(ExchangeCommand command, CancellationToken cancellationToken)
    {
        ExecutedCommands.Add(command);
        var occurrence = _occurrences[command.Name] = _occurrences.GetValueOrDefault(command.Name) + 1;
        if (WriteFailures.TryGetValue(command.Name, out var failure) && failure.Occurrence == occurrence)
        {
            if (failure.ApplyChange) Apply(command);
            throw failure.Error;
        }

        Apply(command);
        AfterWrite?.Invoke(command);
        return Task.FromResult(new ExchangeCommandResult([], 1, TimeSpan.FromMilliseconds(120)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Adds an existing scoped configuration for an application (pointer, scope and assignments).</summary>
    public ExchangeServicePrincipal SeedPointer(Domain.Identity.EntraServicePrincipal sp)
    {
        var pointer = new ExchangeServicePrincipal(sp.ObjectId.ToString(), sp.ObjectId.ToString(), sp.DisplayName, sp.AppId, sp.ObjectId);
        ServicePrincipals.Add(pointer);
        return pointer;
    }

    public ManagementScope SeedScope(string name, string filter)
    {
        var scope = new ManagementScope(name, name, Guid.NewGuid(), filter, null, "RecipientScope", false, DateTimeOffset.UtcNow.AddDays(-3));
        Scopes.Add(scope);
        return scope;
    }

    public ManagementRoleAssignment SeedAssignment(ExchangeServicePrincipal pointer, string role, string? scopeName, string? name = null)
    {
        var assignment = new ManagementRoleAssignment(
            name ?? $"{role}-{pointer.ObjectId}".Replace(' ', '-'), name ?? $"{role}-{pointer.ObjectId}", role, pointer.Name, pointer.Name, "ServicePrincipal",
            scopeName, scopeName is null ? "Organization" : "CustomRecipientScope", null, null, null, true, DateTimeOffset.UtcNow.AddDays(-3));
        Assignments.Add(assignment);
        return assignment;
    }

    public IReadOnlyList<ExchangeRecipient> Evaluate(string filter)
    {
        var (strategy, ids) = RecipientFilterAnalyzer.TryExtractExactIdentity(filter);
        if (strategy is null) return [];
        var set = ids.ToHashSet();
        return Recipients.Where(r => strategy switch
        {
            RecipientFilterStrategy.ExternalDirectoryObjectId => Guid.TryParse(r.ExternalDirectoryObjectId, out var e) && set.Contains(e),
            RecipientFilterStrategy.ObjectGuid => r.Guid is { } g && set.Contains(g),
            _ => r.ExchangeGuid is { } x && x != Guid.Empty && set.Contains(x),
        }).ToList();
    }

    private void Apply(ExchangeCommand command)
    {
        string P(string name) => command.GetString(name) ?? throw new InvalidOperationException($"Missing {name}.");
        switch (command.Name)
        {
            case ExchangeCommands.NewServicePrincipal:
            {
                var appId = Guid.Parse(P("AppId"));
                var objectId = Guid.Parse(P("ObjectId"));
                if (ServicePrincipals.Any(s => s.AppId == appId || s.ObjectId == objectId))
                    throw new StudioOperationException(FailureKind.AlreadyExists, "A service principal with this AppId already exists.");
                ServicePrincipals.Add(new ExchangeServicePrincipal(objectId.ToString(), objectId.ToString(), P("DisplayName"), appId, objectId));
                break;
            }

            case ExchangeCommands.RemoveServicePrincipal:
            {
                var identity = P("Identity");
                var pointer = ServicePrincipals.FirstOrDefault(s => s.ReferenceValues().Contains(identity, StringComparer.OrdinalIgnoreCase))
                              ?? throw new StudioOperationException(FailureKind.NotFound, $"Service principal '{identity}' couldn't be found.");
                ServicePrincipals.Remove(pointer);
                Assignments.RemoveAll(a => a.IsAssignedTo(pointer));
                break;
            }

            case ExchangeCommands.NewManagementScope:
            {
                var name = P("Name");
                if (Scopes.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new StudioOperationException(FailureKind.AlreadyExists, $"The management scope '{name}' already exists.");
                Scopes.Add(new ManagementScope(name, name, Guid.NewGuid(), P("RecipientRestrictionFilter"), null, "RecipientScope", false, DateTimeOffset.UtcNow));
                break;
            }

            case ExchangeCommands.SetManagementScope:
            {
                var index = Scopes.FindIndex(s => s.Name.Equals(P("Identity"), StringComparison.OrdinalIgnoreCase));
                if (index < 0) throw new StudioOperationException(FailureKind.NotFound, "Scope couldn't be found.");
                Scopes[index] = Scopes[index] with { RecipientFilter = P("RecipientRestrictionFilter") };
                break;
            }

            case ExchangeCommands.RemoveManagementScope:
            {
                var name = P("Identity");
                if (Assignments.Any(a => string.Equals(a.CustomResourceScope, name, StringComparison.OrdinalIgnoreCase)))
                    throw new StudioOperationException(FailureKind.Conflict, $"The management scope '{name}' is still associated with role assignments.");
                if (Scopes.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0)
                    throw new StudioOperationException(FailureKind.NotFound, "Scope couldn't be found.");
                break;
            }

            case ExchangeCommands.NewManagementRoleAssignment:
            {
                var app = P("App");
                var pointer = ServicePrincipals.FirstOrDefault(s => s.ObjectId?.ToString() == app)
                              ?? throw new StudioOperationException(FailureKind.NotFound, $"The service principal '{app}' couldn't be found.");
                var scope = P("CustomResourceScope");
                if (!Scopes.Any(s => s.Name.Equals(scope, StringComparison.OrdinalIgnoreCase)))
                    throw new StudioOperationException(FailureKind.NotFound, $"The management scope '{scope}' couldn't be found.");
                var name = P("Name");
                if (Assignments.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new StudioOperationException(FailureKind.AlreadyExists, $"The role assignment '{name}' already exists.");
                Assignments.Add(new ManagementRoleAssignment(name, name, P("Role"), pointer.Name, pointer.Name, "ServicePrincipal", scope, "CustomRecipientScope",
                    null, null, null, true, DateTimeOffset.UtcNow));
                break;
            }

            case ExchangeCommands.SetManagementRoleAssignment:
            {
                var index = Assignments.FindIndex(a => a.Name.Equals(P("Identity"), StringComparison.OrdinalIgnoreCase));
                if (index < 0) throw new StudioOperationException(FailureKind.NotFound, "Role assignment couldn't be found.");
                Assignments[index] = Assignments[index] with { CustomResourceScope = P("CustomResourceScope"), RecipientWriteScope = "CustomRecipientScope" };
                break;
            }

            case ExchangeCommands.RemoveManagementRoleAssignment:
                if (Assignments.RemoveAll(a => a.Name.Equals(P("Identity"), StringComparison.OrdinalIgnoreCase)) == 0)
                    throw new StudioOperationException(FailureKind.NotFound, "Role assignment couldn't be found.");
                break;

            default:
                throw new InvalidOperationException($"Unsupported command {command.Name}.");
        }
    }

    private Task<T> Read<T>(string operation, Func<T> read)
    {
        if (ReadFailures.TryGetValue(operation, out var error)) return Task.FromException<T>(error);
        try
        {
            return Task.FromResult(read());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    private static bool ReferencesScope(ScopeReference reference, string scopeName) => reference.ReferencingProperty.Contains(scopeName, StringComparison.OrdinalIgnoreCase)
        || reference.AssignmentName.Contains(scopeName, StringComparison.OrdinalIgnoreCase);

    private static string PermissionsOf(string role) => string.Join(", ", ApplicationRoleCatalog.Resolve(role).ApiPermissions);
}
