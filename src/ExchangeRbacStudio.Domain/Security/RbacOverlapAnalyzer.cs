using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Permissions;

namespace ExchangeRbacStudio.Domain.Security;

public sealed record RbacOverlapAnalysis(
    IReadOnlyList<ManagementRoleAssignment> ExistingOnIntendedScope,
    IReadOnlyList<ManagementRoleAssignment> OrganizationWide,
    IReadOnlyList<ManagementRoleAssignment> OtherScopes,
    IReadOnlyList<SecurityFinding> Findings)
{
    public bool UnderminesRequestedScope => OrganizationWide.Count > 0;
}

/// <summary>
/// Compares a service principal's existing Exchange application role assignments with the requested roles to detect
/// duplicates (idempotent reuse), organization-wide assignments that defeat the requested scope, and overlapping
/// assignments on other scopes that widen the application's effective mailbox set.
/// </summary>
public static class RbacOverlapAnalyzer
{
    public static RbacOverlapAnalysis Analyze(
        IReadOnlyList<ManagementRoleAssignment> servicePrincipalAssignments,
        IReadOnlyList<string> requestedRoles,
        ManagementScope? intendedScope,
        string intendedScopeName)
    {
        var onIntended = new List<ManagementRoleAssignment>();
        var orgWide = new List<ManagementRoleAssignment>();
        var otherScopes = new List<ManagementRoleAssignment>();
        var findings = new List<SecurityFinding>();

        foreach (var assignment in servicePrincipalAssignments)
        {
            var overlapsRequest = requestedRoles.Any(r => ApplicationRoleCatalog.Overlaps(assignment.Role, r));
            if (!overlapsRequest) continue;

            var targetsIntendedScope = assignment.ScopeKind == AssignmentScopeKind.ManagementScope
                && (intendedScope?.IsReferencedBy(assignment.ScopeReference)
                    ?? string.Equals(assignment.ScopeReference, intendedScopeName, StringComparison.OrdinalIgnoreCase));

            if (targetsIntendedScope)
            {
                onIntended.Add(assignment);
                continue;
            }

            if (!assignment.Enabled)
            {
                findings.Add(new SecurityFinding(
                    "RBAC_DISABLED_OVERLAP",
                    FindingSeverity.Info,
                    $"Disabled assignment {assignment.Name}",
                    $"A disabled assignment of '{assignment.Role}' exists for this application. It does not grant access while disabled, but re-enabling it would.",
                    Subject: assignment.Name));
                continue;
            }

            if (assignment.ScopeKind == AssignmentScopeKind.Organization)
            {
                orgWide.Add(assignment);
                findings.Add(new SecurityFinding(
                    "RBAC_ORG_WIDE",
                    FindingSeverity.Critical,
                    $"Organization-wide assignment of {assignment.Role}",
                    $"Role assignment '{assignment.Name}' grants '{assignment.Role}' without a resource scope, which applies to every mailbox. "
                    + "It overlaps with the requested roles, so the new mailbox scope would not restrict the application.",
                    $"Remove or rescope '{assignment.Name}' (Role Assignments page) after confirming the application does not rely on organization-wide access.",
                    UnderminesScoping: true,
                    Subject: assignment.Name));
            }
            else
            {
                otherScopes.Add(assignment);
                var broader = requestedRoles.Any(r => !assignment.Role.Equals(r, StringComparison.OrdinalIgnoreCase)
                    && ApplicationRoleCatalog.IsEquivalentOrBroader(assignment.Role, r));
                findings.Add(new SecurityFinding(
                    "RBAC_OTHER_SCOPE",
                    FindingSeverity.Warning,
                    $"Overlapping assignment {assignment.Name} on {assignment.ScopeReference ?? assignment.ScopeKind.ToString()}",
                    $"The application already holds '{assignment.Role}'{(broader ? " (equivalent or broader)" : string.Empty)} on scope '{assignment.ScopeReference}'. "
                    + "Exchange RBAC assignments are additive: the application's effective access is the union of all its scopes.",
                    "Confirm the additional scope is intended, or consolidate the assignments.",
                    Subject: assignment.Name));
            }
        }

        return new RbacOverlapAnalysis(onIntended, orgWide, otherScopes, findings);
    }
}
