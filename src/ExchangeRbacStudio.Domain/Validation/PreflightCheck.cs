namespace ExchangeRbacStudio.Domain.Validation;

public enum CheckStatus
{
    Passed,
    Info,
    Warning,

    /// <summary>The check could not be completed. For security-critical checks this is treated as blocking.</summary>
    UnableToVerify,

    Blocked,
}

/// <summary>Result of a single preflight check.</summary>
public sealed record PreflightCheck(
    string Id,
    string Title,
    CheckStatus Status,
    string Summary,
    string? Detail = null,
    string? Remediation = null,
    bool IsSecurityCritical = false,
    bool CanAcknowledge = false)
{
    /// <summary>True when this result prevents execution unless the administrator explicitly acknowledges it.</summary>
    public bool IsBlocking => Status == CheckStatus.Blocked || (Status == CheckStatus.UnableToVerify && IsSecurityCritical);

    /// <summary>True when this result prevents execution regardless of acknowledgement.</summary>
    public bool IsHardBlock => IsBlocking && !CanAcknowledge;

    /// <summary>True when this result means the configuration must not be presented as securely scoped.</summary>
    public bool UnderminesScoping => IsSecurityCritical && (Status == CheckStatus.Blocked || Status == CheckStatus.UnableToVerify);
}

public sealed record PreflightReport(IReadOnlyList<PreflightCheck> Checks, DateTimeOffset CompletedUtc)
{
    public static PreflightReport Empty { get; } = new([], DateTimeOffset.MinValue);

    public bool HasHardBlock => Checks.Any(c => c.IsHardBlock);

    public IReadOnlyList<PreflightCheck> AcknowledgeableBlocks => Checks.Where(c => c.IsBlocking && c.CanAcknowledge).ToList();

    /// <summary>
    /// Execution is permitted only when no check hard-blocks and every acknowledgeable block has been acknowledged.
    /// </summary>
    public bool CanProceed(bool acknowledged) => !HasHardBlock && (AcknowledgeableBlocks.Count == 0 || acknowledged);

    /// <summary>
    /// The resulting configuration may only be described as securely scoped when no security-critical check
    /// failed or was unverifiable.
    /// </summary>
    public bool IsSecurelyScoped => Checks.Count > 0 && !Checks.Any(c => c.UnderminesScoping);

    public int Count(CheckStatus status) => Checks.Count(c => c.Status == status);

    public CheckStatus WorstStatus => Checks.Count == 0 ? CheckStatus.Passed : Checks.Max(c => c.Status);
}
