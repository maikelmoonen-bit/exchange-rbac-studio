using ExchangeRbacStudio.Domain.Clouds;
using ExchangeRbacStudio.Domain.Identity;

namespace ExchangeRbacStudio.Application.Abstractions;

public sealed record EntraSignInOptions(CloudEnvironmentProfile Environment, string ClientId, string? Tenant, string? LoginHint);

public sealed record EntraSignInResult(TenantIdentity Tenant, AdministratorIdentity Administrator);

/// <summary>An App Registration (application object). Used only to detect confusion with service principal IDs.</summary>
public sealed record EntraApplicationObject(Guid ObjectId, Guid AppId, string DisplayName);

/// <summary>Port for Microsoft Entra ID access through Microsoft Graph with delegated administrator permissions.</summary>
public interface IEntraDirectory
{
    bool IsSignedIn { get; }

    /// <summary>Interactive sign-in (system browser, MFA and Conditional Access capable).</summary>
    Task<EntraSignInResult> SignInAsync(EntraSignInOptions options, CancellationToken cancellationToken);

    /// <summary>Discards the in-memory credential and token cache.</summary>
    void SignOut();

    Task<IReadOnlyList<EntraServicePrincipal>> SearchServicePrincipalsAsync(string text, int maxResults, CancellationToken cancellationToken);

    Task<EntraServicePrincipal?> GetServicePrincipalByObjectIdAsync(Guid objectId, CancellationToken cancellationToken);

    Task<EntraServicePrincipal?> GetServicePrincipalByAppIdAsync(Guid appId, CancellationToken cancellationToken);

    Task<EntraApplicationObject?> GetApplicationByObjectIdAsync(Guid objectId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the application permissions actually granted to the service principal (GET /servicePrincipals/{id}/appRoleAssignments),
    /// resolved against each resource service principal's app roles.
    /// </summary>
    Task<IReadOnlyList<EntraAppPermissionGrant>> GetAppPermissionGrantsAsync(Guid servicePrincipalObjectId, CancellationToken cancellationToken);
}
