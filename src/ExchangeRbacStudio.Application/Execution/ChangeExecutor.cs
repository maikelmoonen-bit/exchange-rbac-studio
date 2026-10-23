using System.Text.RegularExpressions;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Auditing;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Inventory;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Auditing;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Planning;
using ExchangeRbacStudio.Domain.Scoping;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.Application.Execution;

/// <summary>
/// Executes a validated change plan against Exchange Online.
/// <list type="bullet">
/// <item>Requires the session to be bound to the plan's tenant and re-checks the live connection.</item>
/// <item>Detects drift by rebuilding the plan from live state; any difference aborts before the first write.</item>
/// <item>Writes the audit entry before changing anything (fails closed if the audit log is unavailable).</item>
/// <item>Executes each write exactly once and never retries it; the resulting state is queried instead.</item>
/// <item>Stops at the first failure and reports Completed / Partially completed / Failed truthfully.</item>
/// </list>
/// </summary>
public sealed partial class ChangeExecutor(
    ConnectionManager connections,
    IExchangeRbacGateway exchange,
    AuditRecorder audit,
    IChangeLock changeLock,
    RbacInventoryService inventory,
    TimeProvider time,
    ILogger<ChangeExecutor> logger)
{
    public static IReadOnlyList<StepResult> CreateInitialSteps(ChangePlan plan)
    {
        var steps = new List<StepResult>
        {
            new(0, "Connecting", ExecutionStage.Connecting, StepStatus.Pending, "Verify the Exchange Online session and target tenant."),
            new(1, "Validating", ExecutionStage.Validating, StepStatus.Pending, "Re-read live configuration and confirm it matches the reviewed plan."),
        };

        foreach (var op in plan.Operations)
            steps.Add(new(steps.Count, TitleFor(op), StageFor(op.Kind), StepStatus.Pending, op.Summary, op.Command?.ToPreview(), Operation: op));

        steps.Add(new(steps.Count, "Verifying configuration", ExecutionStage.Verifying, StepStatus.Pending, "Confirm the final state in Exchange Online."));
        return steps;
    }

    public async Task<ExecutionResult> ExecuteAsync(ChangePlan plan, ExecutionOptions options, IProgress<StepResult>? progress, CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        var started = time.GetUtcNow();
        var steps = CreateInitialSteps(plan).ToList();
        var warnings = new List<string>();
        var created = new List<PlannedOperation>();
        var succeededWrites = new List<PlannedOperation>();
        var failed = false;
        var stopped = false;
        SessionSnapshot? session = null;

        void Set(int index, StepStatus status, string message, string? errorDetail = null, DateTimeOffset? startedUtc = null)
        {
            var completed = status is StepStatus.Running or StepStatus.Pending ? (DateTimeOffset?)null : time.GetUtcNow();
            steps[index] = steps[index] with
            {
                Status = status,
                Message = message,
                ErrorDetail = errorDetail,
                StartedUtc = startedUtc ?? steps[index].StartedUtc ?? (status == StepStatus.Running ? time.GetUtcNow() : null),
                CompletedUtc = completed,
            };
            progress?.Report(steps[index]);
        }

        void MarkRemainingNotRun(int fromIndex, string reason)
        {
            for (var i = fromIndex; i < steps.Count; i++)
                if (steps[i].Status == StepStatus.Pending) Set(i, StepStatus.NotRun, reason);
        }

        using var lockHandle = changeLock.TryAcquire(plan.TenantId);
        if (lockHandle is null)
        {
            Set(0, StepStatus.Failed, "Another change execution for this tenant is already running on this computer. No changes were made.");
            MarkRemainingNotRun(1, "Not run.");
            return Finish(runId, plan, ExecutionOutcome.Failed, false, steps, started, options.DryRun, created, warnings, "Execution was blocked by a concurrent change. No changes were made.");
        }

        // Stage: connecting.
        Set(0, StepStatus.Running, "Checking the Exchange Online session…");
        try
        {
            session = connections.RequireFullSession(plan.TenantId);
            var info = await exchange.GetConnectionInfoAsync(cancellationToken).ConfigureAwait(false);
            if (info is null || !info.IsConnected)
                throw new StudioOperationException(FailureKind.SessionExpired, "The Exchange Online session is not active.");
            if (info.TenantId != plan.TenantId)
                throw new StudioOperationException(FailureKind.TenantMismatch, $"The Exchange Online session is bound to tenant {info.TenantId}, but the plan targets {plan.TenantId}.");
            Set(0, StepStatus.Succeeded, $"Connected to {session.TenantDisplayName} ({plan.TenantId}) as {info.UserPrincipalName}.");
        }
        catch (Exception ex)
        {
            Set(0, StepStatus.Failed, UserFacingError.From(ex).Message + " No changes were made.", (ex as StudioOperationException)?.TechnicalDetail);
            MarkRemainingNotRun(1, "Not run.");
            return Finish(runId, plan, ExecutionOutcome.Failed, false, steps, started, options.DryRun, created, warnings, "The session could not be verified. No changes were made.");
        }

        // Stage: validating (plan integrity and drift detection).
        Set(1, StepStatus.Running, "Re-reading the live configuration…");
        try
        {
            if (plan.HasConflicts)
                throw new StudioOperationException(FailureKind.Conflict, $"The plan contains {plan.Conflicts.Count} unresolved conflict(s).");
            if (!string.Equals(plan.Fingerprint, options.ValidatedFingerprint, StringComparison.Ordinal))
                throw new StudioOperationException(FailureKind.Conflict, "The plan differs from the plan that was validated. Validate the configuration again.");
            if (options.Replan is not null)
            {
                var fresh = await options.Replan(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(fresh.Fingerprint, plan.Fingerprint, StringComparison.Ordinal))
                    throw new StudioOperationException(FailureKind.ConcurrentChange,
                        "The Exchange configuration changed after it was validated (for example, another administrator made a change). Review and validate the configuration again.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (options.DryRun)
            {
                await audit.RecordAsync(session, AuditCategories.Change, "Dry run started", AuditOutcome.DryRun, plan.Title, plan.Application?.DisplayName, runId,
                    details: $"Plan {plan.PlanId}; fingerprint {plan.Fingerprint}").ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await audit.RecordRequiredAsync(session, AuditCategories.Change, "Execution started", AuditOutcome.Started, plan.Title, plan.Application?.DisplayName, runId,
                        details: $"Plan {plan.PlanId}; fingerprint {plan.Fingerprint}; {plan.WriteOperations.Count} write operation(s)", cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new StudioOperationException(FailureKind.Unknown, "The audit log could not be written, so execution was not started.", ex.Message, inner: ex);
                }
            }

            Set(1, StepStatus.Succeeded, options.Replan is null
                ? "The plan matches the validated plan."
                : "Live configuration still matches the validated plan.");
        }
        catch (Exception ex)
        {
            Set(1, StepStatus.Failed, UserFacingError.From(ex).Message + " No changes were made.", (ex as StudioOperationException)?.TechnicalDetail);
            MarkRemainingNotRun(2, "Not run.");
            return Finish(runId, plan, ExecutionOutcome.Failed, false, steps, started, options.DryRun, created, warnings, "Validation before execution failed. No changes were made.");
        }

        // Stage: operations.
        for (var index = 2; index < steps.Count - 1; index++)
        {
            var op = steps[index].Operation!;
            if (failed)
            {
                Set(index, StepStatus.NotRun, "Not run because a previous step failed.");
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                stopped = true;
                Set(index, StepStatus.NotRun, "Not run: execution was stopped by the administrator.");
                continue;
            }

            if (!op.RequiresExecution)
            {
                Set(index, StepStatus.Reused, op.Summary);
                await audit.RecordAsync(session, AuditCategories.Change, op.Kind.ToString(), AuditOutcome.Reused, op.Summary, op.Target, runId).ConfigureAwait(false);
                continue;
            }

            var preview = op.Command!.ToPreview();
            if (options.DryRun)
            {
                Set(index, StepStatus.DryRun, "Dry run: would execute this command.");
                await audit.RecordAsync(session, AuditCategories.Change, op.Command.Name, AuditOutcome.DryRun, op.Summary, op.Target, runId, preview).ConfigureAwait(false);
                continue;
            }

            var stepStarted = time.GetUtcNow();
            Set(index, StepStatus.Running, $"Running {op.Command.Name}…", startedUtc: stepStarted);
            await audit.RecordAsync(session, AuditCategories.Change, op.Command.Name, AuditOutcome.Started, op.Summary, op.Target, runId, preview).ConfigureAwait(false);

            StudioOperationException? error = null;
            ExchangeCommandResult? result = null;
            try
            {
                // Writes are never cancelled mid-flight and never retried automatically.
                result = await exchange.ExecuteAsync(op.Command, CancellationToken.None).ConfigureAwait(false);
            }
            catch (StudioOperationException ex)
            {
                error = ex;
            }
            catch (Exception ex)
            {
                error = new StudioOperationException(FailureKind.Unknown, ex.Message, ex.ToString(), inner: ex);
            }

            var (verified, verification) = await VerifyOperationAsync(op).ConfigureAwait(false);
            if (verified)
            {
                succeededWrites.Add(op);
                if (op.Disposition == OperationDisposition.Create) created.Add(op);
                if (result is { Warnings.Count: > 0 }) warnings.AddRange(result.Warnings.Select(w => $"{op.Command.Name}: {w}"));

                var message = error is null
                    ? $"Completed and verified. {verification}"
                    : $"Exchange reported an error ({error.Message}), but querying the resulting state confirmed the change: {verification}";
                Set(index, StepStatus.Succeeded, message, error?.TechnicalDetail, stepStarted);
                await audit.RecordAsync(session, AuditCategories.Change, op.Command.Name, AuditOutcome.Succeeded, message, op.Target, runId, preview).ConfigureAwait(false);
            }
            else
            {
                failed = true;
                var message = error is null
                    ? $"The command returned without an error, but the expected result could not be confirmed: {verification}"
                    : $"{error.Message} State check: {verification}";
                Set(index, StepStatus.Failed, message, error?.TechnicalDetail, stepStarted);
                await audit.RecordAsync(session, AuditCategories.Change, op.Command.Name, AuditOutcome.Failed, message, op.Target, runId, preview, error?.TechnicalDetail).ConfigureAwait(false);
                logger.LogWarning("Change step failed: {Command} on {Target}: {Message}", op.Command.Name, op.Target, message);
            }
        }

        // Stage: final verification.
        var last = steps.Count - 1;
        if (options.DryRun)
        {
            Set(last, StepStatus.DryRun, "Dry run: no changes were made, so there is nothing to verify.");
        }
        else if (failed || stopped)
        {
            Set(last, StepStatus.NotRun, failed ? "Not run because a step failed." : "Not run because execution was stopped.");
        }
        else
        {
            Set(last, StepStatus.Running, "Re-reading the final configuration…");
            var problems = new List<string>();
            foreach (var op in succeededWrites)
            {
                var (ok, message) = await VerifyOperationAsync(op).ConfigureAwait(false);
                if (!ok) problems.Add($"{op.Target}: {message}");
            }

            if (problems.Count == 0)
            {
                Set(last, StepStatus.Succeeded, succeededWrites.Count == 0
                    ? "No changes were required; the configuration already matched."
                    : $"All {succeededWrites.Count} change(s) are present in Exchange Online.");
            }
            else
            {
                failed = true;
                Set(last, StepStatus.Failed, "Final verification found differences: " + string.Join("; ", problems));
            }
        }

        var changesMade = succeededWrites.Count > 0;
        var outcome = options.DryRun ? ExecutionOutcome.DryRunCompleted
            : !failed && !stopped ? ExecutionOutcome.Completed
            : changesMade ? ExecutionOutcome.PartiallyCompleted
            : ExecutionOutcome.Failed;

        if (changesMade) inventory.Invalidate();

        var summary = outcome switch
        {
            ExecutionOutcome.Completed => succeededWrites.Count == 0 ? "Completed: the configuration already matched; nothing was changed." : $"Completed: {succeededWrites.Count} change(s) executed and verified.",
            ExecutionOutcome.PartiallyCompleted => $"Partially completed: {succeededWrites.Count} change(s) were made before execution {(stopped ? "was stopped" : "failed")}. Review the steps; re-validating will plan only the remaining changes.",
            ExecutionOutcome.DryRunCompleted => $"Dry run completed: {plan.WriteOperations.Count} change(s) would be executed. Nothing was changed.",
            _ => "Failed: no changes were made.",
        };

        await audit.RecordAsync(session, AuditCategories.Change, options.DryRun ? "Dry run completed" : "Execution completed",
            outcome switch
            {
                ExecutionOutcome.Completed => AuditOutcome.Succeeded,
                ExecutionOutcome.DryRunCompleted => AuditOutcome.DryRun,
                ExecutionOutcome.PartiallyCompleted => AuditOutcome.Warning,
                _ => AuditOutcome.Failed,
            },
            summary, plan.Application?.DisplayName, runId).ConfigureAwait(false);

        return Finish(runId, plan, outcome, changesMade, steps, started, options.DryRun, created, warnings, summary);
    }

    /// <summary>Queries Exchange to determine whether an operation's intended result is in place.</summary>
    internal async Task<(bool Ok, string Message)> VerifyOperationAsync(PlannedOperation op)
    {
        var subject = op.Subject;
        var ct = CancellationToken.None;
        try
        {
            switch (op.Kind)
            {
                case OperationKind.CreateServicePrincipal:
                {
                    var pointers = await exchange.GetServicePrincipalsAsync(ct).ConfigureAwait(false);
                    return pointers.Any(p => p.Matches(subject.AppId ?? Guid.Empty, subject.ServicePrincipalObjectId ?? Guid.Empty))
                        ? (true, "The service principal pointer exists with the expected identifiers.")
                        : (false, "No pointer with the expected AppId and ObjectId exists.");
                }

                case OperationKind.RemoveServicePrincipal:
                {
                    var pointers = await exchange.GetServicePrincipalsAsync(ct).ConfigureAwait(false);
                    return pointers.Any(p => p.ObjectId == subject.ServicePrincipalObjectId || (subject.AppId is not null && p.AppId == subject.AppId))
                        ? (false, "The pointer still exists.")
                        : (true, "The pointer no longer exists.");
                }

                case OperationKind.CreateManagementScope or OperationKind.UpdateManagementScope:
                {
                    var scope = await exchange.GetManagementScopeAsync(subject.ScopeName!, ct).ConfigureAwait(false);
                    if (scope is null) return (false, $"Scope '{subject.ScopeName}' was not found.");
                    return FiltersEquivalent(scope.RecipientFilter, subject.Filter)
                        ? (true, $"Scope '{scope.Name}' exists with the expected filter.")
                        : (false, $"Scope '{scope.Name}' exists but its filter is '{scope.RecipientFilter}'.");
                }

                case OperationKind.RemoveManagementScope:
                {
                    var scope = await exchange.GetManagementScopeAsync(subject.ScopeName ?? subject.ObjectName!, ct).ConfigureAwait(false);
                    return scope is null ? (true, "The scope no longer exists.") : (false, "The scope still exists.");
                }

                case OperationKind.CreateRoleAssignment:
                {
                    var assignments = await exchange.GetApplicationRoleAssignmentsAsync([subject.Role!], ct).ConfigureAwait(false);
                    var pointers = await exchange.GetServicePrincipalsAsync(ct).ConfigureAwait(false);
                    var pointer = pointers.FirstOrDefault(p => p.ObjectId == subject.ServicePrincipalObjectId);
                    var match = assignments.FirstOrDefault(a => a.Role.Equals(subject.Role, StringComparison.OrdinalIgnoreCase)
                        && (pointer is not null ? a.IsAssignedTo(pointer) : string.Equals(a.RoleAssigneeName, subject.ServicePrincipalObjectId?.ToString(), StringComparison.OrdinalIgnoreCase))
                        && ScopeMatches(a, subject.ScopeName));
                    return match is not null
                        ? (true, $"Assignment '{match.Name}' grants {match.Role} on {match.ScopeReference}.")
                        : (false, $"No assignment of {subject.Role} on '{subject.ScopeName}' was found for the service principal.");
                }

                case OperationKind.UpdateRoleAssignmentScope:
                {
                    var assignments = await exchange.GetApplicationRoleAssignmentsAsync([subject.Role!], ct).ConfigureAwait(false);
                    var match = assignments.FirstOrDefault(a => a.Name.Equals(subject.ObjectName, StringComparison.OrdinalIgnoreCase));
                    if (match is null) return (false, $"Assignment '{subject.ObjectName}' was not found.");
                    return ScopeMatches(match, subject.ScopeName)
                        ? (true, $"Assignment '{match.Name}' is scoped to '{match.ScopeReference}'.")
                        : (false, $"Assignment '{match.Name}' is scoped to '{match.ScopeReference ?? "organization"}'.");
                }

                case OperationKind.RemoveRoleAssignment:
                {
                    var assignments = await exchange.GetApplicationRoleAssignmentsAsync([subject.Role!], ct).ConfigureAwait(false);
                    return assignments.Any(a => a.Name.Equals(subject.ObjectName, StringComparison.OrdinalIgnoreCase))
                        ? (false, "The assignment still exists.")
                        : (true, "The assignment no longer exists.");
                }

                default:
                    return (false, $"No verification is defined for {op.Kind}.");
            }
        }
        catch (StudioOperationException ex)
        {
            return (false, "The state could not be queried: " + ex.Message);
        }
    }

    internal static bool FiltersEquivalent(string? actual, string? expected)
    {
        if (RecipientFilterAnalyzer.AreEquivalentExactFilters(actual, expected)) return true;
        return string.Equals(Normalize(actual), Normalize(expected), StringComparison.OrdinalIgnoreCase);

        static string Normalize(string? value) => Whitespace().Replace(value ?? string.Empty, " ").Trim();
    }

    private static bool ScopeMatches(ManagementRoleAssignment assignment, string? scopeName) =>
        scopeName is not null && assignment.ScopeReference is { } reference
        && (reference.Equals(scopeName, StringComparison.OrdinalIgnoreCase) || reference.EndsWith("\\" + scopeName, StringComparison.OrdinalIgnoreCase));

    private ExecutionResult Finish(Guid runId, ChangePlan plan, ExecutionOutcome outcome, bool changesMade, List<StepResult> steps, DateTimeOffset started,
        bool dryRun, List<PlannedOperation> created, List<string> warnings, string summary)
    {
        logger.LogInformation("Run {RunId} for plan {PlanId} finished: {Outcome}", runId, plan.PlanId, outcome);
        return new ExecutionResult(runId, plan, outcome, changesMade, steps, started, time.GetUtcNow(), dryRun, summary, created, warnings);
    }

    private static string TitleFor(PlannedOperation op) => op.Kind switch
    {
        OperationKind.CreateServicePrincipal => op.Disposition == OperationDisposition.Reuse ? "Exchange service principal (reuse)" : "Creating Exchange service principal",
        OperationKind.RemoveServicePrincipal => "Removing Exchange service principal",
        OperationKind.CreateManagementScope => op.Disposition == OperationDisposition.Reuse ? "Management scope (reuse)" : "Creating management scope",
        OperationKind.UpdateManagementScope => "Updating management scope",
        OperationKind.RemoveManagementScope => "Removing management scope",
        OperationKind.CreateRoleAssignment => op.Disposition == OperationDisposition.Reuse ? $"Role {op.Subject.Role} (reuse)" : $"Assigning {op.Subject.Role}",
        OperationKind.UpdateRoleAssignmentScope => $"Rescoping {op.Subject.Role ?? op.Target}",
        OperationKind.RemoveRoleAssignment => $"Removing {op.Subject.Role ?? op.Target}",
        _ => op.Summary,
    };

    private static ExecutionStage StageFor(OperationKind kind) => kind switch
    {
        OperationKind.CreateServicePrincipal => ExecutionStage.CreatingServicePrincipal,
        OperationKind.RemoveServicePrincipal => ExecutionStage.RemovingServicePrincipal,
        OperationKind.CreateManagementScope => ExecutionStage.CreatingScope,
        OperationKind.UpdateManagementScope => ExecutionStage.UpdatingScope,
        OperationKind.RemoveManagementScope => ExecutionStage.RemovingScope,
        OperationKind.CreateRoleAssignment => ExecutionStage.AssigningRoles,
        OperationKind.UpdateRoleAssignmentScope => ExecutionStage.UpdatingAssignments,
        OperationKind.RemoveRoleAssignment => ExecutionStage.RemovingAssignments,
        _ => ExecutionStage.Validating,
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
