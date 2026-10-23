using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Scoping;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Identity;
using ExchangeRbacStudio.Domain.Scoping;
using ExchangeRbacStudio.Domain.Security;
using ExchangeRbacStudio.Domain.Validation;

namespace ExchangeRbacStudio.Application.Configuration;

public enum ConfigurationMode
{
    /// <summary>Add scoped access; existing assignments are never removed.</summary>
    New,

    /// <summary>Converge the application's assignments on one scope to the desired role set (may remove roles).</summary>
    Modify,
}

/// <summary>The administrator's desired configuration, as collected by the wizard.</summary>
public sealed record ConfigurationRequest(
    EntraServicePrincipal ServicePrincipal,
    IReadOnlyList<ExchangeRecipient> Mailboxes,
    IReadOnlyList<string> Roles,
    string ScopeName,
    string ScopeFilter,
    RecipientFilterStrategy Strategy,
    ConfigurationMode Mode = ConfigurationMode.New,
    string? ExistingScopeName = null);

/// <summary>The current Exchange configuration relevant to a request, read fresh from Exchange Online.</summary>
public sealed record CurrentConfiguration(
    ExchangeServicePrincipal? Pointer,
    bool PointerMismatch,
    IReadOnlyList<ExchangeServicePrincipal> AllPointers,
    IReadOnlyList<ManagementScope> Scopes,
    IReadOnlyList<ApplicationRoleInfo> TenantRoles,
    IReadOnlyList<ManagementRoleAssignment> AllApplicationAssignments,
    IReadOnlyList<ManagementRoleAssignment> ServicePrincipalAssignments,
    IReadOnlyList<ScopeReference>? TargetScopeReferences,
    IReadOnlyList<ScopeReference>? ExistingScopeReferences)
{
    public ManagementScope? FindScope(string name) => Scopes.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Everything preflight learned; consumed by the planner, the review page and the report.</summary>
public sealed record PreflightContext(
    SessionSnapshot Session,
    EntraServicePrincipal? EntraServicePrincipal,
    CurrentConfiguration? State,
    IReadOnlyList<ExchangeRecipient> RevalidatedMailboxes,
    EntraGrantAnalysis? GrantAnalysis,
    RbacOverlapAnalysis? Overlap,
    ScopeVerification? ScopeVerification,
    IReadOnlyList<ApplicationAccessPolicy> MatchingAccessPolicies);

public sealed record PreflightOutcome(PreflightReport Report, PreflightContext Context, ConfigurationRequest Request);
