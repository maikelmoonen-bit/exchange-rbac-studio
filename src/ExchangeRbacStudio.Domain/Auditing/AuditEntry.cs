namespace ExchangeRbacStudio.Domain.Auditing;

public enum AuditOutcome
{
    Information,
    Started,
    Succeeded,
    Reused,
    Skipped,
    DryRun,
    Warning,
    Failed,
}

public static class AuditCategories
{
    public const string Session = "Session";
    public const string Validation = "Validation";
    public const string Change = "Change";
    public const string Verification = "Verification";
    public const string Report = "Report";
}

/// <summary>
/// An append-only audit record. Entries are chained with SHA-256 hashes so that removal or modification of earlier
/// entries is detectable. Entries never contain credentials or tokens.
/// </summary>
public sealed record AuditEntry
{
    public required Guid EntryId { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public Guid? RunId { get; init; }
    public Guid? TenantId { get; init; }
    public string? TenantName { get; init; }
    public string? Administrator { get; init; }
    public required string Category { get; init; }
    public required string Action { get; init; }
    public string? Target { get; init; }
    public required AuditOutcome Outcome { get; init; }
    public required string Message { get; init; }
    public string? Command { get; init; }
    public string? Details { get; init; }
    public string? MachineName { get; init; }
    public string? AppVersion { get; init; }
    public string? PreviousHash { get; init; }
    public string? Hash { get; init; }
}
