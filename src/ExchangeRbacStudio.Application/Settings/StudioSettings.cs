using ExchangeRbacStudio.Domain.Clouds;
using ExchangeRbacStudio.Domain.Scoping;

namespace ExchangeRbacStudio.Application.Settings;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>Which public client application is used for the delegated Microsoft Graph sign-in.</summary>
public enum GraphClientMode
{
    /// <summary>The administrator has not chosen a sign-in client yet.</summary>
    NotConfigured,

    /// <summary>The Microsoft-published "Microsoft Graph Command Line Tools" public client.</summary>
    MicrosoftGraphCommandLineTools,

    /// <summary>An app registration owned by the organization.</summary>
    Custom,
}

public static class KnownPublicClients
{
    /// <summary>Microsoft Graph Command Line Tools: Microsoft first-party public client used by Connect-MgGraph.</summary>
    public const string MicrosoftGraphCommandLineTools = "14d82eec-204b-4c2f-b7e8-296a70dab67e";
}

/// <summary>Local, non-secret user preferences. No credentials or tokens are ever stored here.</summary>
public sealed record StudioSettings
{
    public ThemePreference Theme { get; init; } = ThemePreference.System;

    public CloudEnvironment Environment { get; init; } = CloudEnvironment.Global;

    public GraphClientMode GraphClient { get; init; } = GraphClientMode.NotConfigured;

    public string? CustomGraphClientId { get; init; }

    /// <summary>Optional tenant ID or verified domain used as the sign-in authority (default: organizations).</summary>
    public string? TenantHint { get; init; }

    public string ScopeNameTemplate { get; init; } = ScopeNamingPolicy.DefaultScopeTemplate;

    public string AssignmentNameTemplate { get; init; } = ScopeNamingPolicy.DefaultAssignmentTemplate;

    public RecipientFilterStrategy FilterStrategy { get; init; } = RecipientFilterStrategy.ExternalDirectoryObjectId;

    public int MaxMailboxesPerScope { get; init; } = 50;

    /// <summary>Use Web Account Manager for Exchange Online sign-in. Off by default because WAM requires a parent window handle.</summary>
    public bool UseWebAccountManagerForExchange { get; init; }

    /// <summary>Explicit path to pwsh.exe; null to auto-detect PowerShell 7.</summary>
    public string? PowerShellPath { get; init; }

    public string MinimumExchangeModuleVersion { get; init; } = "3.7.0";

    public bool RequireTypedConfirmationForRemovals { get; init; } = true;

    public string? EffectiveGraphClientId => GraphClient switch
    {
        GraphClientMode.MicrosoftGraphCommandLineTools => KnownPublicClients.MicrosoftGraphCommandLineTools,
        GraphClientMode.Custom => CustomGraphClientId?.Trim(),
        _ => null,
    };

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (GraphClient == GraphClientMode.Custom && !Guid.TryParse(CustomGraphClientId, out _))
            errors.Add("The custom sign-in client ID must be a GUID (the Application (client) ID of your app registration).");

        if (!string.IsNullOrWhiteSpace(TenantHint) && !Guid.TryParse(TenantHint, out _)
            && !System.Text.RegularExpressions.Regex.IsMatch(TenantHint, @"^[A-Za-z0-9][A-Za-z0-9.-]{0,252}\.[A-Za-z]{2,}$"))
            errors.Add("The tenant must be a tenant ID (GUID) or a verified domain such as contoso.onmicrosoft.com.");

        if (MaxMailboxesPerScope is < 1 or > 500)
            errors.Add("The maximum number of mailboxes per scope must be between 1 and 500.");

        if (string.IsNullOrWhiteSpace(ScopeNameTemplate) || !ScopeNameTemplate.Contains("{APP", StringComparison.OrdinalIgnoreCase))
            errors.Add("The scope naming template must contain {APP}, {APPID} or {APPID8} so names identify the application.");

        if (string.IsNullOrWhiteSpace(AssignmentNameTemplate) || !AssignmentNameTemplate.Contains("{ROLE}", StringComparison.OrdinalIgnoreCase))
            errors.Add("The role assignment naming template must contain {ROLE} so each assignment name is unique.");

        if (!Version.TryParse(MinimumExchangeModuleVersion, out _))
            errors.Add("The minimum ExchangeOnlineManagement version must be a version number such as 3.7.0.");

        if (!string.IsNullOrWhiteSpace(PowerShellPath) && !PowerShellPath.EndsWith("pwsh.exe", StringComparison.OrdinalIgnoreCase))
            errors.Add("The PowerShell path must point to pwsh.exe (PowerShell 7).");

        return errors;
    }
}
