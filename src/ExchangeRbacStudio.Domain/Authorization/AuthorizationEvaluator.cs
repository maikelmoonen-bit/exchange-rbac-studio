using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Permissions;

namespace ExchangeRbacStudio.Domain.Authorization;

public enum TestExpectation
{
    /// <summary>Positive test: the mailbox must be in scope for the role.</summary>
    InScope,

    /// <summary>Negative test: the mailbox must not be accessible with the role's capabilities.</summary>
    OutOfScope,
}

public enum AuthorizationVerdict
{
    Pass,
    Fail,
    Inconclusive,
}

public sealed record RoleAuthorizationOutcome(
    string Mailbox,
    string RoleName,
    TestExpectation Expectation,
    InScopeResult ExchangeResult,
    bool RoleAssigned,
    string? MatchingScope,
    string? ScopeType,
    bool EntraTenantWideGrant,
    AuthorizationVerdict Verdict,
    string Explanation);

/// <summary>
/// Interprets Test-ServicePrincipalAuthorization output per requested role. A single InScope = True row never
/// establishes all requested roles; every role is evaluated separately. Microsoft Entra tenant-wide grants are not
/// evaluated by the Exchange cmdlet and are therefore combined here explicitly.
/// </summary>
public static class AuthorizationEvaluator
{
    public static IReadOnlyList<RoleAuthorizationOutcome> Evaluate(
        string mailbox,
        TestExpectation expectation,
        IReadOnlyList<string> rolesOfInterest,
        IReadOnlyList<AuthorizationTestRow> rows,
        MailboxCapability entraTenantWideCapabilities)
    {
        var outcomes = new List<RoleAuthorizationOutcome>(rolesOfInterest.Count);
        foreach (var role in rolesOfInterest)
        {
            var roleCapabilities = ApplicationRoleCatalog.TryGet(role)?.Grants ?? MailboxCapability.None;
            var entraOverride = roleCapabilities != MailboxCapability.None
                && CapabilityLattice.Overlap(entraTenantWideCapabilities, roleCapabilities) != MailboxCapability.None;

            outcomes.Add(expectation == TestExpectation.InScope
                ? EvaluatePositive(mailbox, role, rows, entraOverride)
                : EvaluateNegative(mailbox, role, rows, entraOverride));
        }

        return outcomes;
    }

    public static AuthorizationVerdict Summarize(IEnumerable<RoleAuthorizationOutcome> outcomes)
    {
        var list = outcomes.ToList();
        if (list.Count == 0) return AuthorizationVerdict.Inconclusive;
        if (list.Any(o => o.Verdict == AuthorizationVerdict.Fail)) return AuthorizationVerdict.Fail;
        if (list.Any(o => o.Verdict == AuthorizationVerdict.Inconclusive)) return AuthorizationVerdict.Inconclusive;
        return AuthorizationVerdict.Pass;
    }

    private static RoleAuthorizationOutcome EvaluatePositive(string mailbox, string role, IReadOnlyList<AuthorizationTestRow> rows, bool entraOverride)
    {
        var roleRows = rows.Where(r => r.RoleName.Equals(role, StringComparison.OrdinalIgnoreCase)).ToList();
        if (roleRows.Count == 0)
        {
            return new(mailbox, role, TestExpectation.InScope, InScopeResult.Unknown, false, null, null, entraOverride, AuthorizationVerdict.Fail,
                $"'{role}' is not assigned to the service principal in Exchange Online.");
        }

        var inScope = roleRows.FirstOrDefault(r => r.InScope == InScopeResult.InScope);
        if (inScope is not null)
        {
            return new(mailbox, role, TestExpectation.InScope, InScopeResult.InScope, true, inScope.AllowedResourceScope, inScope.ScopeType, entraOverride, AuthorizationVerdict.Pass,
                $"In scope through '{inScope.AllowedResourceScope}' ({inScope.ScopeType})."
                + (entraOverride ? " Note: a tenant-wide Microsoft Entra grant also covers this permission." : string.Empty));
        }

        if (roleRows.Any(r => r.InScope is InScopeResult.NotRun or InScopeResult.Unknown))
        {
            return new(mailbox, role, TestExpectation.InScope, roleRows[0].InScope, true, null, roleRows[0].ScopeType, entraOverride, AuthorizationVerdict.Inconclusive,
                "Exchange did not evaluate scope membership for this mailbox.");
        }

        return new(mailbox, role, TestExpectation.InScope, InScopeResult.NotInScope, true, string.Join(", ", roleRows.Select(r => r.AllowedResourceScope)), roleRows[0].ScopeType, entraOverride,
            AuthorizationVerdict.Fail, $"'{role}' is assigned, but the mailbox is not in any of its scopes.");
    }

    private static RoleAuthorizationOutcome EvaluateNegative(string mailbox, string role, IReadOnlyList<AuthorizationTestRow> rows, bool entraOverride)
    {
        // Any assigned role whose capabilities overlap the role of interest can grant access to the mailbox.
        var relevantRows = rows.Where(r => ApplicationRoleCatalog.Overlaps(r.RoleName, role)).ToList();
        var inScope = relevantRows.FirstOrDefault(r => r.InScope == InScopeResult.InScope);

        if (inScope is not null)
        {
            return new(mailbox, role, TestExpectation.OutOfScope, InScopeResult.InScope, true, inScope.AllowedResourceScope, inScope.ScopeType, entraOverride, AuthorizationVerdict.Fail,
                $"Mailbox is IN scope through '{inScope.RoleName}' on '{inScope.AllowedResourceScope}' ({inScope.ScopeType}).");
        }

        if (entraOverride)
        {
            return new(mailbox, role, TestExpectation.OutOfScope, relevantRows.Count == 0 ? InScopeResult.NotInScope : relevantRows[0].InScope,
                relevantRows.Count > 0, null, null, true, AuthorizationVerdict.Fail,
                "Exchange RBAC does not grant access, but a tenant-wide Microsoft Entra application permission gives the application this access to every mailbox, including this one.");
        }

        if (relevantRows.Any(r => r.InScope is InScopeResult.NotRun or InScopeResult.Unknown))
        {
            return new(mailbox, role, TestExpectation.OutOfScope, InScopeResult.Unknown, true, null, null, false, AuthorizationVerdict.Inconclusive,
                "Exchange did not evaluate scope membership for this mailbox.");
        }

        return new(mailbox, role, TestExpectation.OutOfScope, InScopeResult.NotInScope, relevantRows.Count > 0, null, null, false, AuthorizationVerdict.Pass,
            relevantRows.Count == 0
                ? "No overlapping Exchange application role is assigned; the mailbox is out of scope."
                : "Out of scope for every overlapping Exchange application role assigned to the service principal.");
    }
}
