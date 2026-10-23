using ExchangeRbacStudio.Domain.Identity;

namespace ExchangeRbacStudio.Domain.Permissions;

/// <summary>How a Microsoft Entra application permission applies to mailboxes.</summary>
public enum EntraGrantReach
{
    /// <summary>Applies to every mailbox in the tenant (unless constrained by an Application Access Policy).</summary>
    TenantWideMailboxAccess,

    /// <summary>Requires an additional Exchange mailbox permission (for example IMAP/POP/SMTP app access).</summary>
    RequiresExchangeMailboxPermission,

    /// <summary>Administrative or directory permission that can be used to escalate privileges.</summary>
    Administrative,
}

public sealed record EntraPermissionDefinition(
    Guid ResourceAppId,
    string Value,
    MailboxCapability Capabilities,
    EntraGrantReach Reach,
    string Explanation);

/// <summary>
/// Microsoft Entra application permissions that are relevant to Exchange mailbox access or that allow an application
/// to escalate its own privileges. Permissions not listed here are considered unrelated to mailbox access.
/// </summary>
public static class EntraPermissionCatalog
{
    private static readonly Guid Graph = WellKnownResourceApps.MicrosoftGraph;
    private static readonly Guid Exchange = WellKnownResourceApps.Office365ExchangeOnline;

    private static readonly EntraPermissionDefinition[] Definitions =
    [
        // Microsoft Graph application permissions with tenant-wide mailbox reach.
        Tenant(Graph, "Mail.ReadBasic", MailboxCapability.MailReadBasic),
        Tenant(Graph, "Mail.ReadBasic.All", MailboxCapability.MailReadBasic),
        Tenant(Graph, "Mail.Read", MailboxCapability.MailRead),
        Tenant(Graph, "Mail.ReadWrite", MailboxCapability.MailWrite),
        Tenant(Graph, "Mail.Send", MailboxCapability.MailSend),
        Tenant(Graph, "Mail-Advanced.ReadWrite.All", MailboxCapability.MailAdvancedWrite),
        Tenant(Graph, "MailTips.ReadBasic.All", MailboxCapability.MailTipsRead),
        Tenant(Graph, "MailboxSettings.Read", MailboxCapability.MailboxSettingsRead),
        Tenant(Graph, "MailboxSettings.ReadWrite", MailboxCapability.MailboxSettingsWrite),
        Tenant(Graph, "Calendars.ReadBasic", MailboxCapability.CalendarsReadBasic),
        Tenant(Graph, "Calendars.ReadBasic.All", MailboxCapability.CalendarsReadBasic),
        Tenant(Graph, "Calendars.Read", MailboxCapability.CalendarsRead),
        Tenant(Graph, "Calendars.ReadWrite", MailboxCapability.CalendarsWrite),
        Tenant(Graph, "Contacts.Read", MailboxCapability.ContactsRead),
        Tenant(Graph, "Contacts.ReadWrite", MailboxCapability.ContactsWrite),
        Tenant(Graph, "MailboxFolder.Read.All", MailboxCapability.MailboxFolderRead),
        Tenant(Graph, "MailboxFolder.ReadWrite.All", MailboxCapability.MailboxFolderWrite),
        Tenant(Graph, "MailboxItem.Read.All", MailboxCapability.MailboxItemRead),
        Tenant(Graph, "MailboxItem.ReadWrite.All", MailboxCapability.MailboxItemWrite),
        Tenant(Graph, "MailboxItem.Export.All", MailboxCapability.MailboxItemExport),
        Tenant(Graph, "MailboxItem.ImportExport.All", MailboxCapability.MailboxItemExport | MailboxCapability.MailboxItemImport),
        Tenant(Graph, "MailboxConfigItem.Read", MailboxCapability.MailboxConfigRead),
        Tenant(Graph, "MailboxConfigItem.ReadWrite", MailboxCapability.MailboxConfigWrite),

        // Microsoft Graph permissions that allow privilege escalation (the app could grant itself tenant-wide access).
        Admin(Graph, "Application.ReadWrite.All", MailboxCapability.DirectoryPrivilegeEscalation,
            "Can add credentials to and change permissions of any application, including granting itself tenant-wide mailbox permissions."),
        Admin(Graph, "AppRoleAssignment.ReadWrite.All", MailboxCapability.DirectoryPrivilegeEscalation,
            "Can grant any application permission (for example Mail.Read) to itself, bypassing Exchange RBAC scoping."),
        Admin(Graph, "RoleManagement.ReadWrite.Directory", MailboxCapability.DirectoryPrivilegeEscalation,
            "Can assign directory roles (including Exchange Administrator) to itself."),
        Admin(Graph, "RoleManagement.ReadWrite.Exchange", MailboxCapability.ExchangeAdministration,
            "Can change Exchange Online RBAC configuration, including its own application role assignments and scopes."),

        // Office 365 Exchange Online resource permissions.
        Tenant(Exchange, "full_access_as_app", MailboxCapability.EwsFullAccess,
            "Full access to every mailbox through Exchange Web Services."),
        Tenant(Exchange, "EWS.AccessAsApp", MailboxCapability.EwsFullAccess,
            "Full access to every mailbox through Exchange Web Services."),
        Tenant(Exchange, "Mail.Read", MailboxCapability.MailRead),
        Tenant(Exchange, "Mail.ReadWrite", MailboxCapability.MailWrite),
        Tenant(Exchange, "Mail.Send", MailboxCapability.MailSend),
        Tenant(Exchange, "MailboxSettings.Read", MailboxCapability.MailboxSettingsRead),
        Tenant(Exchange, "MailboxSettings.ReadWrite", MailboxCapability.MailboxSettingsWrite),
        Tenant(Exchange, "Calendars.Read", MailboxCapability.CalendarsRead),
        Tenant(Exchange, "Calendars.ReadWrite", MailboxCapability.CalendarsWrite),
        Tenant(Exchange, "Contacts.Read", MailboxCapability.ContactsRead),
        Tenant(Exchange, "Contacts.ReadWrite", MailboxCapability.ContactsWrite),
        new(Exchange, "IMAP.AccessAsApp", MailboxCapability.ImapAccess, EntraGrantReach.RequiresExchangeMailboxPermission,
            "IMAP access as the app. Effective only for mailboxes where the service principal was granted mailbox permissions."),
        new(Exchange, "POP.AccessAsApp", MailboxCapability.PopAccess, EntraGrantReach.RequiresExchangeMailboxPermission,
            "POP access as the app. Effective only for mailboxes where the service principal was granted mailbox permissions."),
        new(Exchange, "SMTP.SendAsApp", MailboxCapability.SmtpSend, EntraGrantReach.RequiresExchangeMailboxPermission,
            "SMTP AUTH submission as the app. Effective only for mailboxes where the service principal was granted mailbox permissions."),
        Admin(Exchange, "Exchange.ManageAsApp", MailboxCapability.ExchangeAdministration,
            "Can run Exchange Online PowerShell as the app. Combined with an Exchange directory role it can change RBAC configuration, including its own scopes."),
    ];

    private static readonly Dictionary<(Guid, string), EntraPermissionDefinition> Index =
        Definitions.ToDictionary(d => (d.ResourceAppId, d.Value.ToUpperInvariant()));

    public static IReadOnlyList<EntraPermissionDefinition> All => Definitions;

    public static EntraPermissionDefinition? TryGet(Guid? resourceAppId, string? permissionValue)
    {
        if (resourceAppId is null || string.IsNullOrWhiteSpace(permissionValue)) return null;
        return Index.TryGetValue((resourceAppId.Value, permissionValue.Trim().ToUpperInvariant()), out var definition) ? definition : null;
    }

    private static EntraPermissionDefinition Tenant(Guid resource, string value, MailboxCapability capabilities, string? explanation = null) =>
        new(resource, value, capabilities, EntraGrantReach.TenantWideMailboxAccess,
            explanation ?? $"Tenant-wide application permission '{value}': applies to every mailbox in the organization.");

    private static EntraPermissionDefinition Admin(Guid resource, string value, MailboxCapability capabilities, string explanation) =>
        new(resource, value, capabilities, EntraGrantReach.Administrative, explanation);
}
