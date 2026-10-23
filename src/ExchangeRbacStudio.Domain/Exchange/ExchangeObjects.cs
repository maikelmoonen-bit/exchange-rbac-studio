namespace ExchangeRbacStudio.Domain.Exchange;

/// <summary>
/// Exchange Online service principal object: a pointer to a Microsoft Entra service principal
/// (created with New-ServicePrincipal -AppId -ObjectId).
/// </summary>
public sealed record ExchangeServicePrincipal(
    string Identity,
    string? Name,
    string DisplayName,
    Guid? AppId,
    Guid? ObjectId)
{
    public bool Matches(Guid appId, Guid objectId) => AppId == appId && ObjectId == objectId;

    /// <summary>All identity strings Exchange may use to refer to this service principal in role assignments.</summary>
    public IEnumerable<string> ReferenceValues()
    {
        if (ObjectId is { } objectId) yield return objectId.ToString();
        if (!string.IsNullOrWhiteSpace(Identity)) yield return Identity;
        if (!string.IsNullOrWhiteSpace(Name)) yield return Name!;
    }
}

/// <summary>An Exchange management scope (Get-ManagementScope).</summary>
public sealed record ManagementScope(
    string Name,
    string Identity,
    Guid? Guid,
    string? RecipientFilter,
    string? RecipientRoot,
    string? ScopeRestrictionType,
    bool Exclusive,
    DateTimeOffset? WhenChangedUtc)
{
    public bool IsRecipientScope =>
        string.IsNullOrWhiteSpace(ScopeRestrictionType)
        || ScopeRestrictionType.Equals("RecipientScope", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when an assignment's scope reference (name, identity or GUID) refers to this scope.</summary>
    public bool IsReferencedBy(string? scopeReference)
    {
        if (string.IsNullOrWhiteSpace(scopeReference)) return false;
        var value = scopeReference.Trim();
        return value.Equals(Name, StringComparison.OrdinalIgnoreCase)
            || value.Equals(Identity, StringComparison.OrdinalIgnoreCase)
            || (Guid is { } g && System.Guid.TryParse(value, out var parsed) && parsed == g)
            || value.EndsWith("\\" + Name, StringComparison.OrdinalIgnoreCase);
    }
}

public enum AssignmentScopeKind
{
    ManagementScope,
    AdministrativeUnit,
    Group,
    Organization,
    Unknown,
}

/// <summary>A management role assignment of an application role to a service principal.</summary>
public sealed record ManagementRoleAssignment(
    string Name,
    string Identity,
    string Role,
    string? RoleAssignee,
    string? RoleAssigneeName,
    string? RoleAssigneeType,
    string? CustomResourceScope,
    string? RecipientWriteScope,
    string? CustomRecipientWriteScope,
    string? RecipientAdministrativeUnitScope,
    string? RecipientGroupScope,
    bool Enabled,
    DateTimeOffset? WhenCreatedUtc)
{
    public bool IsServicePrincipalAssignment =>
        string.Equals(RoleAssigneeType, "ServicePrincipal", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies the resource scope of the assignment. An assignment without any resource scope reference
    /// applies to the whole organization, which is the most dangerous configuration and is therefore the fallback.
    /// </summary>
    public AssignmentScopeKind ScopeKind
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(RecipientAdministrativeUnitScope)
                || string.Equals(RecipientWriteScope, "AdministrativeUnit", StringComparison.OrdinalIgnoreCase))
                return AssignmentScopeKind.AdministrativeUnit;
            if (!string.IsNullOrWhiteSpace(RecipientGroupScope)
                || string.Equals(RecipientWriteScope, "Group", StringComparison.OrdinalIgnoreCase))
                return AssignmentScopeKind.Group;
            if (!string.IsNullOrWhiteSpace(CustomResourceScope) || !string.IsNullOrWhiteSpace(CustomRecipientWriteScope))
                return AssignmentScopeKind.ManagementScope;
            if (string.IsNullOrWhiteSpace(RecipientWriteScope)
                || string.Equals(RecipientWriteScope, "Organization", StringComparison.OrdinalIgnoreCase))
                return AssignmentScopeKind.Organization;
            return AssignmentScopeKind.Unknown;
        }
    }

    /// <summary>The scope reference used by the assignment (scope name, administrative unit or group), if any.</summary>
    public string? ScopeReference =>
        FirstNonEmpty(CustomResourceScope, CustomRecipientWriteScope, RecipientAdministrativeUnitScope, RecipientGroupScope);

    public bool IsAssignedTo(ExchangeServicePrincipal servicePrincipal)
    {
        var references = servicePrincipal.ReferenceValues().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (RoleAssignee is not null && references.Contains(RoleAssignee.Trim()))
            || (RoleAssigneeName is not null && references.Contains(RoleAssigneeName.Trim()));
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}

/// <summary>A recipient returned by Get-Recipient, limited to the properties this tool needs.</summary>
public sealed record ExchangeRecipient(
    string Identity,
    string DisplayName,
    string? PrimarySmtpAddress,
    string? Alias,
    string? RecipientType,
    string? RecipientTypeDetails,
    Guid? ExchangeGuid,
    Guid? Guid,
    string? ExternalDirectoryObjectId)
{
    /// <summary>A stable key used to compare recipient identities (directory object GUID first).</summary>
    public string IdentityKey =>
        Guid is { } g && g != System.Guid.Empty ? g.ToString()
        : !string.IsNullOrWhiteSpace(ExternalDirectoryObjectId) ? ExternalDirectoryObjectId!.ToLowerInvariant()
        : ExchangeGuid is { } eg && eg != System.Guid.Empty ? eg.ToString()
        : Identity.ToLowerInvariant();

    public string Label => string.IsNullOrWhiteSpace(PrimarySmtpAddress) ? DisplayName : $"{DisplayName} <{PrimarySmtpAddress}>";
}

/// <summary>An application role discovered in the connected tenant with Get-ManagementRole.</summary>
public sealed record ApplicationRoleInfo(string Name, string? Description, string? RoleType);

public enum InScopeResult
{
    InScope,
    NotInScope,
    NotRun,
    Unknown,
}

/// <summary>One row of Test-ServicePrincipalAuthorization output.</summary>
public sealed record AuthorizationTestRow(
    string RoleName,
    string? GrantedPermissions,
    string? AllowedResourceScope,
    string? ScopeType,
    InScopeResult InScope)
{
    public static InScopeResult ParseInScope(string? value) => value?.Trim() switch
    {
        null or "" => InScopeResult.Unknown,
        var v when v.Equals("True", StringComparison.OrdinalIgnoreCase) => InScopeResult.InScope,
        var v when v.Equals("False", StringComparison.OrdinalIgnoreCase) => InScopeResult.NotInScope,
        var v when v.Replace(" ", string.Empty).Equals("NotRun", StringComparison.OrdinalIgnoreCase) => InScopeResult.NotRun,
        _ => InScopeResult.Unknown,
    };
}

/// <summary>Get-ConnectionInformation for the active Exchange Online connection.</summary>
public sealed record ExchangeConnectionInfo(
    string State,
    string? TokenStatus,
    Guid? TenantId,
    string? UserPrincipalName,
    string? Organization,
    DateTimeOffset? TokenExpiryUtc,
    string? ConnectionUri)
{
    public bool IsConnected =>
        State.Equals("Connected", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(TokenStatus, "Expired", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Legacy Application Access Policy (constrains only Microsoft Entra granted permissions).</summary>
public sealed record ApplicationAccessPolicy(string Identity, string? AppId, string? ScopeName, string? AccessRight, string? Description);
