using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Configuration;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Execution;
using ExchangeRbacStudio.Domain.Auditing;
using ExchangeRbacStudio.Domain.Planning;

namespace ExchangeRbacStudio.Tests.Application;

public sealed class IdempotentConfigurationTests
{
    [Fact]
    public async Task Empty_tenant_plans_pointer_scope_and_assignments()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var (_, plan) = await env.PrepareAsync(env.Request(roles: ["Application Mail.Read", "Application Mail.Send"]));

        Assert.False(plan.HasConflicts);
        Assert.Equal(
            [ExchangeCommands.NewServicePrincipal, ExchangeCommands.NewManagementScope, ExchangeCommands.NewManagementRoleAssignment, ExchangeCommands.NewManagementRoleAssignment],
            plan.WriteOperations.Select(o => o.Command!.Name));
    }

    [Fact]
    public async Task Executing_the_same_configuration_twice_changes_nothing_the_second_time()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var request = env.Request();
        var (_, plan) = await env.PrepareAsync(request);
        var first = await env.ExecuteAsync(plan, request);
        Assert.Equal(ExecutionOutcome.Completed, first.Outcome);
        var commandsAfterFirst = env.Exchange.ExecutedCommands.Count;

        var (_, second) = await env.PrepareAsync(request);
        Assert.False(second.HasChanges);
        Assert.All(second.Operations, o => Assert.Equal(OperationDisposition.Reuse, o.Disposition));

        var result = await env.ExecuteAsync(second, request);
        Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
        Assert.False(result.ChangesMade);
        Assert.Equal(commandsAfterFirst, env.Exchange.ExecutedCommands.Count);
    }

    [Fact]
    public async Task Existing_equivalent_scope_and_assignment_are_reused()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var request = env.Request();
        var pointer = env.Exchange.SeedPointer(Fixtures.Polo);
        env.Exchange.SeedScope(request.ScopeName, request.ScopeFilter);
        env.Exchange.SeedAssignment(pointer, "Application Mail.Read", request.ScopeName);

        var (_, plan) = await env.PrepareAsync(request with { Roles = ["Application Mail.Read", "Application Calendars.Read"] });
        var write = Assert.Single(plan.WriteOperations);
        Assert.Equal(ExchangeCommands.NewManagementRoleAssignment, write.Command!.Name);
        Assert.Equal("Application Calendars.Read", write.Subject.Role);
    }

    [Fact]
    public async Task New_mode_never_removes_existing_assignments()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var request = env.Request();
        var pointer = env.Exchange.SeedPointer(Fixtures.Polo);
        env.Exchange.SeedScope(request.ScopeName, request.ScopeFilter);
        env.Exchange.SeedAssignment(pointer, "Application Mail.Send", request.ScopeName);

        var (_, plan) = await env.PrepareAsync(request);
        Assert.DoesNotContain(plan.Operations, o => o.Disposition == OperationDisposition.Remove);
    }

    [Fact]
    public async Task Modify_mode_updates_an_owned_scope_and_removes_deselected_roles()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var original = env.Request(mailboxes: [Fixtures.Alice]);
        var pointer = env.Exchange.SeedPointer(Fixtures.Polo);
        env.Exchange.SeedScope(original.ScopeName, original.ScopeFilter);
        env.Exchange.SeedAssignment(pointer, "Application Mail.Read", original.ScopeName);
        env.Exchange.SeedAssignment(pointer, "Application Mail.Send", original.ScopeName);

        var modified = env.Request(mailboxes: [Fixtures.Alice, Fixtures.FinanceShared], roles: ["Application Mail.Read"], mode: ConfigurationMode.Modify, existingScopeName: original.ScopeName);
        var (_, plan) = await env.PrepareAsync(modified);

        Assert.False(plan.HasConflicts);
        var update = Assert.Single(plan.Operations, o => o.Kind == OperationKind.UpdateManagementScope);
        Assert.Equal(original.ScopeFilter, update.BeforeState);
        Assert.Equal(modified.ScopeFilter, update.AfterState);
        Assert.Single(plan.Operations, o => o.Kind == OperationKind.RemoveRoleAssignment && o.Subject.Role == "Application Mail.Send");

        var result = await env.ExecuteAsync(plan, modified);
        Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
        Assert.Single(env.Exchange.Assignments);
    }

    [Fact]
    public async Task Modify_mode_refuses_to_change_a_scope_shared_with_other_assignments()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var original = env.Request(mailboxes: [Fixtures.Alice]);
        var pointer = env.Exchange.SeedPointer(Fixtures.Polo);
        env.Exchange.SeedScope(original.ScopeName, original.ScopeFilter);
        env.Exchange.SeedAssignment(pointer, "Application Mail.Read", original.ScopeName);
        env.Exchange.AdministratorScopeReferences.Add(new ScopeReference("Helpdesk-" + original.ScopeName, "Mail Recipients", "Helpdesk", "RoleGroup", original.ScopeName));

        var modified = env.Request(mailboxes: [Fixtures.Alice, Fixtures.Bob], mode: ConfigurationMode.Modify, existingScopeName: original.ScopeName);
        var (_, plan) = await env.PrepareAsync(modified);
        Assert.Contains(plan.Conflicts, c => c.Code == "SCOPE_SHARED");
    }
}

public sealed class ChangeExecutionTests
{
    [Fact]
    public async Task Successful_execution_is_verified_and_audited()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var request = env.Request(roles: ["Application Mail.Read", "Application Mail.Send"]);
        var (_, plan) = await env.PrepareAsync(request);
        var result = await env.ExecuteAsync(plan, request);

        Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
        Assert.All(result.Steps, s => Assert.Contains(s.Status, new[] { StepStatus.Succeeded, StepStatus.Reused }));
        Assert.Equal(4, result.CreatedObjects.Count);
        Assert.Contains(env.Audit.Entries, e => e.Action == "Execution started" && e.Outcome == AuditOutcome.Started);
        Assert.Contains(env.Audit.Entries, e => e.Action == "Execution completed" && e.Outcome == AuditOutcome.Succeeded);
        Assert.Equal(4, env.Audit.Entries.Count(e => e.Category == AuditCategories.Change && e.Outcome == AuditOutcome.Succeeded && e.Command is not null));
    }

    [Fact]
    public async Task Preview_matches_exactly_what_is_executed()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var request = env.Request();
        var (_, plan) = await env.PrepareAsync(request);
        await env.ExecuteAsync(plan, request);
        Assert.Equal(plan.WriteOperations.Select(o => o.Command!.ToPreview()), env.Exchange.ExecutedCommands.Select(c => c.ToPreview()));
    }

    [Fact]
    public async Task Failure_mid_way_is_partially_completed_and_later_steps_are_not_run()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var request = env.Request(roles: ["Application Mail.Read", "Application Mail.Send"]);
        var (_, plan) = await env.PrepareAsync(request);
        env.Exchange.WriteFailures[ExchangeCommands.NewManagementRoleAssignment] =
            new FailureInjection(new StudioOperationException(FailureKind.AccessDenied, "The role assignment isn't within your current write scope."), Occurrence: 2);

        var result = await env.ExecuteAsync(plan, request);

        Assert.Equal(ExecutionOutcome.PartiallyCompleted, result.Outcome);
        Assert.True(result.ChangesMade);
        Assert.Equal(3, result.CreatedObjects.Count);
        Assert.Contains(result.Steps, s => s.Status == StepStatus.Failed);
        Assert.Equal(StepStatus.NotRun, result.Steps[^1].Status);
        Assert.Contains("Partially completed", result.Summary);
    }

    [Fact]
    public async Task Partial_execution_is_recovered_by_planning_only_the_remaining_changes()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var request = env.Request(roles: ["Application Mail.Read", "Application Mail.Send"]);
        var (_, plan) = await env.PrepareAsync(request);
        env.Exchange.WriteFailures[ExchangeCommands.NewManagementRoleAssignment] = new FailureInjection(new StudioOperationException(FailureKind.Network, "Connection reset."), Occurrence: 2);
        await env.ExecuteAsync(plan, request);
        env.Exchange.WriteFailures.Clear();

        var (_, recovery) = await env.PrepareAsync(request);
        var remaining = Assert.Single(recovery.WriteOperations);
        Assert.Equal("Application Mail.Send", remaining.Subject.Role);

        var result = await env.ExecuteAsync(recovery, request);
        Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(2, env.Exchange.Assignments.Count);
    }

    [Fact]
    public async Task Write_errors_are_never_retried_and_the_resulting_state_is_queried_instead()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var request = env.Request();
        var (_, plan) = await env.PrepareAsync(request);
        env.Exchange.WriteFailures[ExchangeCommands.NewManagementScope] =
            new FailureInjection(new StudioOperationException(FailureKind.Timeout, "No response within 5 minutes."), ApplyChange: true);

        var result = await env.ExecuteAsync(plan, request);

        Assert.Equal(1, env.Exchange.ExecutedCommands.Count(c => c.Name == ExchangeCommands.NewManagementScope));
        var scopeStep = result.Steps.Single(s => s.Operation?.Kind == OperationKind.CreateManagementScope);
        Assert.Equal(StepStatus.Succeeded, scopeStep.Status);
        Assert.Contains("confirmed the change", scopeStep.Message);
        Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task Write_error_without_resulting_state_fails_without_retry()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var request = env.Request();
        var (_, plan) = await env.PrepareAsync(request);
        env.Exchange.WriteFailures[ExchangeCommands.NewServicePrincipal] = new FailureInjection(new StudioOperationException(FailureKind.Throttled, "Server busy."));

        var result = await env.ExecuteAsync(plan, request);

        Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
        Assert.False(result.ChangesMade);
        Assert.Single(env.Exchange.ExecutedCommands);
        Assert.Contains("no changes were made", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Configuration_drift_after_validation_aborts_before_any_write()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var request = env.Request();
        var (_, plan) = await env.PrepareAsync(request);
        env.Exchange.SeedPointer(Fixtures.Polo);

        var result = await env.ExecuteAsync(plan, request);

        Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
        Assert.Empty(env.Exchange.ExecutedCommands);
        Assert.Equal(StepStatus.Failed, result.Steps[1].Status);
        Assert.Contains("changed after it was validated", result.Steps[1].Message);
    }

    [Fact]
    public async Task Plan_that_differs_from_the_validated_fingerprint_is_refused()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var (_, plan) = await env.PrepareAsync(env.Request());
        var result = await env.Executor.ExecuteAsync(plan, new ExecutionOptions(false, "DIFFERENT"), null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
        Assert.Empty(env.Exchange.ExecutedCommands);
    }

    [Fact]
    public async Task Concurrent_execution_is_prevented()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var (_, plan) = await env.PrepareAsync(env.Request());
        env.ChangeLock.Available = false;
        var result = await env.ExecuteAsync(plan);
        Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
        Assert.Empty(env.Exchange.ExecutedCommands);
        Assert.Contains("concurrent", result.Summary);
    }

    [Fact]
    public async Task Unwritable_audit_log_fails_closed()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var (_, plan) = await env.PrepareAsync(env.Request());
        env.Audit.FailWrites = true;
        var result = await env.ExecuteAsync(plan);
        Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
        Assert.Empty(env.Exchange.ExecutedCommands);
    }

    [Fact]
    public async Task Expired_session_is_detected_before_writing()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var (_, plan) = await env.PrepareAsync(env.Request());
        env.Exchange.Connection = env.Exchange.Connection! with { TokenStatus = "Expired" };
        var result = await env.ExecuteAsync(plan);
        Assert.Equal(StepStatus.Failed, result.Steps[0].Status);
        Assert.Empty(env.Exchange.ExecutedCommands);
    }

    [Fact]
    public async Task Plan_for_another_tenant_is_refused()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var (_, plan) = await env.PrepareAsync(env.Request());
        var foreign = plan with { TenantId = Fixtures.OtherTenantId };
        var result = await env.Executor.ExecuteAsync(foreign, new ExecutionOptions(false, foreign.Fingerprint), null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
        Assert.Empty(env.Exchange.ExecutedCommands);
    }

    [Fact]
    public async Task Dry_run_never_executes_write_commands()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var request = env.Request();
        var (_, plan) = await env.PrepareAsync(request);
        var result = await env.ExecuteAsync(plan, request, dryRun: true);
        Assert.Equal(ExecutionOutcome.DryRunCompleted, result.Outcome);
        Assert.Empty(env.Exchange.ExecutedCommands);
        Assert.All(result.Steps.Where(s => s.Operation?.RequiresExecution == true), s => Assert.Equal(StepStatus.DryRun, s.Status));
    }

    [Fact]
    public async Task Stopping_between_steps_leaves_remaining_steps_not_run()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var request = env.Request(roles: ["Application Mail.Read", "Application Mail.Send"]);
        var (_, plan) = await env.PrepareAsync(request);
        using var cts = new CancellationTokenSource();
        env.Exchange.AfterWrite = command => { if (command.Name == ExchangeCommands.NewManagementScope) cts.Cancel(); };

        var result = await env.Executor.ExecuteAsync(plan, new ExecutionOptions(false, plan.Fingerprint), null, cts.Token);

        Assert.Equal(ExecutionOutcome.PartiallyCompleted, result.Outcome);
        Assert.Equal(2, env.Exchange.ExecutedCommands.Count);
        Assert.Equal(2, result.Steps.Count(s => s.Status == StepStatus.NotRun && s.Operation is not null));
    }
}

public sealed class ManagementPlanTests
{
    [Fact]
    public async Task Scope_in_use_cannot_be_removed()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var pointer = env.Exchange.SeedPointer(Fixtures.Polo);
        env.Exchange.SeedScope("SCOPE_X", "Guid -eq 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa'");
        env.Exchange.SeedAssignment(pointer, "Application Mail.Read", "SCOPE_X");

        var prepared = await env.Plans.RemoveScopeAsync("SCOPE_X", CancellationToken.None);
        var conflict = Assert.Single(prepared.Plan.Conflicts);
        Assert.Equal("SCOPE_IN_USE", conflict.Code);

        var result = await env.Executor.ExecuteAsync(prepared.Plan, new ExecutionOptions(false, prepared.Plan.Fingerprint, prepared.Replan), null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
        Assert.Single(env.Exchange.Scopes);
    }

    [Fact]
    public async Task Unused_scope_can_be_removed()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Exchange.SeedScope("SCOPE_X", "Guid -eq 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa'");
        var prepared = await env.Plans.RemoveScopeAsync("SCOPE_X", CancellationToken.None);
        var result = await env.Executor.ExecuteAsync(prepared.Plan, new ExecutionOptions(false, prepared.Plan.Fingerprint, prepared.Replan), null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
        Assert.Empty(env.Exchange.Scopes);
    }

    [Fact]
    public async Task Assignment_can_be_rescoped_and_removed()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var pointer = env.Exchange.SeedPointer(Fixtures.Polo);
        env.Exchange.SeedScope("SCOPE_A", "Guid -eq 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa'");
        env.Exchange.SeedScope("SCOPE_B", "Guid -eq 'bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb'");
        var assignment = env.Exchange.SeedAssignment(pointer, "Application Mail.Read", "SCOPE_A", "RA_POLO_MAIL_READ");

        var rescope = await env.Plans.ChangeAssignmentScopeAsync(assignment, "SCOPE_B", CancellationToken.None);
        Assert.Equal("SCOPE_A", rescope.Plan.Operations[0].BeforeState);
        var rescoped = await env.Executor.ExecuteAsync(rescope.Plan, new ExecutionOptions(false, rescope.Plan.Fingerprint, rescope.Replan), null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Completed, rescoped.Outcome);
        Assert.Equal("SCOPE_B", env.Exchange.Assignments.Single().CustomResourceScope);

        var remove = await env.Plans.RemoveAssignmentAsync(env.Exchange.Assignments.Single(), CancellationToken.None);
        Assert.Contains("-Confirm:$false", remove.Plan.Operations[0].Command!.ToPreview());
        var removed = await env.Executor.ExecuteAsync(remove.Plan, new ExecutionOptions(false, remove.Plan.Fingerprint, remove.Replan), null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Completed, removed.Outcome);
        Assert.Empty(env.Exchange.Assignments);
    }

    [Fact]
    public async Task Compensation_removes_only_objects_created_by_the_failed_run()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var pointer = env.Exchange.SeedPointer(Fixtures.Polo);
        var request = env.Request(roles: ["Application Mail.Read", "Application Mail.Send"]);
        var (_, plan) = await env.PrepareAsync(request);
        env.Exchange.WriteFailures[ExchangeCommands.NewManagementRoleAssignment] = new FailureInjection(new StudioOperationException(FailureKind.Unknown, "boom"), Occurrence: 2);
        var result = await env.ExecuteAsync(plan, request);
        Assert.Equal(ExecutionOutcome.PartiallyCompleted, result.Outcome);
        env.Exchange.WriteFailures.Clear();

        var compensation = await env.Plans.CompensateAsync(result, CancellationToken.None);
        Assert.DoesNotContain(compensation.Plan.Operations, o => o.Kind == OperationKind.RemoveServicePrincipal);
        Assert.Equal([OperationKind.RemoveRoleAssignment, OperationKind.RemoveManagementScope], compensation.Plan.Operations.Select(o => o.Kind));

        var undone = await env.Executor.ExecuteAsync(compensation.Plan, new ExecutionOptions(false, compensation.Plan.Fingerprint, compensation.Replan), null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Completed, undone.Outcome);
        Assert.Empty(env.Exchange.Assignments);
        Assert.Empty(env.Exchange.Scopes);
        Assert.Contains(pointer, env.Exchange.ServicePrincipals);
    }

    [Fact]
    public async Task Removing_an_application_keeps_shared_scopes()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var polo = env.Exchange.SeedPointer(Fixtures.Polo);
        var other = env.Exchange.SeedPointer(Fixtures.ReportingApp);
        env.Exchange.SeedScope("SHARED", "Guid -eq 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa'");
        env.Exchange.SeedAssignment(polo, "Application Mail.Read", "SHARED", "RA_POLO");
        env.Exchange.SeedAssignment(other, "Application Mail.Read", "SHARED", "RA_OTHER");

        var prepared = await env.Plans.RemoveApplicationAsync(Fixtures.PoloObjectId, removePointer: true, removeUnusedScopes: true, CancellationToken.None);
        Assert.Contains(prepared.Plan.Conflicts, c => c.Code == "SCOPE_IN_USE");
        Assert.DoesNotContain(prepared.Plan.Operations, o => o.Kind == OperationKind.RemoveManagementScope);
    }
}
