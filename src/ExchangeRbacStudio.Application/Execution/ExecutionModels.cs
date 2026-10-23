using ExchangeRbacStudio.Domain.Planning;

namespace ExchangeRbacStudio.Application.Execution;

public enum ExecutionStage
{
    Connecting,
    Validating,
    CreatingServicePrincipal,
    CreatingScope,
    UpdatingScope,
    AssigningRoles,
    UpdatingAssignments,
    RemovingAssignments,
    RemovingScope,
    RemovingServicePrincipal,
    Verifying,
}

public enum StepStatus
{
    Pending,
    Running,
    Succeeded,
    Reused,
    DryRun,
    NotRun,
    Failed,
}

public enum ExecutionOutcome
{
    Completed,
    PartiallyCompleted,
    Failed,
    DryRunCompleted,
}

public sealed record StepResult(
    int Index,
    string Title,
    ExecutionStage Stage,
    StepStatus Status,
    string Message,
    string? Command = null,
    string? ErrorDetail = null,
    DateTimeOffset? StartedUtc = null,
    DateTimeOffset? CompletedUtc = null,
    PlannedOperation? Operation = null)
{
    public TimeSpan? Duration => StartedUtc is { } s && CompletedUtc is { } c ? c - s : null;
}

/// <param name="DryRun">Validate and walk the plan without executing any write command.</param>
/// <param name="ValidatedFingerprint">Fingerprint of the plan the administrator reviewed and validated.</param>
/// <param name="Replan">Re-reads live state and rebuilds the plan; any difference aborts execution before writes (drift detection).</param>
public sealed record ExecutionOptions(bool DryRun, string ValidatedFingerprint, Func<CancellationToken, Task<ChangePlan>>? Replan = null);

public sealed record ExecutionResult(
    Guid RunId,
    ChangePlan Plan,
    ExecutionOutcome Outcome,
    bool ChangesMade,
    IReadOnlyList<StepResult> Steps,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    bool DryRun,
    string Summary,
    IReadOnlyList<PlannedOperation> CreatedObjects,
    IReadOnlyList<string> Warnings);
