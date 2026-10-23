using ExchangeRbacStudio.Domain.Clouds;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Identity;

namespace ExchangeRbacStudio.Application.Sessions;

public enum ServiceConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Expired,
    Failed,
}

/// <summary>
/// Immutable view of the administration session. Graph (Entra ID) and Exchange Online states are tracked separately:
/// an authenticated Graph connection never implies a valid Exchange management session.
/// </summary>
public sealed record SessionSnapshot(
    Guid SessionId,
    CloudEnvironmentProfile Environment,
    ServiceConnectionState Graph,
    string? GraphStatusMessage,
    ServiceConnectionState Exchange,
    string? ExchangeStatusMessage,
    TenantIdentity? Tenant,
    AdministratorIdentity? Administrator,
    ExchangeConnectionInfo? ExchangeConnection,
    string? ExchangeModuleVersion,
    string? PowerShellVersion,
    IReadOnlyList<string> MissingExchangeCommands,
    DateTimeOffset? LastHealthCheckUtc)
{
    public static SessionSnapshot Disconnected(CloudEnvironmentProfile environment) => new(
        Guid.NewGuid(), environment, ServiceConnectionState.Disconnected, null, ServiceConnectionState.Disconnected, null,
        null, null, null, null, null, [], null);

    public bool IsGraphConnected => Graph == ServiceConnectionState.Connected;

    public bool IsExchangeConnected => Exchange == ServiceConnectionState.Connected;

    /// <summary>False when Graph and Exchange are connected to different tenants.</summary>
    public bool TenantConsistent =>
        Tenant is null || ExchangeConnection?.TenantId is null || ExchangeConnection.TenantId == Tenant.TenantId;

    public bool IsFullyConnected => IsGraphConnected && IsExchangeConnected && TenantConsistent;

    public Guid? TenantId => Tenant?.TenantId ?? ExchangeConnection?.TenantId;

    public string TenantDisplayName => Tenant?.DisplayName ?? ExchangeConnection?.Organization ?? "Not connected";

    public string? AdministratorUpn => Administrator?.UserPrincipalName ?? ExchangeConnection?.UserPrincipalName;

    public bool CanConfigure => IsFullyConnected && MissingExchangeCommands.Count == 0;
}
