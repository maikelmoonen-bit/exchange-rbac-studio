using ExchangeRbacStudio.Domain.Identity;
using ExchangeRbacStudio.Domain.Permissions;

namespace ExchangeRbacStudio.Domain.Security;

public sealed record EntraGrantAnalysis(
    IReadOnlyList<SecurityFinding> Findings,
    MailboxCapability TenantWideCapabilities,
    IReadOnlyList<EntraAppPermissionGrant> MailboxRelevantGrants,
    IReadOnlyList<EntraAppPermissionGrant> UnderminingGrants,
    IReadOnlyList<EntraAppPermissionGrant> UnresolvedGrants)
{
    public bool UnderminesRequestedScope => UnderminingGrants.Count > 0;
}

/// <summary>
/// Evaluates the application permissions actually granted (admin-consented app role assignments) to a service
/// principal in Microsoft Entra ID. Because Entra ID grants and Exchange RBAC assignments are additive, a tenant-wide
/// Entra grant for a capability makes any Exchange RBAC mailbox scope for that capability ineffective.
/// </summary>
public static class EntraGrantAnalyzer
{
    public static EntraGrantAnalysis Analyze(IReadOnlyList<EntraAppPermissionGrant> grants, MailboxCapability requestedCapabilities)
    {
        var findings = new List<SecurityFinding>();
        var tenantWide = MailboxCapability.None;
        var relevant = new List<EntraAppPermissionGrant>();
        var undermining = new List<EntraAppPermissionGrant>();
        var unresolved = new List<EntraAppPermissionGrant>();
        var requestedExpanded = CapabilityLattice.Expand(requestedCapabilities);

        foreach (var grant in grants)
        {
            if (!grant.IsResolved)
            {
                unresolved.Add(grant);
                continue;
            }

            var definition = EntraPermissionCatalog.TryGet(grant.ResourceAppId, grant.PermissionValue);
            if (definition is null) continue;
            relevant.Add(grant);

            switch (definition.Reach)
            {
                case EntraGrantReach.TenantWideMailboxAccess:
                {
                    tenantWide |= definition.Capabilities;
                    var overlap = CapabilityLattice.Expand(definition.Capabilities) & requestedExpanded;
                    if (overlap != MailboxCapability.None)
                    {
                        undermining.Add(grant);
                        findings.Add(new SecurityFinding(
                            "ENTRA_TENANT_WIDE_OVERLAP",
                            FindingSeverity.Critical,
                            $"Tenant-wide Entra ID permission {grant.PermissionValue} defeats the mailbox scope",
                            $"The service principal holds the admin-consented application permission '{grant.PermissionValue}' on {grant.ResourceDisplayName}. "
                            + "Microsoft Entra ID grants and Exchange RBAC for Applications are additive, so the application can already "
                            + $"{string.Join(", ", CapabilityLattice.Enumerate(overlap).Select(CapabilityLattice.Describe)).ToLowerInvariant()} in every mailbox in the tenant. "
                            + "The Exchange management scope cannot restrict this.",
                            $"After the scoped Exchange RBAC assignment is verified, remove the '{grant.PermissionValue}' application permission from the Enterprise Application "
                            + "(Microsoft Entra admin center > Enterprise applications > the app > Permissions, or Remove-MgServicePrincipalAppRoleAssignment). "
                            + "Exchange RBAC Studio never revokes Entra permissions automatically.",
                            UnderminesScoping: true,
                            Subject: grant.PermissionValue));
                    }
                    else
                    {
                        findings.Add(new SecurityFinding(
                            "ENTRA_TENANT_WIDE_OTHER",
                            FindingSeverity.High,
                            $"Tenant-wide Entra ID permission {grant.PermissionValue}",
                            $"The application can {CapabilityLattice.Describe(definition.Capabilities).ToLowerInvariant()} in every mailbox through '{grant.PermissionValue}' on {grant.ResourceDisplayName}. "
                            + "This is unrelated to the roles being configured but means the application is not limited to the selected mailboxes.",
                            "Replace the tenant-wide grant with a scoped Exchange RBAC assignment if the application only needs access to specific mailboxes.",
                            Subject: grant.PermissionValue));
                    }

                    break;
                }

                case EntraGrantReach.Administrative:
                    findings.Add(new SecurityFinding(
                        definition.Capabilities.HasFlag(MailboxCapability.ExchangeAdministration) ? "ENTRA_EXCHANGE_ADMIN" : "ENTRA_PRIVILEGE_ESCALATION",
                        FindingSeverity.Critical,
                        $"Privileged Entra ID permission {grant.PermissionValue}",
                        definition.Explanation + " An application with this permission can remove or bypass the mailbox restriction configured here.",
                        $"Review whether '{grant.PermissionValue}' is required. If not, remove it from the Enterprise Application.",
                        UnderminesScoping: true,
                        Subject: grant.PermissionValue));
                    break;

                case EntraGrantReach.RequiresExchangeMailboxPermission:
                    findings.Add(new SecurityFinding(
                        "ENTRA_PROTOCOL_PERMISSION",
                        FindingSeverity.Info,
                        $"Protocol permission {grant.PermissionValue}",
                        definition.Explanation,
                        "Review mailbox permissions granted to the service principal (Get-MailboxPermission / Get-RecipientPermission).",
                        Subject: grant.PermissionValue));
                    break;
            }
        }

        if (unresolved.Count > 0)
        {
            findings.Add(new SecurityFinding(
                "ENTRA_UNRESOLVED_GRANTS",
                FindingSeverity.Warning,
                $"{unresolved.Count} application permission(s) could not be resolved",
                "Some app role assignments reference app roles that could not be resolved on the resource service principal. Their effect on mailbox access is unknown: "
                + string.Join(", ", unresolved.Select(u => u.Label)),
                "Inspect the Enterprise Application's permissions in the Microsoft Entra admin center."));
        }

        return new EntraGrantAnalysis(findings, tenantWide, relevant, undermining, unresolved);
    }
}
