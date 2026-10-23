using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Permissions;

namespace ExchangeRbacStudio.Domain.Authorization;

public enum AccessSource
{
    None,

    /// <summary>Exchange RBAC scope membership derived from a recipient filter preview (not yet tested).</summary>
    RbacConfigured,

    /// <summary>Exchange RBAC scope membership confirmed by Test-ServicePrincipalAuthorization.</summary>
    RbacObserved,

    /// <summary>Organization-wide Exchange RBAC assignment.</summary>
    RbacOrganizationWide,

    /// <summary>Tenant-wide Microsoft Entra application permission (independent authorization system).</summary>
    EntraTenantWide,
}

public sealed record AccessCell(string Permission, IReadOnlyList<AccessSource> Sources, string Detail)
{
    public bool HasAccess => Sources.Any(s => s != AccessSource.None);

    public string Label => !HasAccess ? "—" : string.Join(" + ", Sources.Where(s => s != AccessSource.None).Select(SourceLabel));

    public static string SourceLabel(AccessSource source) => source switch
    {
        AccessSource.RbacObserved => "RBAC (tested)",
        AccessSource.RbacConfigured => "RBAC (configured)",
        AccessSource.RbacOrganizationWide => "RBAC (org-wide)",
        AccessSource.EntraTenantWide => "Entra (tenant-wide)",
        _ => "—",
    };
}

public sealed record AccessRow(string Mailbox, bool IsAllOtherMailboxes, IReadOnlyList<AccessCell> Cells);

public sealed record EffectiveAccessMatrix(IReadOnlyList<string> Permissions, IReadOnlyList<AccessRow> Rows);

/// <summary>Input describing one Exchange application role assignment and the recipients its scope resolves to.</summary>
public sealed record AssignmentAccess(
    ManagementRoleAssignment Assignment,
    IReadOnlyList<ExchangeRecipient>? ScopeMembers);

/// <summary>
/// Builds a mailbox-by-permission matrix that keeps the two additive authorization systems apart: observed or
/// configured Exchange RBAC membership, and tenant-wide Microsoft Entra application permissions.
/// </summary>
public static class EffectiveAccessMatrixBuilder
{
    public static EffectiveAccessMatrix Build(
        IReadOnlyList<AssignmentAccess> assignments,
        IReadOnlyDictionary<string, IReadOnlyList<AuthorizationTestRow>> testedMailboxes,
        IReadOnlyDictionary<string, MailboxCapability> entraTenantWidePermissions)
    {
        var enabled = assignments.Where(a => a.Assignment.Enabled).ToList();

        var permissions = enabled.Select(a => PermissionLabel(a.Assignment.Role))
            .Concat(entraTenantWidePermissions.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mailboxes = enabled
            .SelectMany(a => a.ScopeMembers ?? [])
            .Select(r => r.Label)
            .Concat(testedMailboxes.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<AccessRow>();
        foreach (var mailbox in mailboxes)
        {
            testedMailboxes.TryGetValue(mailbox, out var testRows);
            var cells = permissions.Select(p => BuildCell(p, mailbox, enabled, testRows, entraTenantWidePermissions, isAllOthers: false)).ToList();
            rows.Add(new AccessRow(mailbox, false, cells));
        }

        rows.Add(new AccessRow("All other mailboxes", true,
            permissions.Select(p => BuildCell(p, null, enabled, null, entraTenantWidePermissions, isAllOthers: true)).ToList()));

        return new EffectiveAccessMatrix(permissions, rows);
    }

    public static string PermissionLabel(string roleName) =>
        roleName.StartsWith("Application ", StringComparison.OrdinalIgnoreCase) ? roleName["Application ".Length..] : roleName;

    private static AccessCell BuildCell(
        string permission,
        string? mailbox,
        IReadOnlyList<AssignmentAccess> assignments,
        IReadOnlyList<AuthorizationTestRow>? testRows,
        IReadOnlyDictionary<string, MailboxCapability> entra,
        bool isAllOthers)
    {
        var sources = new List<AccessSource>();
        var details = new List<string>();
        var roleAssignments = assignments.Where(a => PermissionLabel(a.Assignment.Role).Equals(permission, StringComparison.OrdinalIgnoreCase)).ToList();

        if (roleAssignments.Any(a => a.Assignment.ScopeKind == AssignmentScopeKind.Organization))
        {
            sources.Add(AccessSource.RbacOrganizationWide);
            details.Add("Organization-wide Exchange RBAC assignment.");
        }
        else if (!isAllOthers && mailbox is not null)
        {
            var observed = testRows?.Where(r => PermissionLabel(r.RoleName).Equals(permission, StringComparison.OrdinalIgnoreCase)).ToList();
            if (observed is { Count: > 0 })
            {
                var hit = observed.FirstOrDefault(r => r.InScope == InScopeResult.InScope);
                if (hit is not null)
                {
                    sources.Add(AccessSource.RbacObserved);
                    details.Add($"Test-ServicePrincipalAuthorization: in scope via '{hit.AllowedResourceScope}'.");
                }
                else
                {
                    details.Add("Test-ServicePrincipalAuthorization: not in scope.");
                }
            }
            else
            {
                var member = roleAssignments.FirstOrDefault(a => a.ScopeMembers?.Any(r => r.Label.Equals(mailbox, StringComparison.OrdinalIgnoreCase)) == true);
                if (member is not null)
                {
                    sources.Add(AccessSource.RbacConfigured);
                    details.Add($"Member of scope '{member.Assignment.ScopeReference}' according to the recipient filter preview (not tested).");
                }
            }
        }

        if (entra.TryGetValue(permission, out _))
        {
            sources.Add(AccessSource.EntraTenantWide);
            details.Add("Tenant-wide Microsoft Entra application permission (not evaluated by Exchange RBAC).");
        }
        else
        {
            var roleCaps = ApplicationRoleCatalog.TryGet("Application " + permission)?.Grants ?? MailboxCapability.None;
            var covering = entra.FirstOrDefault(e => roleCaps != MailboxCapability.None && CapabilityLattice.Covers(e.Value, roleCaps));
            if (covering.Key is not null)
            {
                sources.Add(AccessSource.EntraTenantWide);
                details.Add($"Covered by tenant-wide Microsoft Entra permission '{covering.Key}'.");
            }
        }

        if (sources.Count == 0) sources.Add(AccessSource.None);
        return new AccessCell(permission, sources, string.Join(" ", details));
    }
}
