using ExchangeRbacStudio.Domain.Permissions;

namespace ExchangeRbacStudio.Application.Reporting;

/// <summary>
/// Example Microsoft Graph requests the application owner can use for end-to-end verification with the application's
/// own app-only token. Exchange RBAC Studio never holds application credentials, so it does not run these itself.
/// </summary>
public static class GraphRequestExamples
{
    private const string Denied = "403 Forbidden (ErrorAccessDenied) once the authorization cache has refreshed";

    public static IReadOnlyList<ReportGraphRequest> For(IEnumerable<string> roleNames, string graphBaseUrl, string mailboxPlaceholder = "{mailbox-id-or-upn}")
    {
        var baseUrl = graphBaseUrl.TrimEnd('/');
        var user = $"{baseUrl}/users/{mailboxPlaceholder}";
        var result = new List<ReportGraphRequest>();

        foreach (var role in roleNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var definition = ApplicationRoleCatalog.Resolve(role);
            foreach (var permission in definition.ApiPermissions)
            {
                var example = permission switch
                {
                    "Mail.ReadBasic" => new ReportGraphRequest(permission, "GET", $"{user}/messages?$top=1&$select=subject,from,receivedDateTime", "200 OK", Denied, "Body, preview and attachments are not returned with Mail.ReadBasic."),
                    "Mail.Read" => new ReportGraphRequest(permission, "GET", $"{user}/messages?$top=1", "200 OK", Denied, null),
                    "Mail.ReadWrite" => new ReportGraphRequest(permission, "POST", $"{user}/messages", "201 Created (draft message)", Denied, "Body: { \"subject\": \"RBAC verification draft\" }. Delete the draft afterwards."),
                    "Mail.Send" => new ReportGraphRequest(permission, "POST", $"{user}/sendMail", "202 Accepted", Denied, "Send only to a test recipient you control."),
                    "Calendars.Read" => new ReportGraphRequest(permission, "GET", $"{user}/events?$top=1", "200 OK", Denied, null),
                    "Calendars.ReadWrite" => new ReportGraphRequest(permission, "POST", $"{user}/events", "201 Created", Denied, "Create a test event without attendees and delete it afterwards."),
                    "Contacts.Read" => new ReportGraphRequest(permission, "GET", $"{user}/contacts?$top=1", "200 OK", Denied, null),
                    "Contacts.ReadWrite" => new ReportGraphRequest(permission, "POST", $"{user}/contacts", "201 Created", Denied, "Delete the test contact afterwards."),
                    "MailboxSettings.Read" => new ReportGraphRequest(permission, "GET", $"{user}/mailboxSettings", "200 OK", Denied, null),
                    "MailboxSettings.ReadWrite" => new ReportGraphRequest(permission, "PATCH", $"{user}/mailboxSettings", "200 OK", Denied, "Patch a harmless property (for example timeZone) to its current value."),
                    "MailTips.ReadBasic.All" => new ReportGraphRequest(permission, "POST", $"{user}/getMailTips", "200 OK", Denied, null),
                    "MailboxFolder.Read.All" or "MailboxFolder.ReadWrite.All" or "MailboxItem.Read.All" or "MailboxItem.ReadWrite.All" or "MailboxItem.Export.All" or "MailboxItem.ImportExport.All" =>
                        new ReportGraphRequest(permission, "GET", $"{baseUrl.Replace("/v1.0", "/beta", StringComparison.Ordinal)}/admin/exchange/mailboxes/{{mailbox-id}}/folders", "200 OK", Denied,
                            "Mailbox import/export APIs; check the current API version and the mailbox ID format in Microsoft Graph documentation."),
                    "EWS.AccessAsApp" => new ReportGraphRequest(permission, "POST", "https://outlook.office365.com/EWS/Exchange.asmx", "Success response", "ErrorAccessDenied",
                        "EWS request with an app-only token and the X-AnchorMailbox header set to the target mailbox."),
                    "SMTP.SendAsApp" => new ReportGraphRequest(permission, "SMTP", "smtp.office365.com:587 (AUTH XOAUTH2)", "250 OK", "535 Authentication unsuccessful", "Authenticate as the app with an app-only token for the mailbox."),
                    _ => new ReportGraphRequest(permission, "-", "See Microsoft Graph documentation for the permission.", "Success", Denied, null),
                };
                if (result.All(r => r.Permission != example.Permission)) result.Add(example);
            }
        }

        return result;
    }
}
