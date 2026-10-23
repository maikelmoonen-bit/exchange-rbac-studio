using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Auditing;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Auditing;
using ExchangeRbacStudio.Domain.Authorization;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Permissions;

namespace ExchangeRbacStudio.Application.Authorization;

public sealed record AuthorizationTestRequest(
    Guid ServicePrincipalObjectId,
    string ApplicationDisplayName,
    IReadOnlyList<ExchangeRecipient> PositiveMailboxes,
    IReadOnlyList<ExchangeRecipient> NegativeMailboxes,
    IReadOnlyList<string> Roles,
    MailboxCapability EntraTenantWideCapabilities,
    bool EntraGrantsVerified);

public sealed record MailboxTestResult(
    ExchangeRecipient Mailbox,
    TestExpectation Expectation,
    IReadOnlyList<AuthorizationTestRow> Rows,
    IReadOnlyList<RoleAuthorizationOutcome> Outcomes,
    AuthorizationVerdict Verdict,
    string? Error);

public sealed record AuthorizationTestReport(
    DateTimeOffset ExecutedUtc,
    string ApplicationDisplayName,
    IReadOnlyList<MailboxTestResult> Results,
    AuthorizationVerdict OverallVerdict,
    IReadOnlyList<string> Notes)
{
    public int Passed => Results.Count(r => r.Verdict == AuthorizationVerdict.Pass);

    public int Failed => Results.Count(r => r.Verdict == AuthorizationVerdict.Fail);

    public int Inconclusive => Results.Count(r => r.Verdict == AuthorizationVerdict.Inconclusive);
}

/// <summary>
/// Runs Test-ServicePrincipalAuthorization for intended (positive) and out-of-scope (negative) mailboxes and evaluates
/// each requested role separately. Read-only.
/// </summary>
public sealed class AuthorizationTestService(ConnectionManager connections, IExchangeRbacGateway exchange, AuditRecorder audit, TimeProvider time)
{
    public static IReadOnlyList<string> StandardNotes { get; } =
    [
        "Test-ServicePrincipalAuthorization evaluates Exchange RBAC for Applications only. It excludes permissions granted separately in Microsoft Entra ID, which are additive.",
        "The test bypasses the authorization cache. Real API calls by the application can reflect changes only after 30 minutes to 2 hours, depending on the application's recent activity.",
        "This is an authorization evaluation, not an end-to-end test: it does not authenticate as the application or call Microsoft Graph. Verify end-to-end behaviour with the example requests in the report.",
    ];

    public async Task<AuthorizationTestReport> RunAsync(AuthorizationTestRequest request, IProgress<MailboxTestResult>? progress, CancellationToken cancellationToken)
    {
        var session = connections.RequireExchange();
        var overlapping = request.PositiveMailboxes.Select(m => m.IdentityKey).Intersect(request.NegativeMailboxes.Select(m => m.IdentityKey)).ToList();
        if (overlapping.Count > 0)
            throw new StudioOperationException(FailureKind.InvalidInput, "A mailbox cannot be both an intended (positive) and an out-of-scope (negative) test target.");
        if (request.Roles.Count == 0)
            throw new StudioOperationException(FailureKind.InvalidInput, "Select at least one role to evaluate.");

        var results = new List<MailboxTestResult>();
        var targets = request.PositiveMailboxes.Select(m => (m, TestExpectation.InScope))
            .Concat(request.NegativeMailboxes.Select(m => (m, TestExpectation.OutOfScope)));

        foreach (var (mailbox, expectation) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MailboxTestResult result;
            try
            {
                var resource = mailbox.Guid?.ToString("D") ?? mailbox.PrimarySmtpAddress ?? mailbox.Identity;
                var rows = await exchange.TestServicePrincipalAuthorizationAsync(request.ServicePrincipalObjectId.ToString("D"), resource, cancellationToken).ConfigureAwait(false);
                var outcomes = AuthorizationEvaluator.Evaluate(mailbox.Label, expectation, request.Roles, rows, request.EntraTenantWideCapabilities);
                result = new MailboxTestResult(mailbox, expectation, rows, outcomes, AuthorizationEvaluator.Summarize(outcomes), null);
            }
            catch (StudioOperationException ex) when (!ex.IsSessionProblem)
            {
                result = new MailboxTestResult(mailbox, expectation, [], [], AuthorizationVerdict.Inconclusive, ex.Message);
            }

            results.Add(result);
            progress?.Report(result);
        }

        var notes = StandardNotes.ToList();
        if (!request.EntraGrantsVerified)
            notes.Insert(0, "Microsoft Entra ID permissions of the application could not be verified; negative test results cannot rule out tenant-wide access.");

        var overall = results.Count == 0 ? AuthorizationVerdict.Inconclusive
            : results.Any(r => r.Verdict == AuthorizationVerdict.Fail) ? AuthorizationVerdict.Fail
            : results.Any(r => r.Verdict == AuthorizationVerdict.Inconclusive) || !request.EntraGrantsVerified ? AuthorizationVerdict.Inconclusive
            : AuthorizationVerdict.Pass;

        var report = new AuthorizationTestReport(time.GetUtcNow(), request.ApplicationDisplayName, results, overall, notes);
        await audit.RecordAsync(session, AuditCategories.Verification, "Test-ServicePrincipalAuthorization",
            overall switch { AuthorizationVerdict.Pass => AuditOutcome.Succeeded, AuthorizationVerdict.Fail => AuditOutcome.Failed, _ => AuditOutcome.Warning },
            $"{report.Passed} passed, {report.Failed} failed, {report.Inconclusive} inconclusive across {results.Count} mailbox(es) for roles {string.Join(", ", request.Roles)}.",
            request.ApplicationDisplayName).ConfigureAwait(false);
        return report;
    }
}
