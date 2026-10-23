using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Planning;
using ExchangeRbacStudio.Domain.Scoping;

namespace ExchangeRbacStudio.Application.Configuration;

/// <summary>
/// Computes the idempotent set of Exchange operations that converges current state to the desired configuration.
/// Objects that already exist with the desired configuration are reused; conflicting objects are never overwritten
/// implicitly but reported as conflicts that require a deliberate decision.
/// </summary>
public static class ConfigurationPlanner
{
    public static ChangePlan Plan(
        ConfigurationRequest request,
        CurrentConfiguration state,
        Guid tenantId,
        string tenantName,
        string assignmentNameTemplate,
        DateTimeOffset now)
    {
        var sp = request.ServicePrincipal;
        var operations = new List<PlannedOperation>();
        var conflicts = new List<PlanConflict>();
        var sequence = 0;
        var spSubject = new OperationSubject(ObjectName: sp.DisplayName, AppId: sp.AppId, ServicePrincipalObjectId: sp.ObjectId);

        // 1. Exchange service principal pointer.
        if (state.PointerMismatch)
        {
            conflicts.Add(new PlanConflict("POINTER_MISMATCH", "Exchange service principal pointer does not match Entra ID",
                $"Pointer '{state.Pointer!.DisplayName}' has AppId {state.Pointer.AppId} and ObjectId {state.Pointer.ObjectId}; the Enterprise Application has AppId {sp.AppId} and ObjectId {sp.ObjectId}.",
                "Review the pointer's role assignments and remove the stale pointer before configuring this application."));
        }
        else if (state.Pointer is not null)
        {
            operations.Add(new PlannedOperation(++sequence, OperationKind.CreateServicePrincipal, OperationDisposition.Reuse, sp.DisplayName,
                $"Reuse existing Exchange service principal '{state.Pointer.DisplayName}'", null, spSubject));
        }
        else
        {
            operations.Add(new PlannedOperation(++sequence, OperationKind.CreateServicePrincipal, OperationDisposition.Create, sp.DisplayName,
                $"Create Exchange service principal pointer for '{sp.DisplayName}'",
                ExchangeCommands.CreateServicePrincipal(sp.AppId, sp.ObjectId, sp.DisplayName), spSubject,
                AfterState: $"AppId {sp.AppId}, ObjectId {sp.ObjectId}"));
        }

        // 2. Management scope.
        var target = state.FindScope(request.ScopeName);
        var scopeSubject = new OperationSubject(ObjectName: request.ScopeName, ScopeName: request.ScopeName, Filter: request.ScopeFilter);
        if (target is null)
        {
            operations.Add(new PlannedOperation(++sequence, OperationKind.CreateManagementScope, OperationDisposition.Create, request.ScopeName,
                $"Create management scope '{request.ScopeName}' for {request.Mailboxes.Count} mailbox(es)",
                ExchangeCommands.CreateManagementScope(request.ScopeName, request.ScopeFilter), scopeSubject,
                AfterState: request.ScopeFilter));
        }
        else if (RecipientFilterAnalyzer.AreEquivalentExactFilters(target.RecipientFilter, request.ScopeFilter))
        {
            operations.Add(new PlannedOperation(++sequence, OperationKind.CreateManagementScope, OperationDisposition.Reuse, target.Name,
                $"Reuse existing management scope '{target.Name}' (identical membership filter)", null, scopeSubject,
                BeforeState: target.RecipientFilter, AfterState: target.RecipientFilter));
        }
        else if (request.Mode == ConfigurationMode.Modify
                 && target.Name.Equals(request.ExistingScopeName ?? request.ScopeName, StringComparison.OrdinalIgnoreCase)
                 && state.Pointer is not null
                 && IsReferencedOnlyBy(state.TargetScopeReferences, state.Pointer))
        {
            operations.Add(new PlannedOperation(++sequence, OperationKind.UpdateManagementScope, OperationDisposition.Update, target.Name,
                $"Update the membership filter of '{target.Name}' ({request.Mailboxes.Count} mailbox(es))",
                ExchangeCommands.UpdateManagementScopeFilter(target.Name, request.ScopeFilter), scopeSubject,
                BeforeState: target.RecipientFilter, AfterState: request.ScopeFilter));
        }
        else if (request.Mode == ConfigurationMode.Modify && state.TargetScopeReferences is null)
        {
            conflicts.Add(new PlanConflict("SCOPE_REFERENCES_UNKNOWN", $"Dependencies of '{target.Name}' are unknown",
                "The role assignments that use this scope could not be determined, so its filter cannot be changed safely.",
                "Retry validation, or create a new scope under a different name."));
        }
        else
        {
            var others = state.TargetScopeReferences?.Where(r => state.Pointer is null || !ReferencesPointer(r, state.Pointer)).ToList() ?? [];
            conflicts.Add(new PlanConflict(
                others.Count > 0 ? "SCOPE_SHARED" : "SCOPE_NAME_IN_USE",
                $"Management scope '{target.Name}' already exists with a different filter",
                others.Count > 0
                    ? $"The scope is used by {others.Count} other role assignment(s) ({string.Join(", ", others.Take(5).Select(o => o.AssignmentName))}). Changing its filter would change their access too."
                    : $"Existing filter: {target.RecipientFilter ?? "(none)"}. Desired filter: {request.ScopeFilter}.",
                "Choose a different scope name. Existing scopes are never overwritten implicitly."));
        }

        // 3. Role assignments.
        var assignmentNames = state.AllApplicationAssignments.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var onTarget = target is null ? [] : state.ServicePrincipalAssignments.Where(a => a.ScopeKind == AssignmentScopeKind.ManagementScope && target.IsReferencedBy(a.ScopeReference)).ToList();
        var oldScope = request.Mode == ConfigurationMode.Modify && request.ExistingScopeName is { } existingName
            && !existingName.Equals(request.ScopeName, StringComparison.OrdinalIgnoreCase)
                ? state.FindScope(existingName)
                : null;
        var onOld = oldScope is null ? [] : state.ServicePrincipalAssignments.Where(a => a.ScopeKind == AssignmentScopeKind.ManagementScope && oldScope.IsReferencedBy(a.ScopeReference)).ToList();
        var naming = new NamingContext(sp.DisplayName, sp.AppId, request.Roles, request.Mailboxes.Count, now);

        foreach (var role in request.Roles.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
        {
            var roleSubject = new OperationSubject(Role: role, ScopeName: request.ScopeName, AppId: sp.AppId, ServicePrincipalObjectId: sp.ObjectId);
            var existing = onTarget.FirstOrDefault(a => a.Role.Equals(role, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                operations.Add(new PlannedOperation(++sequence, OperationKind.CreateRoleAssignment, OperationDisposition.Reuse, existing.Name,
                    $"Reuse existing assignment '{existing.Name}' ({role} on {request.ScopeName})", null, roleSubject with { ObjectName = existing.Name }));
                continue;
            }

            var movable = onOld.FirstOrDefault(a => a.Role.Equals(role, StringComparison.OrdinalIgnoreCase));
            if (movable is not null)
            {
                operations.Add(new PlannedOperation(++sequence, OperationKind.UpdateRoleAssignmentScope, OperationDisposition.Update, movable.Name,
                    $"Move assignment '{movable.Name}' ({role}) from '{oldScope!.Name}' to '{request.ScopeName}'",
                    ExchangeCommands.ChangeRoleAssignmentScope(movable.Name, request.ScopeName), roleSubject with { ObjectName = movable.Name },
                    BeforeState: oldScope.Name, AfterState: request.ScopeName));
                continue;
            }

            var name = ScopeNamingPolicy.EnsureUnique(ScopeNamingPolicy.RenderAssignmentName(assignmentNameTemplate, naming, role), assignmentNames);
            assignmentNames.Add(name);
            operations.Add(new PlannedOperation(++sequence, OperationKind.CreateRoleAssignment, OperationDisposition.Create, name,
                $"Assign '{role}' to '{sp.DisplayName}' on scope '{request.ScopeName}'",
                ExchangeCommands.CreateRoleAssignment(name, sp.ObjectId, role, request.ScopeName), roleSubject with { ObjectName = name },
                AfterState: $"{role} → {request.ScopeName}"));
        }

        // 4. In modify mode, remove roles that are no longer desired from the managed scope(s). Removals come last.
        if (request.Mode == ConfigurationMode.Modify)
        {
            var desired = request.Roles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var obsolete in onTarget.Concat(onOld).Where(a => !desired.Contains(a.Role)).DistinctBy(a => a.Name))
            {
                operations.Add(new PlannedOperation(++sequence, OperationKind.RemoveRoleAssignment, OperationDisposition.Remove, obsolete.Name,
                    $"Remove assignment '{obsolete.Name}' ({obsolete.Role} on {obsolete.ScopeReference})",
                    ExchangeCommands.DeleteRoleAssignment(obsolete.Name),
                    new OperationSubject(ObjectName: obsolete.Name, Role: obsolete.Role, ScopeName: obsolete.ScopeReference, AppId: sp.AppId, ServicePrincipalObjectId: sp.ObjectId),
                    BeforeState: $"{obsolete.Role} → {obsolete.ScopeReference}", AfterState: "(removed)"));
            }
        }

        var title = request.Mode == ConfigurationMode.Modify
            ? $"Modify scoped access for {sp.DisplayName}"
            : $"Configure scoped access for {sp.DisplayName}";

        return new ChangePlan(Guid.NewGuid(), request.Mode == ConfigurationMode.Modify ? ChangePlanKind.ModifyAccess : ChangePlanKind.ConfigureAccess,
            title, tenantId, tenantName, now, operations, conflicts,
            new PlanApplicationContext(sp.DisplayName, sp.AppId, sp.ObjectId), request.ScopeName, request.ScopeFilter, request.Roles);
    }

    public static bool IsReferencedOnlyBy(IReadOnlyList<ScopeReference>? references, ExchangeServicePrincipal pointer) =>
        references is not null && references.All(r => ReferencesPointer(r, pointer));

    public static bool ReferencesPointer(ScopeReference reference, ExchangeServicePrincipal pointer) =>
        string.Equals(reference.RoleAssigneeType, "ServicePrincipal", StringComparison.OrdinalIgnoreCase)
        && reference.RoleAssigneeName is not null
        && pointer.ReferenceValues().Contains(reference.RoleAssigneeName.Trim(), StringComparer.OrdinalIgnoreCase);
}
