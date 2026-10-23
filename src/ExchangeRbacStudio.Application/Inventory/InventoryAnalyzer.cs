using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Permissions;
using ExchangeRbacStudio.Domain.Scoping;
using ExchangeRbacStudio.Domain.Security;

namespace ExchangeRbacStudio.Application.Inventory;

/// <summary>Derives potentially unsafe conditions from retrieved RBAC data. Every finding is based on observed data.</summary>
public static class InventoryAnalyzer
{
    public static IReadOnlyList<InventoryFinding> Analyze(
        IReadOnlyList<ExchangeServicePrincipal> servicePrincipals,
        IReadOnlyList<ManagementScope> scopes,
        IReadOnlyList<ManagementRoleAssignment> assignments,
        IReadOnlyList<ApplicationAccessPolicy>? accessPolicies,
        EntraEnrichment? entra)
    {
        var findings = new List<InventoryFinding>();

        foreach (var assignment in assignments.Where(a => a.Enabled))
        {
            var sp = servicePrincipals.FirstOrDefault(assignment.IsAssignedTo);
            var app = sp?.DisplayName ?? assignment.RoleAssigneeName ?? assignment.RoleAssignee ?? "unknown application";

            switch (assignment.ScopeKind)
            {
                case AssignmentScopeKind.Organization:
                    findings.Add(new InventoryFinding(FindingSeverity.High, "Organization-wide assignment", assignment.Name,
                        $"{app}: {assignment.Role} applies to every mailbox",
                        $"Role assignment '{assignment.Name}' has no resource scope, so '{assignment.Role}' applies to all mailboxes in the organization.",
                        "Rescope the assignment to a management scope or administrative unit, or remove it if organization-wide access is not required."));
                    break;
                case AssignmentScopeKind.ManagementScope when !scopes.Any(s => s.IsReferencedBy(assignment.ScopeReference)):
                    findings.Add(new InventoryFinding(FindingSeverity.Warning, "Unresolved scope", assignment.Name,
                        $"{app}: scope '{assignment.ScopeReference}' could not be resolved",
                        $"Role assignment '{assignment.Name}' references '{assignment.ScopeReference}', which is not a management scope returned by Get-ManagementScope. It may be an administrative unit or a scope that no longer exists.",
                        "Inspect the assignment with Test-ServicePrincipalAuthorization to see the effective scope type."));
                    break;
                case AssignmentScopeKind.Unknown:
                    findings.Add(new InventoryFinding(FindingSeverity.Warning, "Unknown scope type", assignment.Name,
                        $"{app}: scope of '{assignment.Name}' could not be classified",
                        $"The resource scope of role assignment '{assignment.Name}' (RecipientWriteScope '{assignment.RecipientWriteScope}') could not be classified.",
                        "Use the Authorization Tester to inspect the effective scope."));
                    break;
            }
        }

        var appScopes = scopes.Where(s => assignments.Any(a => a.ScopeKind == AssignmentScopeKind.ManagementScope && s.IsReferencedBy(a.ScopeReference))).ToList();
        foreach (var scope in appScopes)
        {
            var analysis = RecipientFilterAnalyzer.Analyze(scope.RecipientFilter);
            foreach (var flag in analysis.Flags.Where(f => f.Risk is FilterRisk.High or FilterRisk.Moderate or FilterRisk.Unknown))
            {
                findings.Add(new InventoryFinding(
                    flag.Risk == FilterRisk.High ? FindingSeverity.High : FindingSeverity.Warning,
                    "Scope filter", scope.Name,
                    $"Scope '{scope.Name}': {FlagTitle(flag.Code)}",
                    flag.Description,
                    "Prefer exact-identity scopes generated from selected mailboxes, or confirm that the people who control the filtered attributes or groups are trusted."));
            }

            if (!string.IsNullOrWhiteSpace(scope.RecipientRoot))
            {
                findings.Add(new InventoryFinding(FindingSeverity.Info, "Scope filter", scope.Name,
                    $"Scope '{scope.Name}' uses a recipient root",
                    $"The scope is limited to recipients under '{scope.RecipientRoot}'. Recipient previews do not apply the root, so member counts may be higher than the effective scope."));
            }
        }

        foreach (var sp in servicePrincipals)
        {
            if (!assignments.Any(a => a.IsAssignedTo(sp)))
            {
                findings.Add(new InventoryFinding(FindingSeverity.Info, "Unused pointer", sp.DisplayName,
                    $"{sp.DisplayName} has no application role assignments",
                    "The Exchange service principal pointer exists but no application roles are assigned to it."));
            }
        }

        if (entra is null)
        {
            findings.Add(new InventoryFinding(FindingSeverity.Info, "Entra ID", "Microsoft Entra ID",
                "Microsoft Entra ID permissions were not analyzed",
                "Connect to Microsoft Entra ID to detect tenant-wide application permissions that bypass Exchange RBAC scopes."));
        }
        else
        {
            AnalyzeEntra(servicePrincipals, assignments, entra, findings);
        }

        if (accessPolicies is { Count: > 0 })
        {
            findings.Add(new InventoryFinding(FindingSeverity.Info, "Application access policies", "Organization",
                $"{accessPolicies.Count} legacy Application Access Policies exist",
                "Application Access Policies constrain only Microsoft Entra ID granted permissions and are replaced by RBAC for Applications. They do not restrict Exchange RBAC assignments.",
                "Plan migration to RBAC for Applications as documented by Microsoft."));
        }

        return findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Subject, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AnalyzeEntra(IReadOnlyList<ExchangeServicePrincipal> servicePrincipals, IReadOnlyList<ManagementRoleAssignment> assignments, EntraEnrichment entra, List<InventoryFinding> findings)
    {
        if (entra.Failed.Count > 0)
        {
            findings.Add(new InventoryFinding(FindingSeverity.Warning, "Entra ID", "Microsoft Entra ID",
                $"Entra ID permissions could not be verified for {entra.Failed.Count} application(s)",
                entra.FailureMessage ?? "Some Microsoft Graph requests failed. Tenant-wide grants for these applications are unknown.",
                "Check that your account can read application role assignments (for example Directory Readers or Global Reader), then refresh."));
        }

        foreach (var sp in servicePrincipals)
        {
            if (sp.ObjectId is not { } objectId || entra.Failed.Contains(objectId)) continue;

            if (entra.ServicePrincipals.TryGetValue(objectId, out var entraSp))
            {
                if (entraSp is null)
                {
                    findings.Add(new InventoryFinding(FindingSeverity.Warning, "Pointer integrity", sp.DisplayName,
                        $"{sp.DisplayName}: no matching Entra service principal",
                        $"The Exchange pointer references service principal object ID {objectId}, which was not found in Microsoft Entra ID.",
                        "Verify the Enterprise Application. Exchange normally removes pointers of deleted service principals automatically."));
                    continue;
                }

                if (sp.AppId is { } appId && appId != entraSp.AppId)
                {
                    findings.Add(new InventoryFinding(FindingSeverity.High, "Pointer integrity", sp.DisplayName,
                        $"{sp.DisplayName}: AppId mismatch",
                        $"The Exchange pointer has AppId {appId} but the Entra service principal {objectId} has AppId {entraSp.AppId}.",
                        "Remove and recreate the pointer with the correct identifiers."));
                }
            }

            if (!entra.Grants.TryGetValue(objectId, out var grants)) continue;
            var assigned = assignments.Where(a => a.Enabled && a.IsAssignedTo(sp)).Select(a => a.Role).ToList();
            var analysis = EntraGrantAnalyzer.Analyze(grants, ApplicationRoleCatalog.CapabilitiesOf(assigned));
            foreach (var finding in analysis.Findings.Where(f => f.Severity >= FindingSeverity.High))
            {
                var severity = finding.UnderminesScoping && assigned.Count > 0 ? FindingSeverity.Critical : finding.Severity;
                findings.Add(new InventoryFinding(severity, "Entra ID", sp.DisplayName, $"{sp.DisplayName}: {finding.Title}", finding.Detail, finding.Remediation));
            }
        }
    }

    private static string FlagTitle(string code) => code switch
    {
        "EMPTY_FILTER" => "no recipient filter",
        "UNPARSEABLE" => "filter could not be analyzed",
        "NEGATION" => "negative filter",
        "WILDCARD" => "wildcard filter",
        "NULL_COMPARISON" => "$null comparison",
        "GROUP_MEMBERSHIP" => "group-controlled membership",
        "ADMIN_UNIT" => "administrative-unit-controlled membership",
        "MUTABLE_ATTRIBUTE" => "attribute-based filter",
        "ADDRESS_MATCH" => "email address filter",
        "TYPE_ONLY" => "recipient-type-only filter",
        _ => code,
    };
}
