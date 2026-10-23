using ExchangeRbacStudio.Application.Inventory;
using ExchangeRbacStudio.Domain.Permissions;
using ExchangeRbacStudio.Domain.Security;

namespace ExchangeRbacStudio.Application.Permissions;

public sealed record SelectionAdvice(FindingSeverity Severity, string Title, string Detail);

/// <summary>Provides the application roles available in the connected tenant and least-privilege advice for a selection.</summary>
public sealed class RoleCatalogService(RbacInventoryService inventory)
{
    public async Task<IReadOnlyList<ApplicationRoleDefinition>> GetRolesAsync(CancellationToken cancellationToken)
    {
        var current = await inventory.GetAsync(cancellationToken).ConfigureAwait(false);
        return ApplicationRoleCatalog.Merge(current.ApplicationRoles);
    }

    public static IReadOnlyList<SelectionAdvice> Evaluate(IReadOnlyList<string> selectedRoles)
    {
        var advice = new List<SelectionAdvice>();
        var definitions = selectedRoles.Select(r => ApplicationRoleCatalog.Resolve(r)).ToList();
        var names = selectedRoles.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions.Where(d => d.Privilege == PrivilegeLevel.Critical))
            advice.Add(new(FindingSeverity.Critical, $"{definition.RoleName} is highly privileged", definition.SecurityImplications));

        foreach (var definition in definitions.Where(d => !d.IsCurated))
            advice.Add(new(FindingSeverity.Warning, $"{definition.RoleName} is not described by this release",
                "Review the role in Microsoft documentation before assigning it, and treat it as privileged."));

        var capabilities = ApplicationRoleCatalog.CapabilitiesOf(selectedRoles);
        var expanded = CapabilityLattice.Expand(capabilities);

        if (expanded.HasFlag(MailboxCapability.MailWrite) && !expanded.HasFlag(MailboxCapability.MailSend))
            advice.Add(new(FindingSeverity.Info, "Mail.ReadWrite does not include Mail.Send",
                "The selected roles allow creating and changing messages but not sending them. If the application must send mail, add Application Mail.Send explicitly; otherwise this is the least-privileged choice."));

        if (expanded.HasFlag(MailboxCapability.MailWrite) && (expanded.HasFlag(MailboxCapability.MailSend) || expanded.HasFlag(MailboxCapability.SmtpSend)))
            advice.Add(new(FindingSeverity.High, "Write and send combined",
                "The application will be able to both alter mailbox content and send mail as the mailbox owners. A compromise of this application could be used for convincing impersonation and to hide its traces."));

        if ((expanded & (MailboxCapability.MailboxItemExport | MailboxCapability.EwsFullAccess)) != 0)
            advice.Add(new(FindingSeverity.High, "Bulk data extraction possible",
                "The selection allows complete extraction of mailbox content. Make sure the application's credentials are protected accordingly (certificate credentials, Conditional Access for workload identities)."));

        foreach (var definition in definitions)
        {
            var coveredBy = definitions.FirstOrDefault(other => !ReferenceEquals(other, definition)
                && other.IsCurated && definition.IsCurated
                && !other.RoleName.Equals(definition.RoleName, StringComparison.OrdinalIgnoreCase)
                && CapabilityLattice.Covers(other.Grants, definition.Grants));
            if (coveredBy is not null)
                advice.Add(new(FindingSeverity.Warning, $"{definition.RoleName} is redundant",
                    $"All capabilities of {definition.RoleName} are already included in {coveredBy.RoleName}. Remove the redundant role, or replace the broader role with the narrower one if that is sufficient."));
        }

        if (names.Contains("Application Mail.Read") && !names.Contains("Application Mail.ReadBasic") && names.Count == 1)
            advice.Add(new(FindingSeverity.Info, "Consider Mail.ReadBasic",
                "If the application only needs senders, subjects and timestamps, Application Mail.ReadBasic avoids access to message bodies and attachments."));

        return advice.DistinctBy(a => a.Title).OrderByDescending(a => a.Severity).ToList();
    }
}
