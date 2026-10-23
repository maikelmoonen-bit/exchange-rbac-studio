using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Planning;

namespace ExchangeRbacStudio.Application.Abstractions;

public sealed record ExchangeHostInfo(string PowerShellVersion, string PowerShellEdition, int ProcessId, IReadOnlyList<string> AvailableModuleVersions);

public sealed record ExchangeModuleInfo(string Version, string? ModuleBase);

public sealed record ExchangeConnectOptions(
    string? UserPrincipalName,
    string ExchangeEnvironmentName,
    bool UseWebAccountManager,
    IReadOnlyList<string> CommandNames);

public sealed record RecipientPreviewResult(IReadOnlyList<ExchangeRecipient> Recipients, bool Truncated);

public sealed record ExchangeCommandResult(IReadOnlyList<string> Warnings, int RecordCount, TimeSpan Duration);

/// <summary>A role assignment (of any assignee type) that references a management scope.</summary>
public sealed record ScopeReference(string AssignmentName, string Role, string? RoleAssigneeName, string? RoleAssigneeType, string ReferencingProperty);

public sealed class ExchangeSessionProblemEventArgs(StudioOperationException error) : EventArgs
{
    public StudioOperationException Error { get; } = error;
}

/// <summary>
/// Port for Exchange Online administration. Implemented by an isolated, persistent PowerShell 7 host running the
/// ExchangeOnlineManagement module. Read methods may be retried; <see cref="ExecuteAsync"/> never is.
/// </summary>
public interface IExchangeRbacGateway : IAsyncDisposable
{
    /// <summary>Raised when an operation reveals that the Exchange session is no longer usable (expired, disconnected, host exited).</summary>
    event EventHandler<ExchangeSessionProblemEventArgs>? SessionProblemDetected;

    bool IsHostRunning { get; }

    Task<ExchangeHostInfo> StartAsync(CancellationToken cancellationToken);

    Task<ExchangeModuleInfo> ImportModuleAsync(Version minimumVersion, CancellationToken cancellationToken);

    Task<ExchangeConnectionInfo> ConnectAsync(ExchangeConnectOptions options, CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    Task<ExchangeConnectionInfo?> GetConnectionInfoAsync(CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, bool>> GetCommandAvailabilityAsync(IReadOnlyList<string> commandNames, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExchangeServicePrincipal>> GetServicePrincipalsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ManagementScope>> GetManagementScopesAsync(CancellationToken cancellationToken);

    Task<ManagementScope?> GetManagementScopeAsync(string identity, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApplicationRoleInfo>> GetApplicationRolesAsync(CancellationToken cancellationToken);

    /// <summary>Role assignments of the given application roles whose assignee is a service principal.</summary>
    Task<IReadOnlyList<ManagementRoleAssignment>> GetApplicationRoleAssignmentsAsync(IReadOnlyList<string> roleNames, CancellationToken cancellationToken);

    /// <summary>All role assignments in the organization (administrators and applications) that reference the scope.</summary>
    Task<IReadOnlyList<ScopeReference>> GetScopeReferencesAsync(string scopeName, CancellationToken cancellationToken);

    Task<IReadOnlyList<ExchangeRecipient>> SearchRecipientsAsync(string text, int maxResults, CancellationToken cancellationToken);

    Task<ExchangeRecipient?> GetRecipientAsync(string identity, CancellationToken cancellationToken);

    /// <summary>Resolves the recipients an OPATH recipient filter matches (Get-Recipient -RecipientPreviewFilter).</summary>
    Task<RecipientPreviewResult> PreviewRecipientFilterAsync(string filter, int maxResults, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuthorizationTestRow>> TestServicePrincipalAuthorizationAsync(string servicePrincipalIdentity, string resource, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApplicationAccessPolicy>> GetApplicationAccessPoliciesAsync(CancellationToken cancellationToken);

    /// <summary>Executes an allowlisted write command exactly once. Never retried automatically.</summary>
    Task<ExchangeCommandResult> ExecuteAsync(ExchangeCommand command, CancellationToken cancellationToken);
}
