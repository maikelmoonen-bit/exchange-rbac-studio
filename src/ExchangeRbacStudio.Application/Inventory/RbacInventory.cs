using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Identity;
using ExchangeRbacStudio.Domain.Security;

namespace ExchangeRbacStudio.Application.Inventory;

public sealed record InventoryFinding(
    FindingSeverity Severity,
    string Category,
    string Subject,
    string Title,
    string Detail,
    string? Remediation = null);

/// <summary>Microsoft Entra data joined to Exchange service principal pointers.</summary>
public sealed record EntraEnrichment(
    IReadOnlyDictionary<Guid, EntraServicePrincipal?> ServicePrincipals,
    IReadOnlyDictionary<Guid, IReadOnlyList<EntraAppPermissionGrant>> Grants,
    IReadOnlyList<Guid> Failed,
    string? FailureMessage);

/// <summary>
/// Point-in-time snapshot of a tenant's application RBAC configuration. Bound to a session ID so data is never reused
/// after a tenant change or reconnection to a different tenant.
/// </summary>
public sealed record RbacInventory(
    Guid SessionId,
    Guid? TenantId,
    DateTimeOffset RetrievedUtc,
    IReadOnlyList<ExchangeServicePrincipal> ServicePrincipals,
    IReadOnlyList<ManagementScope> Scopes,
    IReadOnlyList<ApplicationRoleInfo> ApplicationRoles,
    IReadOnlyList<ManagementRoleAssignment> Assignments,
    IReadOnlyList<ApplicationAccessPolicy>? AccessPolicies,
    string? AccessPolicyError,
    IReadOnlyList<InventoryFinding> Findings,
    EntraEnrichment? Entra)
{
    public IReadOnlyList<ManagementRoleAssignment> AssignmentsFor(ExchangeServicePrincipal servicePrincipal) =>
        Assignments.Where(a => a.IsAssignedTo(servicePrincipal)).ToList();

    public IReadOnlyList<ManagementRoleAssignment> AssignmentsReferencing(ManagementScope scope) =>
        Assignments.Where(a => a.ScopeKind == AssignmentScopeKind.ManagementScope && scope.IsReferencedBy(a.ScopeReference)).ToList();

    public ExchangeServicePrincipal? ServicePrincipalFor(ManagementRoleAssignment assignment) =>
        ServicePrincipals.FirstOrDefault(assignment.IsAssignedTo);

    public ManagementScope? ScopeFor(ManagementRoleAssignment assignment) =>
        assignment.ScopeKind == AssignmentScopeKind.ManagementScope ? Scopes.FirstOrDefault(s => s.IsReferencedBy(assignment.ScopeReference)) : null;

    public ExchangeServicePrincipal? FindServicePrincipal(Guid appId) => ServicePrincipals.FirstOrDefault(sp => sp.AppId == appId);

    public int UnsafeFindingCount => Findings.Count(f => f.Severity >= FindingSeverity.High);

    public int WarningFindingCount => Findings.Count(f => f.Severity == FindingSeverity.Warning);
}
