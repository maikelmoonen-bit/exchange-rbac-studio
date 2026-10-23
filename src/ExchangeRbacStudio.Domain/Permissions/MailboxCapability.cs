namespace ExchangeRbacStudio.Domain.Permissions;

/// <summary>
/// Normalized mailbox data capabilities. Exchange RBAC application roles and Microsoft Entra application
/// permissions are both mapped onto this set, so the two independent (additive) authorization systems can be compared.
/// </summary>
[Flags]
public enum MailboxCapability : long
{
    None = 0,
    MailReadBasic = 1L << 0,
    MailRead = 1L << 1,
    MailWrite = 1L << 2,
    MailSend = 1L << 3,
    MailAdvancedWrite = 1L << 4,
    MailboxSettingsRead = 1L << 5,
    MailboxSettingsWrite = 1L << 6,
    CalendarsReadBasic = 1L << 7,
    CalendarsRead = 1L << 8,
    CalendarsWrite = 1L << 9,
    ContactsRead = 1L << 10,
    ContactsWrite = 1L << 11,
    MailboxFolderRead = 1L << 12,
    MailboxFolderWrite = 1L << 13,
    MailboxItemRead = 1L << 14,
    MailboxItemWrite = 1L << 15,
    MailboxItemExport = 1L << 16,
    MailboxItemImport = 1L << 17,
    MailboxConfigRead = 1L << 18,
    MailboxConfigWrite = 1L << 19,
    MailTipsRead = 1L << 20,
    EwsFullAccess = 1L << 21,
    SmtpSend = 1L << 22,
    ImapAccess = 1L << 23,
    PopAccess = 1L << 24,

    /// <summary>Can administer Exchange Online, including RBAC configuration itself.</summary>
    ExchangeAdministration = 1L << 25,

    /// <summary>Can grant itself or others additional directory permissions (privilege escalation path).</summary>
    DirectoryPrivilegeEscalation = 1L << 26,
}

/// <summary>
/// Implication rules between capabilities ("ReadWrite implies Read"). Used conservatively for security analysis:
/// broader permissions are always treated as covering the narrower ones.
/// </summary>
public static class CapabilityLattice
{
    private const MailboxCapability AllMailboxData =
        MailboxCapability.MailReadBasic | MailboxCapability.MailRead | MailboxCapability.MailWrite | MailboxCapability.MailSend
        | MailboxCapability.MailboxSettingsRead | MailboxCapability.MailboxSettingsWrite
        | MailboxCapability.CalendarsReadBasic | MailboxCapability.CalendarsRead | MailboxCapability.CalendarsWrite
        | MailboxCapability.ContactsRead | MailboxCapability.ContactsWrite
        | MailboxCapability.MailboxFolderRead | MailboxCapability.MailboxFolderWrite
        | MailboxCapability.MailboxItemRead | MailboxCapability.MailboxItemWrite
        | MailboxCapability.MailboxItemExport | MailboxCapability.MailboxItemImport
        | MailboxCapability.MailboxConfigRead | MailboxCapability.MailboxConfigWrite | MailboxCapability.MailTipsRead;

    /// <summary>Capabilities that grant access to mailbox content or settings (excludes administrative capabilities).</summary>
    public const MailboxCapability MailboxDataCapabilities =
        AllMailboxData | MailboxCapability.MailAdvancedWrite | MailboxCapability.EwsFullAccess
        | MailboxCapability.SmtpSend | MailboxCapability.ImapAccess | MailboxCapability.PopAccess;

    private static readonly (MailboxCapability Source, MailboxCapability Implied)[] Rules =
    [
        (MailboxCapability.MailRead, MailboxCapability.MailReadBasic),
        (MailboxCapability.MailWrite, MailboxCapability.MailRead),
        (MailboxCapability.MailAdvancedWrite, MailboxCapability.MailWrite),
        (MailboxCapability.MailboxSettingsWrite, MailboxCapability.MailboxSettingsRead),
        (MailboxCapability.CalendarsRead, MailboxCapability.CalendarsReadBasic),
        (MailboxCapability.CalendarsWrite, MailboxCapability.CalendarsRead),
        (MailboxCapability.ContactsWrite, MailboxCapability.ContactsRead),
        (MailboxCapability.MailboxFolderWrite, MailboxCapability.MailboxFolderRead),
        (MailboxCapability.MailboxItemWrite, MailboxCapability.MailboxItemRead),
        (MailboxCapability.MailboxItemExport, MailboxCapability.MailboxItemRead),
        (MailboxCapability.MailboxItemImport, MailboxCapability.MailboxItemWrite),
        // Mailbox item APIs operate on items of every type (messages, events, contacts).
        (MailboxCapability.MailboxItemRead, MailboxCapability.MailRead | MailboxCapability.CalendarsRead | MailboxCapability.ContactsRead | MailboxCapability.MailboxFolderRead),
        (MailboxCapability.MailboxItemWrite, MailboxCapability.MailWrite | MailboxCapability.CalendarsWrite | MailboxCapability.ContactsWrite),
        (MailboxCapability.MailboxConfigWrite, MailboxCapability.MailboxConfigRead),
        // EWS full access grants unrestricted access to mailbox content, including sending.
        (MailboxCapability.EwsFullAccess, AllMailboxData),
    ];

    /// <summary>Returns the capability set closed under all implication rules.</summary>
    public static MailboxCapability Expand(MailboxCapability capabilities)
    {
        var current = capabilities;
        bool changed;
        do
        {
            changed = false;
            foreach (var (source, implied) in Rules)
            {
                if ((current & source) != 0 && (current & implied) != implied)
                {
                    current |= implied;
                    changed = true;
                }
            }
        }
        while (changed);
        return current;
    }

    /// <summary>True if <paramref name="broader"/> grants every capability of <paramref name="narrower"/>.</summary>
    public static bool Covers(MailboxCapability broader, MailboxCapability narrower)
    {
        var expanded = Expand(broader);
        var required = Expand(narrower);
        return required != MailboxCapability.None && (expanded & required) == required;
    }

    /// <summary>The capabilities shared by both sets after expansion.</summary>
    public static MailboxCapability Overlap(MailboxCapability a, MailboxCapability b) => Expand(a) & Expand(b);

    public static IEnumerable<MailboxCapability> Enumerate(MailboxCapability capabilities)
    {
        foreach (var value in Enum.GetValues<MailboxCapability>())
        {
            if (value != MailboxCapability.None && (capabilities & value) == value)
                yield return value;
        }
    }

    public static string Describe(MailboxCapability capability) => capability switch
    {
        MailboxCapability.MailReadBasic => "Read message metadata (no body or attachments)",
        MailboxCapability.MailRead => "Read mail",
        MailboxCapability.MailWrite => "Create, update and delete mail",
        MailboxCapability.MailSend => "Send mail as the mailbox",
        MailboxCapability.MailAdvancedWrite => "Modify contents of sent (non-draft) mail",
        MailboxCapability.MailboxSettingsRead => "Read mailbox settings",
        MailboxCapability.MailboxSettingsWrite => "Change mailbox settings",
        MailboxCapability.CalendarsReadBasic => "Read basic calendar details",
        MailboxCapability.CalendarsRead => "Read calendars",
        MailboxCapability.CalendarsWrite => "Create, update and delete calendar events",
        MailboxCapability.ContactsRead => "Read contacts",
        MailboxCapability.ContactsWrite => "Create, update and delete contacts",
        MailboxCapability.MailboxFolderRead => "Read mailbox folders",
        MailboxCapability.MailboxFolderWrite => "Change mailbox folders",
        MailboxCapability.MailboxItemRead => "Read any mailbox item",
        MailboxCapability.MailboxItemWrite => "Write and delete any mailbox item",
        MailboxCapability.MailboxItemExport => "Export mailbox items",
        MailboxCapability.MailboxItemImport => "Import mailbox items",
        MailboxCapability.MailboxConfigRead => "Read mailbox configuration objects",
        MailboxCapability.MailboxConfigWrite => "Change mailbox configuration objects",
        MailboxCapability.MailTipsRead => "Read MailTips metadata",
        MailboxCapability.EwsFullAccess => "Full mailbox access through Exchange Web Services",
        MailboxCapability.SmtpSend => "Submit mail through SMTP AUTH",
        MailboxCapability.ImapAccess => "Mailbox access through IMAP",
        MailboxCapability.PopAccess => "Mailbox access through POP",
        MailboxCapability.ExchangeAdministration => "Administer Exchange Online (including RBAC)",
        MailboxCapability.DirectoryPrivilegeEscalation => "Grant itself additional directory permissions",
        _ => capability.ToString(),
    };
}
