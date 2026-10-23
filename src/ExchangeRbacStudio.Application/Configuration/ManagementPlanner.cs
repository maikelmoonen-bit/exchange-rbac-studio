using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Planning;

namespace ExchangeRbacStudio.Application.Configuration;

/// <summary>Plans for managing existing configuration: removals, rescoping and compensation. Dependencies are always checked.</summary>
public static class ManagementPlanner
{
    public static ChangePlan RemoveRoleAssignment(Guid tenantId, string tenantName, ManagementRoleAssignment assignment, ExchangeServicePrincipal? servicePrincipal, DateTimeOffset now)
    {
        var op = new PlannedOperation(1, OperationKind.RemoveRoleAssignment, OperationDisposition.Remove, assignment.Name,
            $"Remove role assignment '{assignment.Name}' ({assignment.Role} on {assignment.ScopeReference ?? assignment.ScopeKind.ToString()})",
            ExchangeCommands.DeleteRoleAssignment(assignment.Name),
            new OperationSubject(ObjectName: assignment.Name, Role: assignment.Role, ScopeName: assignment.ScopeReference, AppId: servicePrincipal?.AppId, ServicePrincipalObjectId: servicePrincipal?.ObjectId),
            BeforeState: $"{servicePrincipal?.DisplayName ?? assignment.RoleAssigneeName}: {assignment.Role} → {assignment.ScopeReference ?? "organization"}",
            AfterState: "(removed)");

        return new ChangePlan(Guid.NewGuid(), ChangePlanKind.RemoveRoleAssignment, $"Remove role assignment {assignment.Name}", tenantId, tenantName, now, [op], [],
            servicePrincipal is { AppId: { } appId, ObjectId: { } objectId } ? new PlanApplicationContext(servicePrincipal.DisplayName, appId, objectId) : null,
            assignment.ScopeReference, null, [assignment.Role]);
    }

    public static ChangePlan ChangeRoleAssignmentScope(Guid tenantId, string tenantName, ManagementRoleAssignment assignment, ExchangeServicePrincipal? servicePrincipal,
        ManagementScope targetScope, DateTimeOffset now)
    {
        var conflicts = new List<PlanConflict>();
        if (targetScope.IsReferencedBy(assignment.ScopeReference))
            conflicts.Add(new PlanConflict("NO_CHANGE", "The assignment already uses this scope", $"'{assignment.Name}' is already scoped to '{targetScope.Name}'.", "Choose a different scope."));

        var op = new PlannedOperation(1, OperationKind.UpdateRoleAssignmentScope, OperationDisposition.Update, assignment.Name,
            $"Change the scope of '{assignment.Name}' ({assignment.Role}) to '{targetScope.Name}'",
            ExchangeCommands.ChangeRoleAssignmentScope(assignment.Name, targetScope.Name),
            new OperationSubject(ObjectName: assignment.Name, Role: assignment.Role, ScopeName: targetScope.Name, Filter: targetScope.RecipientFilter,
                AppId: servicePrincipal?.AppId, ServicePrincipalObjectId: servicePrincipal?.ObjectId),
            BeforeState: assignment.ScopeReference ?? "organization-wide",
            AfterState: $"{targetScope.Name} ({targetScope.RecipientFilter})");

        return new ChangePlan(Guid.NewGuid(), ChangePlanKind.ChangeRoleAssignmentScope, $"Rescope role assignment {assignment.Name}", tenantId, tenantName, now, [op], conflicts,
            servicePrincipal is { AppId: { } appId, ObjectId: { } objectId } ? new PlanApplicationContext(servicePrincipal.DisplayName, appId, objectId) : null,
            targetScope.Name, targetScope.RecipientFilter, [assignment.Role]);
    }

    /// <summary>Plans removal of a scope. Blocked while any role assignment (application or administrator) references it.</summary>
    public static ChangePlan RemoveManagementScope(Guid tenantId, string tenantName, ManagementScope scope, IReadOnlyList<ScopeReference>? references, DateTimeOffset now)
    {
        var conflicts = new List<PlanConflict>();
        if (references is null)
        {
            conflicts.Add(new PlanConflict("REFERENCES_UNKNOWN", "Dependencies could not be determined",
                "The role assignments that reference this scope could not be read.", "Retry once the dependency check succeeds."));
        }
        else if (references.Count > 0)
        {
            conflicts.Add(new PlanConflict("SCOPE_IN_USE", $"'{scope.Name}' is still referenced by {references.Count} role assignment(s)",
                string.Join(Environment.NewLine, references.Select(r => $"{r.AssignmentName}: {r.Role} ({r.RoleAssigneeType} {r.RoleAssigneeName}) via {r.ReferencingProperty}")),
                "Remove or rescope these assignments first. Removing a scope that is in use would break them."));
        }

        var op = new PlannedOperation(1, OperationKind.RemoveManagementScope, OperationDisposition.Remove, scope.Name,
            $"Remove management scope '{scope.Name}'", ExchangeCommands.DeleteManagementScope(scope.Name),
            new OperationSubject(ObjectName: scope.Name, ScopeName: scope.Name, Filter: scope.RecipientFilter),
            BeforeState: scope.RecipientFilter, AfterState: "(removed)");

        return new ChangePlan(Guid.NewGuid(), ChangePlanKind.RemoveManagementScope, $"Remove management scope {scope.Name}", tenantId, tenantName, now, [op], conflicts,
            ScopeName: scope.Name, ScopeFilter: scope.RecipientFilter);
    }

    /// <summary>
    /// Plans removal of an application's Exchange RBAC configuration: all its role assignments and optionally the pointer.
    /// Scopes are only removed when explicitly requested and when no other assignment references them.
    /// </summary>
    public static ChangePlan RemoveApplicationConfiguration(Guid tenantId, string tenantName, ExchangeServicePrincipal servicePrincipal,
        IReadOnlyList<ManagementRoleAssignment> assignments, bool removePointer, IReadOnlyDictionary<ManagementScope, IReadOnlyList<ScopeReference>?> scopesToRemove,
        DateTimeOffset now)
    {
        var operations = new List<PlannedOperation>();
        var conflicts = new List<PlanConflict>();
        var sequence = 0;
        var removedNames = assignments.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var assignment in assignments.OrderBy(a => a.Role, StringComparer.OrdinalIgnoreCase))
        {
            operations.Add(new PlannedOperation(++sequence, OperationKind.RemoveRoleAssignment, OperationDisposition.Remove, assignment.Name,
                $"Remove role assignment '{assignment.Name}' ({assignment.Role} on {assignment.ScopeReference ?? "organization"})",
                ExchangeCommands.DeleteRoleAssignment(assignment.Name),
                new OperationSubject(ObjectName: assignment.Name, Role: assignment.Role, ScopeName: assignment.ScopeReference, AppId: servicePrincipal.AppId, ServicePrincipalObjectId: servicePrincipal.ObjectId),
                BeforeState: $"{assignment.Role} → {assignment.ScopeReference ?? "organization"}", AfterState: "(removed)"));
        }

        foreach (var (scope, references) in scopesToRemove)
        {
            var remaining = references?.Where(r => !removedNames.Contains(r.AssignmentName)).ToList();
            if (remaining is null)
            {
                conflicts.Add(new PlanConflict("REFERENCES_UNKNOWN", $"Dependencies of '{scope.Name}' are unknown", "The role assignments that reference this scope could not be read.", "Keep the scope or retry."));
                continue;
            }

            if (remaining.Count > 0)
            {
                conflicts.Add(new PlanConflict("SCOPE_IN_USE", $"'{scope.Name}' is used by other assignments",
                    string.Join(Environment.NewLine, remaining.Select(r => $"{r.AssignmentName}: {r.Role} ({r.RoleAssigneeName})")),
                    "Keep the scope; it is shared with other configuration."));
                continue;
            }

            operations.Add(new PlannedOperation(++sequence, OperationKind.RemoveManagementScope, OperationDisposition.Remove, scope.Name,
                $"Remove management scope '{scope.Name}' (no longer referenced)", ExchangeCommands.DeleteManagementScope(scope.Name),
                new OperationSubject(ObjectName: scope.Name, ScopeName: scope.Name, Filter: scope.RecipientFilter),
                BeforeState: scope.RecipientFilter, AfterState: "(removed)"));
        }

        if (removePointer)
        {
            var identity = servicePrincipal.ObjectId?.ToString("D") ?? servicePrincipal.Identity;
            operations.Add(new PlannedOperation(++sequence, OperationKind.RemoveServicePrincipal, OperationDisposition.Remove, servicePrincipal.DisplayName,
                $"Remove Exchange service principal pointer '{servicePrincipal.DisplayName}' (the Entra application itself is not affected)",
                ExchangeCommands.DeleteServicePrincipal(identity),
                new OperationSubject(ObjectName: servicePrincipal.DisplayName, AppId: servicePrincipal.AppId, ServicePrincipalObjectId: servicePrincipal.ObjectId),
                BeforeState: $"AppId {servicePrincipal.AppId}, ObjectId {servicePrincipal.ObjectId}", AfterState: "(removed)"));
        }

        return new ChangePlan(Guid.NewGuid(), ChangePlanKind.RemoveApplicationConfiguration, $"Remove Exchange RBAC configuration of {servicePrincipal.DisplayName}",
            tenantId, tenantName, now, operations, conflicts,
            servicePrincipal is { AppId: { } appId, ObjectId: { } objectId } ? new PlanApplicationContext(servicePrincipal.DisplayName, appId, objectId) : null,
            Roles: assignments.Select(a => a.Role).ToList());
    }

    /// <summary>
    /// Plans compensating removal of objects created by a (partially) failed run. Only objects created by that run are
    /// considered, in reverse order, and only when nothing else depends on them. Pre-existing objects are never touched.
    /// </summary>
    public static ChangePlan Compensate(Guid tenantId, string tenantName, IReadOnlyList<PlannedOperation> createdByRun,
        IReadOnlyList<ManagementRoleAssignment> currentApplicationAssignments, IReadOnlyDictionary<string, IReadOnlyList<ScopeReference>?> scopeReferences, DateTimeOffset now)
    {
        var operations = new List<PlannedOperation>();
        var conflicts = new List<PlanConflict>();
        var sequence = 0;
        var removedAssignments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var created in createdByRun.Where(o => o.Disposition == OperationDisposition.Create).OrderByDescending(o => o.Sequence))
        {
            switch (created.Kind)
            {
                case OperationKind.CreateRoleAssignment when created.Subject.ObjectName is { } name:
                    removedAssignments.Add(name);
                    operations.Add(new PlannedOperation(++sequence, OperationKind.RemoveRoleAssignment, OperationDisposition.Remove, name,
                        $"Remove role assignment '{name}' created by the failed run", ExchangeCommands.DeleteRoleAssignment(name), created.Subject,
                        BeforeState: created.AfterState, AfterState: "(removed)"));
                    break;

                case OperationKind.CreateManagementScope when created.Subject.ScopeName is { } scopeName:
                    scopeReferences.TryGetValue(scopeName, out var references);
                    var remaining = references?.Where(r => !removedAssignments.Contains(r.AssignmentName)).ToList();
                    if (remaining is null || remaining.Count > 0)
                    {
                        conflicts.Add(new PlanConflict("SCOPE_IN_USE", $"Scope '{scopeName}' is kept",
                            remaining is null ? "Its dependencies could not be read." : $"It is referenced by {remaining.Count} other assignment(s).",
                            "The scope is left in place."));
                        continue;
                    }

                    operations.Add(new PlannedOperation(++sequence, OperationKind.RemoveManagementScope, OperationDisposition.Remove, scopeName,
                        $"Remove management scope '{scopeName}' created by the failed run", ExchangeCommands.DeleteManagementScope(scopeName), created.Subject,
                        BeforeState: created.Subject.Filter, AfterState: "(removed)"));
                    break;

                case OperationKind.CreateServicePrincipal when created.Subject.ServicePrincipalObjectId is { } objectId:
                    var stillAssigned = currentApplicationAssignments.Any(a => !removedAssignments.Contains(a.Name)
                        && (string.Equals(a.RoleAssignee, objectId.ToString(), StringComparison.OrdinalIgnoreCase)
                            || string.Equals(a.RoleAssigneeName, objectId.ToString(), StringComparison.OrdinalIgnoreCase)));
                    if (stillAssigned)
                    {
                        conflicts.Add(new PlanConflict("POINTER_IN_USE", "Service principal pointer is kept", "Other role assignments still reference it.", "The pointer is left in place."));
                        continue;
                    }

                    operations.Add(new PlannedOperation(++sequence, OperationKind.RemoveServicePrincipal, OperationDisposition.Remove, created.Target,
                        $"Remove Exchange service principal pointer '{created.Target}' created by the failed run", ExchangeCommands.DeleteServicePrincipal(objectId.ToString("D")), created.Subject,
                        AfterState: "(removed)"));
                    break;
            }
        }

        // Objects that are kept are documented as notes, not conflicts: they must not block the removals that are safe.
        return new ChangePlan(Guid.NewGuid(), ChangePlanKind.Compensation, "Remove objects created by the failed run", tenantId, tenantName, now, operations, [],
            Notes: conflicts.Select(c => $"{c.Title}: {c.Detail}").ToList());
    }
}
