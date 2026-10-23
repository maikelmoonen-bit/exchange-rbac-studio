using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Authorization;
using ExchangeRbacStudio.Application.Configuration;
using ExchangeRbacStudio.Application.Diagnostics;
using ExchangeRbacStudio.Application.Execution;
using ExchangeRbacStudio.Application.Scoping;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Authorization;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Permissions;
using ExchangeRbacStudio.Domain.Planning;
using ExchangeRbacStudio.Domain.Security;
using ExchangeRbacStudio.Domain.Validation;

namespace ExchangeRbacStudio.Application.Reporting;

public sealed record ReportInput(
    SessionSnapshot Session,
    StudioRuntimeInfo Runtime,
    string Title,
    ChangePlan? Plan,
    ExecutionResult? Execution,
    PreflightReport? Preflight,
    ConfigurationRequest? Request,
    ScopeVerification? ScopeVerification,
    EntraGrantAnalysis? GrantAnalysis,
    AuthorizationTestReport? Tests,
    string? PointerStatus = null);

public static class ReportBuilder
{
    public static ChangeReport Build(ReportInput input, DateTimeOffset now)
    {
        var session = input.Session;
        var plan = input.Plan ?? input.Execution?.Plan;
        var roles = input.Request?.Roles ?? plan?.Roles ?? [];
        var verdict = BuildVerdict(input, plan);

        return new ChangeReport
        {
            ReportId = Guid.NewGuid(),
            Title = input.Title,
            GeneratedUtc = now,
            ToolVersion = input.Runtime.Version,
            DryRun = input.Execution?.DryRun ?? false,
            Tenant = new ReportTenant(session.TenantId, session.TenantDisplayName, session.Tenant?.DefaultDomain, session.Environment.DisplayName),
            Administrator = new ReportActor(session.AdministratorUpn, session.Administrator?.DisplayName, input.Runtime.MachineName),
            Application = plan?.Application is { } app
                ? new ReportApplication(app.DisplayName, app.AppId, app.ServicePrincipalObjectId, input.PointerStatus)
                : input.Request is { } request
                    ? new ReportApplication(request.ServicePrincipal.DisplayName, request.ServicePrincipal.AppId, request.ServicePrincipal.ObjectId, input.PointerStatus)
                    : null,
            Scope = BuildScope(input, plan),
            Roles = roles.Select(r =>
            {
                var d = ApplicationRoleCatalog.Resolve(r);
                return new ReportRole(d.RoleName, d.ApiPermissions, d.Privilege.ToString(), d.Explanation);
            }).ToList(),
            Execution = input.Execution is { } execution ? BuildExecution(execution) : null,
            SecurityVerdict = verdict,
            PreflightChecks = input.Preflight?.Checks.Select(c => new ReportCheck(c.Id, c.Title, c.Status.ToString(), Clean(c.Summary)!, Clean(c.Detail), Clean(c.Remediation), c.IsSecurityCritical)).ToList() ?? [],
            SecurityFindings = (input.GrantAnalysis?.Findings ?? [])
                .Select(f => new ReportFinding(f.Code, f.Severity.ToString(), f.Title, Clean(f.Detail)!, Clean(f.Remediation)))
                .ToList(),
            AuthorizationTests = input.Tests is { } tests ? BuildTests(tests) : null,
            RemainingVerification = BuildRemaining(input, verdict),
            ExampleGraphRequests = GraphRequestExamples.For(roles, session.Environment.GraphBaseUrl),
            PowerShellEquivalent = plan?.ToPowerShellScript(),
        };
    }

    private static ReportScope? BuildScope(ReportInput input, ChangePlan? plan)
    {
        var name = input.Request?.ScopeName ?? plan?.ScopeName;
        if (name is null) return null;
        var comparison = input.ScopeVerification?.Comparison;
        return new ReportScope(
            name,
            input.Request?.ScopeFilter ?? plan?.ScopeFilter,
            input.Request?.Strategy.ToString(),
            (input.Request?.Mailboxes ?? []).Select(ToMailbox).ToList(),
            (comparison?.Matched.Concat(comparison.Unexpected) ?? []).Select(ToMailbox).ToList(),
            input.ScopeVerification is { } v ? $"{v.Status}: {v.Message}" : "Not verified",
            input.ScopeVerification?.VerifiedUtc);
    }

    private static ReportExecution BuildExecution(ExecutionResult execution)
    {
        var operations = execution.Steps.Where(s => s.Operation is not null).Select(s => new ReportOperation(
            s.Operation!.Sequence,
            s.Operation.Kind.ToString(),
            s.Operation.Disposition.ToString(),
            s.Operation.Target,
            s.Operation.Summary,
            s.Status.ToString(),
            Clean(s.Message)!,
            s.Operation.Command?.ToPreview(),
            s.Operation.BeforeState,
            s.Operation.AfterState,
            s.StartedUtc,
            s.CompletedUtc)).ToList();

        return new ReportExecution(
            execution.RunId,
            execution.Plan.PlanId,
            execution.Plan.Fingerprint,
            execution.Outcome.ToString(),
            execution.ChangesMade,
            execution.Summary,
            execution.StartedUtc,
            execution.CompletedUtc,
            operations,
            execution.CreatedObjects.Select(o => $"{o.Kind}: {o.Target}").ToList(),
            execution.Plan.Operations.Where(o => o.Disposition == OperationDisposition.Reuse).Select(o => $"{o.Kind}: {o.Target}").ToList(),
            execution.Warnings.Select(w => Clean(w)!).ToList());
    }

    private static ReportAuthorizationTests BuildTests(AuthorizationTestReport tests) => new(
        tests.ExecutedUtc,
        tests.OverallVerdict.ToString(),
        tests.Results.SelectMany(r => r.Outcomes.Select(o => new ReportTestOutcome(
            o.Mailbox, o.Expectation == TestExpectation.InScope ? "In scope (positive)" : "Out of scope (negative)", o.RoleName,
            o.ExchangeResult.ToString(), o.MatchingScope, o.EntraTenantWideGrant, o.Verdict.ToString(), o.Explanation))).ToList(),
        tests.Results.Where(r => r.Error is not null).Select(r => $"{r.Mailbox.Label}: {Clean(r.Error)}").ToList(),
        tests.Notes);

    private static ReportSecurityVerdict BuildVerdict(ReportInput input, ChangePlan? plan)
    {
        var reasons = new List<string>();
        if (input.Preflight is null)
            reasons.Add("Preflight validation was not performed.");
        else
            reasons.AddRange(input.Preflight.Checks.Where(c => c.UnderminesScoping).Select(c => $"{c.Title}: {c.Summary}"));

        if (input.Execution is null)
            reasons.Add("The configuration has not been executed.");
        else if (input.Execution.DryRun)
            reasons.Add("This was a dry run; no changes were made.");
        else if (input.Execution.Outcome != ExecutionOutcome.Completed)
            reasons.Add($"Execution outcome: {input.Execution.Outcome}.");

        if (input.Tests is null)
            reasons.Add("Authorization tests (Test-ServicePrincipalAuthorization) have not been run.");
        else
        {
            if (input.Tests.OverallVerdict == AuthorizationVerdict.Fail)
                reasons.Add($"{input.Tests.Failed} authorization test(s) failed.");
            else if (input.Tests.OverallVerdict == AuthorizationVerdict.Inconclusive)
                reasons.Add("Some authorization tests were inconclusive.");
            if (!input.Tests.Results.Any(r => r.Expectation == TestExpectation.InScope))
                reasons.Add("No positive (in-scope) tests were run.");
        }

        if (input.GrantAnalysis is null && input.Preflight is not null)
            reasons.Add("Microsoft Entra ID permissions were not verified.");

        var secure = reasons.Count == 0;
        var mailboxCount = input.Request?.Mailboxes.Count;
        var statement = secure
            ? $"Securely scoped: the requested permissions are granted through Exchange RBAC on scope '{input.Request?.ScopeName ?? plan?.ScopeName}'"
              + (mailboxCount is not null ? $" ({mailboxCount} mailbox(es))" : string.Empty)
              + ", Exchange authorization tests passed, and no tenant-wide Microsoft Entra ID permission or organization-wide Exchange assignment overlaps them."
            : "NOT confirmed as securely scoped. See the reasons below.";
        return new ReportSecurityVerdict(secure, statement, reasons);
    }

    private static IReadOnlyList<string> BuildRemaining(ReportInput input, ReportSecurityVerdict verdict)
    {
        var remaining = new List<string>
        {
            "Changes to application permissions are cached for 30 minutes to 2 hours depending on the application's recent activity. Real API calls may not reflect this change until then.",
            "Perform an end-to-end test with the application's own credentials using the example requests: one in-scope mailbox (expect success) and one out-of-scope mailbox (expect 403 ErrorAccessDenied).",
        };

        if (input.GrantAnalysis?.UnderminingGrants is { Count: > 0 } undermining)
            remaining.Add($"Remove the tenant-wide Microsoft Entra ID permission(s) {string.Join(", ", undermining.Select(g => g.PermissionValue))} from the Enterprise Application after verifying the scoped access. Until then, the mailbox restriction is not effective.");

        if (input.Tests is null || !input.Tests.Results.Any(r => r.Expectation == TestExpectation.OutOfScope))
            remaining.Add("Run at least one negative authorization test against an existing out-of-scope mailbox.");

        if (!verdict.SecurelyScoped && input.Execution is { Outcome: ExecutionOutcome.PartiallyCompleted })
            remaining.Add("Execution was only partially completed. Validate the configuration again to plan the remaining changes, or remove the objects created by this run.");

        return remaining;
    }

    private static ReportMailbox ToMailbox(ExchangeRecipient r) => new(r.DisplayName, r.PrimarySmtpAddress, r.RecipientTypeDetails, r.Guid, r.ExternalDirectoryObjectId);

    private static string? Clean(string? value) => value is null ? null : SensitiveDataRedactor.Redact(value);
}
