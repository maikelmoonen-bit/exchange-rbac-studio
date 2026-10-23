using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Execution;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Planning;

namespace ExchangeRbacStudio.Application.Configuration;

/// <summary>A plan together with the function that rebuilds it from live state immediately before execution.</summary>
public sealed record PreparedPlan(ChangePlan Plan, Func<CancellationToken, Task<ChangePlan>> Replan);

/// <summary>
/// Prepares management plans (removal, rescoping, compensation) from live Exchange state. The returned replan function
/// re-reads state so the executor can abort if anything changed after review.
/// </summary>
public sealed class ManagementPlanService(ConnectionManager connections, IExchangeRbacGateway exchange, TimeProvider time)
{
    public async Task<PreparedPlan> RemoveAssignmentAsync(ManagementRoleAssignment assignment, CancellationToken cancellationToken)
    {
        var session = connections.RequireExchange();
        var tenantId = session.TenantId!.Value;
        var now = time.GetUtcNow();

        async Task<ChangePlan> Build(CancellationToken ct)
        {
            var (current, pointer) = await ReadAssignmentAsync(assignment, ct).ConfigureAwait(false);
            return current is null
                ? Missing(tenantId, session.TenantDisplayName, ChangePlanKind.RemoveRoleAssignment, $"Role assignment '{assignment.Name}' no longer exists.", now)
                : ManagementPlanner.RemoveRoleAssignment(tenantId, session.TenantDisplayName, current, pointer, now);
        }

        return new PreparedPlan(await Build(cancellationToken).ConfigureAwait(false), Build);
    }

    public async Task<PreparedPlan> ChangeAssignmentScopeAsync(ManagementRoleAssignment assignment, string targetScopeName, CancellationToken cancellationToken)
    {
        var session = connections.RequireExchange();
        var tenantId = session.TenantId!.Value;
        var now = time.GetUtcNow();

        async Task<ChangePlan> Build(CancellationToken ct)
        {
            var (current, pointer) = await ReadAssignmentAsync(assignment, ct).ConfigureAwait(false);
            if (current is null)
                return Missing(tenantId, session.TenantDisplayName, ChangePlanKind.ChangeRoleAssignmentScope, $"Role assignment '{assignment.Name}' no longer exists.", now);
            var scope = await exchange.GetManagementScopeAsync(targetScopeName, ct).ConfigureAwait(false);
            if (scope is null)
                return Missing(tenantId, session.TenantDisplayName, ChangePlanKind.ChangeRoleAssignmentScope, $"Management scope '{targetScopeName}' does not exist.", now);
            return ManagementPlanner.ChangeRoleAssignmentScope(tenantId, session.TenantDisplayName, current, pointer, scope, now);
        }

        return new PreparedPlan(await Build(cancellationToken).ConfigureAwait(false), Build);
    }

    public async Task<PreparedPlan> RemoveScopeAsync(string scopeName, CancellationToken cancellationToken)
    {
        var session = connections.RequireExchange();
        var tenantId = session.TenantId!.Value;
        var now = time.GetUtcNow();

        async Task<ChangePlan> Build(CancellationToken ct)
        {
            var scope = await exchange.GetManagementScopeAsync(scopeName, ct).ConfigureAwait(false);
            if (scope is null)
                return Missing(tenantId, session.TenantDisplayName, ChangePlanKind.RemoveManagementScope, $"Management scope '{scopeName}' no longer exists.", now);
            var references = await exchange.GetScopeReferencesAsync(scope.Name, ct).ConfigureAwait(false);
            return ManagementPlanner.RemoveManagementScope(tenantId, session.TenantDisplayName, scope, references, now);
        }

        return new PreparedPlan(await Build(cancellationToken).ConfigureAwait(false), Build);
    }

    public async Task<PreparedPlan> RemoveApplicationAsync(Guid servicePrincipalObjectId, bool removePointer, bool removeUnusedScopes, CancellationToken cancellationToken)
    {
        var session = connections.RequireExchange();
        var tenantId = session.TenantId!.Value;
        var now = time.GetUtcNow();

        async Task<ChangePlan> Build(CancellationToken ct)
        {
            var pointers = await exchange.GetServicePrincipalsAsync(ct).ConfigureAwait(false);
            var pointer = pointers.FirstOrDefault(p => p.ObjectId == servicePrincipalObjectId);
            if (pointer is null)
                return Missing(tenantId, session.TenantDisplayName, ChangePlanKind.RemoveApplicationConfiguration, "The Exchange service principal pointer no longer exists.", now);

            var roles = await exchange.GetApplicationRolesAsync(ct).ConfigureAwait(false);
            var assignments = (await exchange.GetApplicationRoleAssignmentsAsync(roles.Select(r => r.Name).ToList(), ct).ConfigureAwait(false))
                .Where(a => a.IsAssignedTo(pointer)).ToList();

            var scopes = new Dictionary<ManagementScope, IReadOnlyList<ScopeReference>?>();
            if (removeUnusedScopes)
            {
                var allScopes = await exchange.GetManagementScopesAsync(ct).ConfigureAwait(false);
                foreach (var scope in assignments.Select(a => allScopes.FirstOrDefault(s => s.IsReferencedBy(a.ScopeReference))).Where(s => s is not null).Cast<ManagementScope>().DistinctBy(s => s.Name))
                {
                    IReadOnlyList<ScopeReference>? references;
                    try
                    {
                        references = await exchange.GetScopeReferencesAsync(scope.Name, ct).ConfigureAwait(false);
                    }
                    catch (Errors.StudioOperationException ex) when (!ex.IsSessionProblem)
                    {
                        references = null;
                    }

                    scopes[scope] = references;
                }
            }

            return ManagementPlanner.RemoveApplicationConfiguration(tenantId, session.TenantDisplayName, pointer, assignments, removePointer, scopes, now);
        }

        return new PreparedPlan(await Build(cancellationToken).ConfigureAwait(false), Build);
    }

    /// <summary>Plans removal of the objects a partially failed run created (never pre-existing or shared objects).</summary>
    public async Task<PreparedPlan> CompensateAsync(ExecutionResult result, CancellationToken cancellationToken)
    {
        var session = connections.RequireFullSession(result.Plan.TenantId);
        var tenantId = session.TenantId!.Value;
        var now = time.GetUtcNow();
        var created = result.CreatedObjects;

        async Task<ChangePlan> Build(CancellationToken ct)
        {
            var roles = created.Where(o => o.Subject.Role is not null).Select(o => o.Subject.Role!).Distinct().ToList();
            var assignments = roles.Count == 0 ? [] : await exchange.GetApplicationRoleAssignmentsAsync(roles, ct).ConfigureAwait(false);
            var references = new Dictionary<string, IReadOnlyList<ScopeReference>?>(StringComparer.OrdinalIgnoreCase);
            foreach (var scopeName in created.Where(o => o.Kind == OperationKind.CreateManagementScope).Select(o => o.Subject.ScopeName).OfType<string>())
            {
                try
                {
                    references[scopeName] = await exchange.GetScopeReferencesAsync(scopeName, ct).ConfigureAwait(false);
                }
                catch (Errors.StudioOperationException ex) when (!ex.IsSessionProblem)
                {
                    references[scopeName] = null;
                }
            }

            return ManagementPlanner.Compensate(tenantId, session.TenantDisplayName, created, assignments, references, now);
        }

        return new PreparedPlan(await Build(cancellationToken).ConfigureAwait(false), Build);
    }

    private async Task<(ManagementRoleAssignment? Assignment, ExchangeServicePrincipal? Pointer)> ReadAssignmentAsync(ManagementRoleAssignment assignment, CancellationToken ct)
    {
        var assignments = await exchange.GetApplicationRoleAssignmentsAsync([assignment.Role], ct).ConfigureAwait(false);
        var current = assignments.FirstOrDefault(a => a.Name.Equals(assignment.Name, StringComparison.OrdinalIgnoreCase));
        if (current is null) return (null, null);
        var pointers = await exchange.GetServicePrincipalsAsync(ct).ConfigureAwait(false);
        return (current, pointers.FirstOrDefault(current.IsAssignedTo));
    }

    private static ChangePlan Missing(Guid tenantId, string tenantName, ChangePlanKind kind, string message, DateTimeOffset now) =>
        new(Guid.NewGuid(), kind, "Nothing to change", tenantId, tenantName, now, [],
            [new PlanConflict("OBJECT_MISSING", "The object changed", message, "Refresh the page to see the current configuration.")]);
}
