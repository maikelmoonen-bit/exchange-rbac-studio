using ExchangeRbacStudio.Domain.Planning;

namespace ExchangeRbacStudio.Application.Sessions;

/// <summary>Exchange Online cmdlets this tool needs. Only these are loaded into the session (Connect-ExchangeOnline -CommandName).</summary>
public static class ExchangeCommandSets
{
    public static IReadOnlyList<string> ReadCommands { get; } =
    [
        "Get-ServicePrincipal",
        "Get-ManagementScope",
        "Get-ManagementRole",
        "Get-ManagementRoleAssignment",
        "Get-Recipient",
        "Test-ServicePrincipalAuthorization",
        "Get-ApplicationAccessPolicy",
        "Get-OrganizationConfig",
    ];

    public static IReadOnlyList<string> WriteCommands { get; } = ExchangeCommands.WriteCommandNames.OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>Commands required to configure scoped application access.</summary>
    public static IReadOnlyList<string> ConfigurationCommands { get; } =
    [
        ExchangeCommands.NewServicePrincipal,
        ExchangeCommands.NewManagementScope,
        ExchangeCommands.NewManagementRoleAssignment,
        "Get-ManagementRoleAssignment",
        "Test-ServicePrincipalAuthorization",
    ];

    public static IReadOnlyList<string> All { get; } = ReadCommands.Concat(WriteCommands).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
