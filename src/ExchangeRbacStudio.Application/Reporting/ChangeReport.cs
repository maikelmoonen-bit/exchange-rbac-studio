namespace ExchangeRbacStudio.Application.Reporting;

/// <summary>
/// Structured change report exported as JSON and HTML. Contains identifiers and outcomes only; never credentials,
/// secrets or tokens.
/// </summary>
public sealed record ChangeReport
{
    public required Guid ReportId { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset GeneratedUtc { get; init; }
    public required string ToolVersion { get; init; }
    public required bool DryRun { get; init; }
    public required ReportTenant Tenant { get; init; }
    public required ReportActor Administrator { get; init; }
    public ReportApplication? Application { get; init; }
    public ReportScope? Scope { get; init; }
    public IReadOnlyList<ReportRole> Roles { get; init; } = [];
    public ReportExecution? Execution { get; init; }
    public required ReportSecurityVerdict SecurityVerdict { get; init; }
    public IReadOnlyList<ReportCheck> PreflightChecks { get; init; } = [];
    public IReadOnlyList<ReportFinding> SecurityFindings { get; init; } = [];
    public ReportAuthorizationTests? AuthorizationTests { get; init; }
    public IReadOnlyList<string> RemainingVerification { get; init; } = [];
    public IReadOnlyList<ReportGraphRequest> ExampleGraphRequests { get; init; } = [];
    public string? PowerShellEquivalent { get; init; }
}

public sealed record ReportTenant(Guid? TenantId, string DisplayName, string? DefaultDomain, string Environment);

public sealed record ReportActor(string? UserPrincipalName, string? DisplayName, string? MachineName);

public sealed record ReportApplication(string DisplayName, Guid AppId, Guid ServicePrincipalObjectId, string? ExchangeServicePrincipalStatus);

public sealed record ReportMailbox(string DisplayName, string? PrimarySmtpAddress, string? RecipientTypeDetails, Guid? ObjectGuid, string? ExternalDirectoryObjectId);

public sealed record ReportScope(
    string Name,
    string? Filter,
    string? Strategy,
    IReadOnlyList<ReportMailbox> IntendedMailboxes,
    IReadOnlyList<ReportMailbox> ResolvedMatches,
    string MembershipVerdict,
    DateTimeOffset? VerifiedUtc);

public sealed record ReportRole(string RoleName, IReadOnlyList<string> ApiPermissions, string Privilege, string Explanation);

public sealed record ReportOperation(
    int Sequence,
    string Kind,
    string Disposition,
    string Target,
    string Summary,
    string Status,
    string Message,
    string? Command,
    string? BeforeState,
    string? AfterState,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc);

public sealed record ReportExecution(
    Guid RunId,
    Guid PlanId,
    string PlanFingerprint,
    string Outcome,
    bool ChangesMade,
    string Summary,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    IReadOnlyList<ReportOperation> Operations,
    IReadOnlyList<string> CreatedObjects,
    IReadOnlyList<string> ReusedObjects,
    IReadOnlyList<string> Warnings);

public sealed record ReportSecurityVerdict(bool SecurelyScoped, string Statement, IReadOnlyList<string> Reasons);

public sealed record ReportCheck(string Id, string Title, string Status, string Summary, string? Detail, string? Remediation, bool SecurityCritical);

public sealed record ReportFinding(string Code, string Severity, string Title, string Detail, string? Remediation);

public sealed record ReportTestOutcome(string Mailbox, string Expectation, string Role, string ExchangeResult, string? Scope, bool EntraTenantWideGrant, string Verdict, string Explanation);

public sealed record ReportAuthorizationTests(DateTimeOffset ExecutedUtc, string OverallVerdict, IReadOnlyList<ReportTestOutcome> Outcomes, IReadOnlyList<string> Errors, IReadOnlyList<string> Notes);

public sealed record ReportGraphRequest(string Permission, string Method, string Url, string ExpectedInScope, string ExpectedOutOfScope, string? Notes);
