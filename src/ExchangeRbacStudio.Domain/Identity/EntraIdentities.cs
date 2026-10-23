using ExchangeRbacStudio.Domain.Clouds;

namespace ExchangeRbacStudio.Domain.Identity;

/// <summary>The tenant an administration session is bound to.</summary>
public sealed record TenantIdentity(Guid TenantId, string DisplayName, string? DefaultDomain, CloudEnvironment Environment);

/// <summary>The signed-in administrator as reported by Microsoft Graph (/me).</summary>
public sealed record AdministratorIdentity(string UserPrincipalName, string? DisplayName, Guid? ObjectId);

/// <summary>
/// An Enterprise Application (service principal object) in Microsoft Entra ID.
/// <para>
/// <see cref="ObjectId"/> is the service principal's object ID ("Object ID" on the Enterprise applications blade),
/// never the App Registration (application object) ID, which is a different object with a different GUID.
/// </para>
/// </summary>
public sealed record EntraServicePrincipal(
    Guid ObjectId,
    Guid AppId,
    string DisplayName,
    string? ServicePrincipalType,
    bool? AccountEnabled,
    Guid? AppOwnerOrganizationId,
    IReadOnlyList<string> Tags,
    string? PublisherName)
{
    /// <summary>Tenant that owns Microsoft first-party applications.</summary>
    public static readonly Guid MicrosoftServicesTenantId = Guid.Parse("f8cdef31-a31e-4b4a-93e4-5f571e91255a");

    /// <summary>Microsoft corporate tenant, which also owns some first-party applications.</summary>
    public static readonly Guid MicrosoftCorporateTenantId = Guid.Parse("72f988bf-86f1-41af-91ab-2d7cd011db47");

    public bool IsMicrosoftFirstParty =>
        AppOwnerOrganizationId == MicrosoftServicesTenantId || AppOwnerOrganizationId == MicrosoftCorporateTenantId;

    public bool IsManagedIdentity => string.Equals(ServicePrincipalType, "ManagedIdentity", StringComparison.OrdinalIgnoreCase);

    public bool IsEnterpriseApplication =>
        Tags.Any(t => string.Equals(t, "WindowsAzureActiveDirectoryIntegratedApp", StringComparison.OrdinalIgnoreCase));

    public bool IsOwnedBy(Guid tenantId) => AppOwnerOrganizationId == tenantId;
}

/// <summary>
/// An application permission (app role assignment) actually granted to a service principal, i.e. admin-consented.
/// Resolved against the resource service principal's appRoles so the permission name is known.
/// </summary>
public sealed record EntraAppPermissionGrant(
    Guid AssignmentId,
    Guid ResourceServicePrincipalId,
    Guid? ResourceAppId,
    string ResourceDisplayName,
    Guid AppRoleId,
    string? PermissionValue,
    string? PermissionDisplayName,
    DateTimeOffset? GrantedUtc)
{
    public bool IsResolved => !string.IsNullOrWhiteSpace(PermissionValue);

    public string Label => IsResolved ? $"{ResourceDisplayName}: {PermissionValue}" : $"{ResourceDisplayName}: unresolved app role {AppRoleId}";
}

/// <summary>Well-known resource application IDs (identical across Microsoft national clouds).</summary>
public static class WellKnownResourceApps
{
    public static readonly Guid MicrosoftGraph = Guid.Parse("00000003-0000-0000-c000-000000000000");
    public static readonly Guid Office365ExchangeOnline = Guid.Parse("00000002-0000-0ff1-ce00-000000000000");
}
