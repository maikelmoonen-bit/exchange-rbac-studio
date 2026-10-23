using System.Security.Cryptography;
using System.Text;

namespace ExchangeRbacStudio.Domain.Planning;

public enum OperationKind
{
    CreateServicePrincipal,
    RemoveServicePrincipal,
    CreateManagementScope,
    UpdateManagementScope,
    RemoveManagementScope,
    CreateRoleAssignment,
    UpdateRoleAssignmentScope,
    RemoveRoleAssignment,
}

public enum OperationDisposition
{
    Create,
    Update,
    Remove,

    /// <summary>The desired object already exists with the desired configuration and is reused unchanged.</summary>
    Reuse,
}

public enum ChangePlanKind
{
    ConfigureAccess,
    ModifyAccess,
    RemoveRoleAssignment,
    ChangeRoleAssignmentScope,
    RemoveManagementScope,
    RemoveApplicationConfiguration,
    Compensation,
}

/// <summary>The Exchange object an operation acts on; used to verify the resulting state after execution.</summary>
public sealed record OperationSubject(
    string? ObjectName = null,
    string? Role = null,
    string? ScopeName = null,
    string? Filter = null,
    Guid? AppId = null,
    Guid? ServicePrincipalObjectId = null);

/// <summary>One step in a change plan. <see cref="Command"/> is null for reused objects.</summary>
public sealed record PlannedOperation(
    int Sequence,
    OperationKind Kind,
    OperationDisposition Disposition,
    string Target,
    string Summary,
    ExchangeCommand? Command,
    OperationSubject Subject,
    string? BeforeState = null,
    string? AfterState = null)
{
    public bool RequiresExecution => Disposition != OperationDisposition.Reuse && Command is not null;

    public bool IsDestructive => Disposition == OperationDisposition.Remove || Disposition == OperationDisposition.Update;
}

/// <summary>A difference between existing and desired configuration that requires a deliberate decision.</summary>
public sealed record PlanConflict(string Code, string Title, string Detail, string Resolution);

public sealed record PlanApplicationContext(string DisplayName, Guid AppId, Guid ServicePrincipalObjectId);

/// <summary>
/// An ordered, tenant-bound set of Exchange operations. Plans are immutable; their <see cref="Fingerprint"/> is used to
/// prove that what was validated is exactly what is executed.
/// </summary>
public sealed record ChangePlan(
    Guid PlanId,
    ChangePlanKind Kind,
    string Title,
    Guid TenantId,
    string TenantDisplayName,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<PlannedOperation> Operations,
    IReadOnlyList<PlanConflict> Conflicts,
    PlanApplicationContext? Application = null,
    string? ScopeName = null,
    string? ScopeFilter = null,
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<string>? Notes = null)
{
    public IReadOnlyList<PlannedOperation> WriteOperations => Operations.Where(o => o.RequiresExecution).ToList();

    public bool HasConflicts => Conflicts.Count > 0;

    public bool HasChanges => WriteOperations.Count > 0;

    public bool IsDestructive => Operations.Any(o => o.RequiresExecution && o.IsDestructive);

    /// <summary>SHA-256 over the tenant and the exact command sequence.</summary>
    public string Fingerprint
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(TenantId.ToString("D")).Append('\n').Append(Kind).Append('\n');
            foreach (var op in Operations)
                sb.Append(op.Kind).Append('|').Append(op.Disposition).Append('|').Append(op.Target).Append('|').Append(op.Command?.ToPreview()).Append('\n');
            foreach (var conflict in Conflicts)
                sb.Append("conflict|").Append(conflict.Code).Append('|').Append(conflict.Detail).Append('\n');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        }
    }

    /// <summary>
    /// An equivalent PowerShell script for transparency and audit. The application executes the same commands through
    /// typed parameter binding in an isolated host, not by running this text.
    /// </summary>
    public string ToPowerShellScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Exchange RBAC Studio - planned changes");
        sb.AppendLine($"# Tenant: {TenantDisplayName} ({TenantId:D})");
        sb.AppendLine($"# Plan:   {Title}  [{PlanId:D}]");
        sb.AppendLine($"# Fingerprint: {Fingerprint}");
        sb.AppendLine("# Prerequisite: Connect-ExchangeOnline as an administrator of the tenant above.");
        sb.AppendLine();
        foreach (var op in Operations)
        {
            sb.AppendLine($"# {op.Sequence}. {op.Summary}");
            sb.AppendLine(op.Command is null ? "#    (no change - existing object is reused)" : op.Command.ToPreview());
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }
}
