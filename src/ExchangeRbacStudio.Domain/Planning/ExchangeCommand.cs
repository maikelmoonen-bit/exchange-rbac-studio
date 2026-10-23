using System.Globalization;
using System.Text;

namespace ExchangeRbacStudio.Domain.Planning;

public enum ExchangeParameterKind
{
    String,
    Guid,
    Switch,
}

/// <summary>A typed, named cmdlet parameter. Values are bound by the PowerShell host, never concatenated into script text.</summary>
public sealed record ExchangeParameter(string Name, ExchangeParameterKind Kind, string? StringValue = null, Guid? GuidValue = null, bool? SwitchValue = null)
{
    public static ExchangeParameter String(string name, string value) => new(name, ExchangeParameterKind.String, StringValue: value);

    public static ExchangeParameter Guid(string name, System.Guid value) => new(name, ExchangeParameterKind.Guid, GuidValue: value);

    public static ExchangeParameter Switch(string name, bool value) => new(name, ExchangeParameterKind.Switch, SwitchValue: value);

    /// <summary>The value as it is sent to the host (GUIDs in canonical "D" format).</summary>
    public object WireValue => Kind switch
    {
        ExchangeParameterKind.String => StringValue ?? string.Empty,
        ExchangeParameterKind.Guid => GuidValue!.Value.ToString("D", CultureInfo.InvariantCulture),
        ExchangeParameterKind.Switch => SwitchValue ?? false,
        _ => throw new InvalidOperationException(),
    };

    public string ToPreview() => Kind switch
    {
        ExchangeParameterKind.Switch => $"-{Name}:${(SwitchValue == true ? "true" : "false")}",
        ExchangeParameterKind.Guid => $"-{Name} '{GuidValue!.Value:D}'",
        _ => $"-{Name} {PowerShellQuoting.Quote(StringValue ?? string.Empty)}",
    };
}

/// <summary>
/// A single Exchange Online cmdlet invocation in a change plan. The same object is used to render the preview shown
/// to the administrator and to execute the change, so the preview always matches what is executed.
/// </summary>
public sealed record ExchangeCommand
{
    public ExchangeCommand(string name, IReadOnlyList<ExchangeParameter> parameters)
    {
        if (!ExchangeCommands.WriteCommandNames.Contains(name))
            throw new ArgumentException($"'{name}' is not an allowed Exchange write command.", nameof(name));
        Name = name;
        Parameters = parameters;
    }

    public string Name { get; }

    public IReadOnlyList<ExchangeParameter> Parameters { get; }

    public string ToPreview()
    {
        var sb = new StringBuilder(Name);
        foreach (var parameter in Parameters)
            sb.Append(' ').Append(parameter.ToPreview());
        return sb.ToString();
    }

    public string? GetString(string parameterName) =>
        Parameters.FirstOrDefault(p => p.Name.Equals(parameterName, StringComparison.OrdinalIgnoreCase))?.WireValue?.ToString();

    public bool Equals(ExchangeCommand? other) => other is not null && ToPreview() == other.ToPreview();

    public override int GetHashCode() => ToPreview().GetHashCode(StringComparison.Ordinal);
}

/// <summary>Factory for the write cmdlets this tool is allowed to execute (mirrors the host allowlist).</summary>
public static class ExchangeCommands
{
    public const string NewServicePrincipal = "New-ServicePrincipal";
    public const string RemoveServicePrincipal = "Remove-ServicePrincipal";
    public const string NewManagementScope = "New-ManagementScope";
    public const string SetManagementScope = "Set-ManagementScope";
    public const string RemoveManagementScope = "Remove-ManagementScope";
    public const string NewManagementRoleAssignment = "New-ManagementRoleAssignment";
    public const string SetManagementRoleAssignment = "Set-ManagementRoleAssignment";
    public const string RemoveManagementRoleAssignment = "Remove-ManagementRoleAssignment";

    public static IReadOnlySet<string> WriteCommandNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        NewServicePrincipal, RemoveServicePrincipal, NewManagementScope, SetManagementScope, RemoveManagementScope,
        NewManagementRoleAssignment, SetManagementRoleAssignment, RemoveManagementRoleAssignment,
    };

    public static ExchangeCommand CreateServicePrincipal(Guid appId, Guid servicePrincipalObjectId, string displayName) =>
        new(NewServicePrincipal,
        [
            ExchangeParameter.Guid("AppId", appId),
            ExchangeParameter.Guid("ObjectId", servicePrincipalObjectId),
            ExchangeParameter.String("DisplayName", displayName),
        ]);

    public static ExchangeCommand DeleteServicePrincipal(string identity) =>
        new(RemoveServicePrincipal, [ExchangeParameter.String("Identity", identity), ExchangeParameter.Switch("Confirm", false)]);

    public static ExchangeCommand CreateManagementScope(string name, string recipientRestrictionFilter) =>
        new(NewManagementScope,
        [
            ExchangeParameter.String("Name", name),
            ExchangeParameter.String("RecipientRestrictionFilter", recipientRestrictionFilter),
        ]);

    public static ExchangeCommand UpdateManagementScopeFilter(string identity, string recipientRestrictionFilter) =>
        new(SetManagementScope,
        [
            ExchangeParameter.String("Identity", identity),
            ExchangeParameter.String("RecipientRestrictionFilter", recipientRestrictionFilter),
        ]);

    public static ExchangeCommand DeleteManagementScope(string identity) =>
        new(RemoveManagementScope, [ExchangeParameter.String("Identity", identity), ExchangeParameter.Switch("Confirm", false)]);

    public static ExchangeCommand CreateRoleAssignment(string name, Guid servicePrincipalObjectId, string role, string customResourceScope) =>
        new(NewManagementRoleAssignment,
        [
            ExchangeParameter.String("Name", name),
            ExchangeParameter.Guid("App", servicePrincipalObjectId),
            ExchangeParameter.String("Role", role),
            ExchangeParameter.String("CustomResourceScope", customResourceScope),
        ]);

    public static ExchangeCommand ChangeRoleAssignmentScope(string identity, string customResourceScope) =>
        new(SetManagementRoleAssignment,
        [
            ExchangeParameter.String("Identity", identity),
            ExchangeParameter.String("CustomResourceScope", customResourceScope),
        ]);

    public static ExchangeCommand DeleteRoleAssignment(string identity) =>
        new(RemoveManagementRoleAssignment, [ExchangeParameter.String("Identity", identity), ExchangeParameter.Switch("Confirm", false)]);
}

public static class PowerShellQuoting
{
    /// <summary>
    /// Quotes a value as a PowerShell string literal for display. Uses double quotes when that is unambiguous and
    /// more readable (for OPATH filters containing single quotes), otherwise a single-quoted verbatim literal.
    /// </summary>
    public static string Quote(string value)
    {
        if (value.Contains('\'') && value.IndexOfAny(['"', '$', '`', '“', '”']) < 0)
            return "\"" + value + "\"";
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
