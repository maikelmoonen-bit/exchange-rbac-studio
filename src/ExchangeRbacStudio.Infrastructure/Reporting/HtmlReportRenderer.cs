using System.Globalization;
using System.Net;
using System.Text;
using ExchangeRbacStudio.Application.Reporting;

namespace ExchangeRbacStudio.Infrastructure.Reporting;

/// <summary>Renders a <see cref="ChangeReport"/> as a self-contained, printable HTML document. All values are HTML-encoded.</summary>
public static class HtmlReportRenderer
{
    public static string Render(ChangeReport report)
    {
        var sb = new StringBuilder(32 * 1024);
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(E(report.Title)).Append("</title><style>").Append(Css).Append("</style></head><body><main>");

        sb.Append("<header><div class=\"brand\">Exchange RBAC Studio</div><h1>").Append(E(report.Title)).Append("</h1>");
        sb.Append("<p class=\"muted\">Generated ").Append(E(Format(report.GeneratedUtc))).Append(" · Report ").Append(E(report.ReportId.ToString())).Append(" · Version ").Append(E(report.ToolVersion)).Append("</p>");
        if (report.DryRun) sb.Append("<p class=\"banner warn\">DRY RUN: no changes were made in Exchange Online.</p>");
        sb.Append("</header>");

        var verdict = report.SecurityVerdict;
        sb.Append("<section class=\"verdict ").Append(verdict.SecurelyScoped ? "ok" : "bad").Append("\"><h2>Security verdict</h2><p class=\"statement\">")
          .Append(E(verdict.Statement)).Append("</p>");
        if (verdict.Reasons.Count > 0)
        {
            sb.Append("<ul>");
            foreach (var reason in verdict.Reasons) sb.Append("<li>").Append(E(reason)).Append("</li>");
            sb.Append("</ul>");
        }

        sb.Append("</section>");

        sb.Append("<section><h2>Identity</h2><table class=\"kv\">");
        Row(sb, "Tenant", $"{report.Tenant.DisplayName} ({report.Tenant.TenantId})");
        Row(sb, "Default domain", report.Tenant.DefaultDomain);
        Row(sb, "Environment", report.Tenant.Environment);
        Row(sb, "Executed by", report.Administrator.UserPrincipalName);
        Row(sb, "Workstation", report.Administrator.MachineName);
        if (report.Application is { } app)
        {
            Row(sb, "Application", app.DisplayName);
            Row(sb, "Application (client) ID", app.AppId.ToString());
            Row(sb, "Service principal object ID", app.ServicePrincipalObjectId.ToString());
            Row(sb, "Exchange service principal", app.ExchangeServicePrincipalStatus);
        }

        sb.Append("</table></section>");

        if (report.Scope is { } scope)
        {
            sb.Append("<section><h2>Mailbox scope</h2><table class=\"kv\">");
            Row(sb, "Management scope", scope.Name);
            Row(sb, "Filter strategy", scope.Strategy);
            sb.Append("<tr><th>Recipient filter</th><td><code>").Append(E(scope.Filter)).Append("</code></td></tr>");
            Row(sb, "Membership verification", scope.MembershipVerdict);
            sb.Append("</table>");
            Table(sb, "Intended mailboxes", ["Display name", "Primary SMTP address", "Type", "Object GUID"],
                scope.IntendedMailboxes.Select(m => new[] { m.DisplayName, m.PrimarySmtpAddress, m.RecipientTypeDetails, m.ObjectGuid?.ToString() }));
            if (scope.ResolvedMatches.Count > 0)
                Table(sb, "Recipients matched by the filter (Exchange preview)", ["Display name", "Primary SMTP address", "Type", "Object GUID"],
                    scope.ResolvedMatches.Select(m => new[] { m.DisplayName, m.PrimarySmtpAddress, m.RecipientTypeDetails, m.ObjectGuid?.ToString() }));
            sb.Append("</section>");
        }

        if (report.Roles.Count > 0)
        {
            sb.Append("<section><h2>Roles</h2>");
            Table(sb, null, ["Role", "API permissions", "Privilege", "Explanation"],
                report.Roles.Select(r => new[] { r.RoleName, string.Join(", ", r.ApiPermissions), r.Privilege, r.Explanation }));
            sb.Append("</section>");
        }

        if (report.Execution is { } execution)
        {
            sb.Append("<section><h2>Execution</h2><table class=\"kv\">");
            Row(sb, "Outcome", execution.Outcome);
            Row(sb, "Summary", execution.Summary);
            Row(sb, "Started", Format(execution.StartedUtc));
            Row(sb, "Completed", Format(execution.CompletedUtc));
            Row(sb, "Run ID", execution.RunId.ToString());
            Row(sb, "Plan fingerprint", execution.PlanFingerprint);
            sb.Append("</table>");
            Table(sb, "Operations", ["#", "Operation", "Disposition", "Status", "Result", "Command"],
                execution.Operations.Select(o => new[] { o.Sequence.ToString(CultureInfo.InvariantCulture), o.Summary, o.Disposition, o.Status, o.Message, o.Command }),
                codeColumn: 5, statusColumn: 3);
            if (execution.CreatedObjects.Count > 0) List(sb, "Created objects", execution.CreatedObjects);
            if (execution.ReusedObjects.Count > 0) List(sb, "Reused objects", execution.ReusedObjects);
            if (execution.Warnings.Count > 0) List(sb, "Warnings", execution.Warnings);
            sb.Append("</section>");
        }

        if (report.AuthorizationTests is { } tests)
        {
            sb.Append("<section><h2>Authorization tests</h2><p>Overall: <strong>").Append(E(tests.OverallVerdict)).Append("</strong> · Executed ")
              .Append(E(Format(tests.ExecutedUtc))).Append("</p>");
            Table(sb, null, ["Mailbox", "Expectation", "Role", "Exchange result", "Entra tenant-wide", "Verdict", "Explanation"],
                tests.Outcomes.Select(o => new[] { o.Mailbox, o.Expectation, o.Role, o.ExchangeResult, o.EntraTenantWideGrant ? "Yes" : "No", o.Verdict, o.Explanation }),
                statusColumn: 5);
            if (tests.Errors.Count > 0) List(sb, "Errors", tests.Errors);
            List(sb, "Notes", tests.Notes);
            sb.Append("</section>");
        }

        if (report.SecurityFindings.Count > 0)
        {
            sb.Append("<section><h2>Security findings</h2>");
            Table(sb, null, ["Severity", "Finding", "Detail", "Remediation"],
                report.SecurityFindings.Select(f => new[] { f.Severity, f.Title, f.Detail, f.Remediation }), statusColumn: 0);
            sb.Append("</section>");
        }

        if (report.PreflightChecks.Count > 0)
        {
            sb.Append("<section><h2>Preflight validation</h2>");
            Table(sb, null, ["Check", "Status", "Result", "Remediation"],
                report.PreflightChecks.Select(c => new[] { c.Title + (c.SecurityCritical ? " (security-critical)" : string.Empty), c.Status, c.Summary, c.Remediation }),
                statusColumn: 1);
            sb.Append("</section>");
        }

        if (report.RemainingVerification.Count > 0)
        {
            sb.Append("<section><h2>Remaining verification</h2>");
            List(sb, null, report.RemainingVerification);
            sb.Append("</section>");
        }

        if (report.ExampleGraphRequests.Count > 0)
        {
            sb.Append("<section><h2>Example Microsoft Graph requests</h2><p class=\"muted\">Run with the application's own app-only token. Exchange RBAC Studio never holds application credentials.</p>");
            Table(sb, null, ["Permission", "Method", "Request", "In scope", "Out of scope", "Notes"],
                report.ExampleGraphRequests.Select(r => new[] { r.Permission, r.Method, r.Url, r.ExpectedInScope, r.ExpectedOutOfScope, r.Notes }), codeColumn: 2);
            sb.Append("</section>");
        }

        if (!string.IsNullOrWhiteSpace(report.PowerShellEquivalent))
        {
            sb.Append("<section><h2>Equivalent PowerShell</h2><p class=\"muted\">For transparency. The application executed these operations through typed parameter binding in an isolated PowerShell 7 host.</p><pre>")
              .Append(E(report.PowerShellEquivalent)).Append("</pre></section>");
        }

        sb.Append("<footer class=\"muted\">This report contains no credentials, secrets or tokens.</footer></main></body></html>");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append("<tr><th>").Append(E(label)).Append("</th><td>").Append(E(value)).Append("</td></tr>");
    }

    private static void Table(StringBuilder sb, string? caption, string[] headers, IEnumerable<string?[]> rows, int codeColumn = -1, int statusColumn = -1)
    {
        if (caption is not null) sb.Append("<h3>").Append(E(caption)).Append("</h3>");
        sb.Append("<div class=\"scroll\"><table class=\"grid\"><thead><tr>");
        foreach (var header in headers) sb.Append("<th>").Append(E(header)).Append("</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            sb.Append("<tr>");
            for (var i = 0; i < row.Length; i++)
            {
                var value = E(row[i]);
                sb.Append("<td>");
                if (i == codeColumn && !string.IsNullOrEmpty(value)) sb.Append("<code>").Append(value).Append("</code>");
                else if (i == statusColumn) sb.Append("<span class=\"pill ").Append(StatusClass(row[i])).Append("\">").Append(value).Append("</span>");
                else sb.Append(value);
                sb.Append("</td>");
            }

            sb.Append("</tr>");
        }

        sb.Append("</tbody></table></div>");
    }

    private static void List(StringBuilder sb, string? caption, IEnumerable<string> items)
    {
        if (caption is not null) sb.Append("<h3>").Append(E(caption)).Append("</h3>");
        sb.Append("<ul>");
        foreach (var item in items) sb.Append("<li>").Append(E(item)).Append("</li>");
        sb.Append("</ul>");
    }

    private static string StatusClass(string? status) => status?.ToLowerInvariant() switch
    {
        "passed" or "pass" or "succeeded" or "completed" or "reused" or "info" => "ok",
        "warning" or "inconclusive" or "dryrun" or "notrun" or "partiallycompleted" => "warn",
        "blocked" or "failed" or "fail" or "unabletoverify" or "critical" or "high" => "bad",
        _ => "neutral",
    };

    private static string Format(DateTimeOffset value) => value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private const string Css = """
        :root{--bg:#f6f7f9;--card:#fff;--text:#1b1f24;--muted:#5b6470;--border:#dde1e6;--accent:#0f6cbd;--ok:#0e7a0d;--okbg:#e7f5e6;--warn:#8a5300;--warnbg:#fff4ce;--bad:#b10e1c;--badbg:#fde7e9}
        @media (prefers-color-scheme:dark){:root{--bg:#1b1b1f;--card:#26262b;--text:#f1f1f3;--muted:#a9adb5;--border:#3a3a41;--accent:#62a7ec;--ok:#6ccb5f;--okbg:#1f3a1d;--warn:#f2c661;--warnbg:#3b2f10;--bad:#ff99a4;--badbg:#44181d}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI Variable","Segoe UI",system-ui,sans-serif}
        main{max-width:1100px;margin:0 auto;padding:32px 16px 64px}header{margin-bottom:24px}.brand{color:var(--accent);font-weight:600;letter-spacing:.02em}
        h1{font-size:26px;margin:4px 0}h2{font-size:18px;margin:0 0 12px}h3{font-size:14px;margin:18px 0 8px;color:var(--muted)}
        section{background:var(--card);border:1px solid var(--border);border-radius:10px;padding:20px;margin:0 0 16px}
        .muted{color:var(--muted)}.banner{padding:10px 14px;border-radius:8px;font-weight:600}.banner.warn{background:var(--warnbg);color:var(--warn)}
        .verdict.ok{border-left:4px solid var(--ok)}.verdict.bad{border-left:4px solid var(--bad)}.statement{font-weight:600}
        table{border-collapse:collapse;width:100%}.kv th{text-align:left;width:240px;color:var(--muted);font-weight:500;vertical-align:top;padding:4px 12px 4px 0}.kv td{padding:4px 0}
        .grid th,.grid td{border-bottom:1px solid var(--border);padding:8px;text-align:left;vertical-align:top}.grid th{color:var(--muted);font-weight:600;font-size:12px;text-transform:uppercase;letter-spacing:.03em}
        .scroll{overflow-x:auto}code,pre{font-family:"Cascadia Mono",Consolas,monospace;font-size:12.5px}code{word-break:break-all}
        pre{background:var(--bg);border:1px solid var(--border);border-radius:8px;padding:12px;overflow-x:auto;white-space:pre}
        .pill{display:inline-block;padding:1px 8px;border-radius:999px;font-size:12px;font-weight:600}.pill.ok{background:var(--okbg);color:var(--ok)}.pill.warn{background:var(--warnbg);color:var(--warn)}.pill.bad{background:var(--badbg);color:var(--bad)}.pill.neutral{border:1px solid var(--border)}
        footer{margin-top:24px;font-size:12px}@media print{body{background:#fff}section{break-inside:avoid}}
        """;
}
