using ExchangeRbacStudio.Domain.Exchange;

namespace ExchangeRbacStudio.Domain.Permissions;

public enum PrivilegeLevel
{
    Low,
    Moderate,
    High,
    Critical,
}

public enum RoleCategory
{
    Mail,
    Calendars,
    Contacts,
    MailboxSettings,
    MailboxFoldersAndItems,
    Protocols,
    FullAccessBundles,
    Other,
}

/// <summary>
/// Metadata for an Exchange Online application role ("Application Mail.Read", ...).
/// <see cref="IsCurated"/> is false for roles that exist in the tenant but are unknown to this release;
/// such roles are presented conservatively as privileged.
/// </summary>
public sealed record ApplicationRoleDefinition(
    string RoleName,
    string Title,
    RoleCategory Category,
    string Protocol,
    IReadOnlyList<string> ApiPermissions,
    string Explanation,
    string SecurityImplications,
    PrivilegeLevel Privilege,
    MailboxCapability Grants,
    bool IsCurated = true,
    bool IsAvailableInTenant = true)
{
    public MailboxCapability ExpandedGrants => CapabilityLattice.Expand(Grants);

    public IReadOnlyList<string> EffectiveCapabilities =>
        CapabilityLattice.Enumerate(ExpandedGrants).Select(CapabilityLattice.Describe).ToList();

    /// <summary>Short token used in generated object names, e.g. MAIL or CALENDARS.</summary>
    public string NameToken => Category switch
    {
        RoleCategory.Mail => "MAIL",
        RoleCategory.Calendars => "CALENDARS",
        RoleCategory.Contacts => "CONTACTS",
        RoleCategory.MailboxSettings => "SETTINGS",
        RoleCategory.MailboxFoldersAndItems => "ITEMS",
        RoleCategory.Protocols => "PROTOCOL",
        RoleCategory.FullAccessBundles => "FULL",
        _ => "APP",
    };
}

/// <summary>
/// Curated catalog of Exchange Online application roles, based on the "Supported Application Roles" table in
/// https://learn.microsoft.com/exchange/permissions-exo/application-rbac. The roles actually offered to the
/// administrator are always intersected with the roles discovered in the connected tenant (Get-ManagementRole).
/// </summary>
public static class ApplicationRoleCatalog
{
    private static readonly ApplicationRoleDefinition[] CuratedRoles =
    [
        new("Application Mail.ReadBasic", "Read basic mail properties", RoleCategory.Mail, "Microsoft Graph", ["Mail.ReadBasic"],
            "Read email in the scoped mailboxes except the body, previewBody, attachments and extended properties.",
            "Exposes senders, recipients, subjects and timestamps. Prefer this over Mail.Read when message content is not needed.",
            PrivilegeLevel.Moderate, MailboxCapability.MailReadBasic),
        new("Application Mail.Read", "Read mail", RoleCategory.Mail, "Microsoft Graph", ["Mail.Read"],
            "Read email in the scoped mailboxes, including bodies and attachments.",
            "Full read access to message content. Treat the application's credentials as sensitive as the mailboxes it can read.",
            PrivilegeLevel.High, MailboxCapability.MailRead),
        new("Application Mail.ReadWrite", "Read and write mail", RoleCategory.Mail, "Microsoft Graph", ["Mail.ReadWrite"],
            "Create, read, update and delete email in the scoped mailboxes. Does not include permission to send mail.",
            "Can alter or delete evidence and move messages. Mail.ReadWrite does NOT grant Mail.Send; sending requires a separate role.",
            PrivilegeLevel.High, MailboxCapability.MailWrite),
        new("Application Mail.Send", "Send mail", RoleCategory.Mail, "Microsoft Graph", ["Mail.Send"],
            "Send mail as any of the scoped mailboxes.",
            "Messages are sent as the mailbox owner, which enables impersonation and phishing from trusted addresses if the app is compromised.",
            PrivilegeLevel.High, MailboxCapability.MailSend),
        new("Application Mail-Advanced.ReadWrite.All", "Advanced mail read/write", RoleCategory.Mail, "Microsoft Graph", ["Mail-Advanced.ReadWrite.All"],
            "Create, read, update and delete all email, including the contents of non-draft (sent or received) messages. Does not include permission to send mail.",
            "Can modify the content of delivered messages, which undermines the integrity of mail records. Use only for specialised scenarios.",
            PrivilegeLevel.Critical, MailboxCapability.MailAdvancedWrite),
        new("Application MailTips.ReadBasic.All", "Read MailTips", RoleCategory.Mail, "Microsoft Graph", ["MailTips.ReadBasic.All"],
            "Read MailTips metadata (for example automatic replies status) without exposing email content.",
            "Low impact metadata access.",
            PrivilegeLevel.Low, MailboxCapability.MailTipsRead),

        new("Application Calendars.Read", "Read calendars", RoleCategory.Calendars, "Microsoft Graph", ["Calendars.Read"],
            "Read events of calendars in the scoped mailboxes.",
            "Exposes meeting subjects, attendees, locations and bodies, which can be sensitive.",
            PrivilegeLevel.Moderate, MailboxCapability.CalendarsRead),
        new("Application Calendars.ReadWrite", "Read and write calendars", RoleCategory.Calendars, "Microsoft Graph", ["Calendars.ReadWrite"],
            "Create, read, update and delete events of calendars in the scoped mailboxes.",
            "Can create, change or cancel meetings on behalf of the mailbox owner.",
            PrivilegeLevel.High, MailboxCapability.CalendarsWrite),

        new("Application Contacts.Read", "Read contacts", RoleCategory.Contacts, "Microsoft Graph", ["Contacts.Read"],
            "Read all contacts in the scoped mailboxes.",
            "Exposes personal contact data of the mailbox owners and their correspondents.",
            PrivilegeLevel.Moderate, MailboxCapability.ContactsRead),
        new("Application Contacts.ReadWrite", "Read and write contacts", RoleCategory.Contacts, "Microsoft Graph", ["Contacts.ReadWrite"],
            "Create, read, update and delete all contacts in the scoped mailboxes.",
            "Can alter or remove contact data.",
            PrivilegeLevel.Moderate, MailboxCapability.ContactsWrite),

        new("Application MailboxSettings.Read", "Read mailbox settings", RoleCategory.MailboxSettings, "Microsoft Graph", ["MailboxSettings.Read"],
            "Read mailbox settings (automatic replies, time zone, locale, working hours) in the scoped mailboxes.",
            "Low impact configuration data.",
            PrivilegeLevel.Low, MailboxCapability.MailboxSettingsRead),
        new("Application MailboxSettings.ReadWrite", "Read and write mailbox settings", RoleCategory.MailboxSettings, "Microsoft Graph", ["MailboxSettings.ReadWrite"],
            "Create, read, update and delete mailbox settings in the scoped mailboxes.",
            "Can change automatic replies and other settings visible to correspondents.",
            PrivilegeLevel.Moderate, MailboxCapability.MailboxSettingsWrite),

        new("Application MailboxFolder.Read", "Read mailbox folders", RoleCategory.MailboxFoldersAndItems, "Microsoft Graph", ["MailboxFolder.Read.All"],
            "Read the mailbox folders of the scoped mailboxes.",
            "Exposes folder structure and names.",
            PrivilegeLevel.Moderate, MailboxCapability.MailboxFolderRead),
        new("Application MailboxFolder.ReadWrite", "Read and write mailbox folders", RoleCategory.MailboxFoldersAndItems, "Microsoft Graph", ["MailboxFolder.ReadWrite.All"],
            "Read and write the mailbox folders of the scoped mailboxes.",
            "Can reorganise or remove folders.",
            PrivilegeLevel.High, MailboxCapability.MailboxFolderWrite),
        new("Application MailboxItem.Read", "Read mailbox items", RoleCategory.MailboxFoldersAndItems, "Microsoft Graph", ["MailboxItem.Read.All"],
            "Read all mailbox items (messages, events, contacts and other item types) in the scoped mailboxes.",
            "Broad read access across every item type in the mailbox.",
            PrivilegeLevel.High, MailboxCapability.MailboxItemRead),
        new("Application MailboxItem.ReadWrite", "Read and write mailbox items", RoleCategory.MailboxFoldersAndItems, "Microsoft Graph", ["MailboxItem.ReadWrite.All"],
            "Read, write and delete all mailbox items in the scoped mailboxes.",
            "Broad write access across every item type in the mailbox.",
            PrivilegeLevel.Critical, MailboxCapability.MailboxItemWrite),
        new("Application MailboxItem.Export", "Export mailbox items", RoleCategory.MailboxFoldersAndItems, "Microsoft Graph", ["MailboxItem.Export.All"],
            "Export all mailbox items of the scoped mailboxes.",
            "Enables bulk extraction of complete mailbox content.",
            PrivilegeLevel.Critical, MailboxCapability.MailboxItemExport),
        new("Application MailboxItem.ImportExport", "Import and export mailbox items", RoleCategory.MailboxFoldersAndItems, "Microsoft Graph", ["MailboxItem.ImportExport.All"],
            "Export and import all mailbox items of the scoped mailboxes.",
            "Enables bulk extraction and injection of mailbox content (backup and migration scenarios).",
            PrivilegeLevel.Critical, MailboxCapability.MailboxItemExport | MailboxCapability.MailboxItemImport),
        new("Application MailboxConfigItem.Read", "Read mailbox configuration items", RoleCategory.MailboxFoldersAndItems, "Microsoft Graph", ["MailboxConfigItem.Read"],
            "Read UserConfiguration objects (folder-associated configuration data).",
            "Exposes client and add-in configuration stored in the mailbox.",
            PrivilegeLevel.Moderate, MailboxCapability.MailboxConfigRead),
        new("Application MailboxConfigItem.ReadWrite", "Read and write mailbox configuration items", RoleCategory.MailboxFoldersAndItems, "Microsoft Graph", ["MailboxConfigItem.ReadWrite"],
            "Read, write, update and delete UserConfiguration objects.",
            "Can alter client configuration stored in the mailbox.",
            PrivilegeLevel.High, MailboxCapability.MailboxConfigWrite),

        new("Application EWS.AccessAsApp", "Exchange Web Services full access", RoleCategory.Protocols, "EWS", ["EWS.AccessAsApp"],
            "Use Exchange Web Services with full access to the scoped mailboxes.",
            "Full read, write and send access to everything in the mailbox through EWS. Microsoft has announced the retirement of EWS in Exchange Online; prefer granular Microsoft Graph roles.",
            PrivilegeLevel.Critical, MailboxCapability.EwsFullAccess),
        new("Application SMTP.SendAsApp", "SMTP client submission", RoleCategory.Protocols, "SMTP", ["SMTP.SendAsApp"],
            "Use SMTP client submission (SMTP AUTH) to submit mail to the outbox of the scoped mailboxes.",
            "Allows sending mail as the mailbox owner over SMTP AUTH.",
            PrivilegeLevel.High, MailboxCapability.SmtpSend),

        new("Application Mail Full Access", "Mail full access bundle", RoleCategory.FullAccessBundles, "Microsoft Graph", ["Mail.ReadWrite", "Mail.Send"],
            "Create, read, update and delete email and send mail as any of the scoped mailboxes.",
            "Combines tampering and impersonation capabilities. Prefer the individual roles you actually need.",
            PrivilegeLevel.Critical, MailboxCapability.MailWrite | MailboxCapability.MailSend),
        new("Application Exchange Full Access", "Exchange full access bundle", RoleCategory.FullAccessBundles, "Microsoft Graph",
            ["Mail.ReadWrite", "Mail.Send", "MailboxSettings.ReadWrite", "Calendars.ReadWrite", "Contacts.ReadWrite"],
            "Full read/write access to mail, calendars, contacts and mailbox settings, and send mail as any of the scoped mailboxes.",
            "The broadest Graph mailbox role. Use only when every included capability is genuinely required.",
            PrivilegeLevel.Critical,
            MailboxCapability.MailWrite | MailboxCapability.MailSend | MailboxCapability.MailboxSettingsWrite
            | MailboxCapability.CalendarsWrite | MailboxCapability.ContactsWrite),
    ];

    private static readonly Dictionary<string, ApplicationRoleDefinition> ByName =
        CuratedRoles.ToDictionary(r => r.RoleName, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ApplicationRoleDefinition> Curated => CuratedRoles;

    public static ApplicationRoleDefinition? TryGet(string roleName) =>
        ByName.TryGetValue(roleName.Trim(), out var definition) ? definition : null;

    /// <summary>
    /// Returns the definition for a role name, falling back to a conservative definition for roles unknown to this release.
    /// </summary>
    public static ApplicationRoleDefinition Resolve(string roleName, string? description = null) =>
        TryGet(roleName) ?? CreateUncurated(roleName, description, isAvailable: true);

    /// <summary>
    /// Merges the curated catalog with the roles discovered in the tenant. Curated roles that the tenant does not
    /// expose are returned with <see cref="ApplicationRoleDefinition.IsAvailableInTenant"/> = false, and discovered
    /// roles unknown to this release are returned as uncurated, privileged definitions.
    /// </summary>
    public static IReadOnlyList<ApplicationRoleDefinition> Merge(IEnumerable<ApplicationRoleInfo> discoveredRoles)
    {
        var discovered = discoveredRoles
            .Where(r => r.Name.StartsWith("Application ", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var result = new List<ApplicationRoleDefinition>();
        foreach (var curated in CuratedRoles)
            result.Add(curated with { IsAvailableInTenant = discovered.ContainsKey(curated.RoleName) });

        foreach (var role in discovered.Values.Where(r => !ByName.ContainsKey(r.Name)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            result.Add(CreateUncurated(role.Name, role.Description, isAvailable: true));

        return result;
    }

    /// <summary>
    /// Roles whose capabilities cover all capabilities of <paramref name="roleName"/> (including the role itself).
    /// Uncurated roles are treated as potentially covering everything.
    /// </summary>
    public static bool IsEquivalentOrBroader(string candidateRole, string roleName)
    {
        if (candidateRole.Equals(roleName, StringComparison.OrdinalIgnoreCase)) return true;
        var candidate = TryGet(candidateRole);
        var target = TryGet(roleName);
        if (candidate is null || target is null) return candidate is null;
        return CapabilityLattice.Covers(candidate.Grants, target.Grants);
    }

    /// <summary>True when two roles share any capability (after expansion). Uncurated roles always overlap.</summary>
    public static bool Overlaps(string roleA, string roleB)
    {
        if (roleA.Equals(roleB, StringComparison.OrdinalIgnoreCase)) return true;
        var a = TryGet(roleA);
        var b = TryGet(roleB);
        if (a is null || b is null) return true;
        return CapabilityLattice.Overlap(a.Grants, b.Grants) != MailboxCapability.None;
    }

    public static MailboxCapability CapabilitiesOf(IEnumerable<string> roleNames) =>
        roleNames.Aggregate(MailboxCapability.None, (acc, name) => acc | (TryGet(name)?.Grants ?? MailboxCapability.None));

    private static ApplicationRoleDefinition CreateUncurated(string roleName, string? description, bool isAvailable)
    {
        var permission = roleName.StartsWith("Application ", StringComparison.OrdinalIgnoreCase) ? roleName["Application ".Length..] : roleName;
        return new ApplicationRoleDefinition(
            roleName,
            permission,
            RoleCategory.Other,
            "Unknown",
            [permission],
            string.IsNullOrWhiteSpace(description)
                ? "This application role exists in the connected tenant but is not described by this release of Exchange RBAC Studio."
                : description!,
            "Capabilities of this role are not known to this release. Review Microsoft documentation and treat it as privileged.",
            PrivilegeLevel.High,
            MailboxCapability.None,
            IsCurated: false,
            IsAvailableInTenant: isAvailable);
    }
}
