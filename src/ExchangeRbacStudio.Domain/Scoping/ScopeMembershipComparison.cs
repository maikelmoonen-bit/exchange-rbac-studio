using ExchangeRbacStudio.Domain.Exchange;

namespace ExchangeRbacStudio.Domain.Scoping;

public enum ScopeMatchVerdict
{
    /// <summary>The filter resolves to exactly the intended recipients.</summary>
    Exact,

    /// <summary>The filter includes recipients that were not selected.</summary>
    Overmatch,

    /// <summary>The filter misses recipients that were selected.</summary>
    Undermatch,

    /// <summary>The filter both misses selected recipients and includes unselected ones.</summary>
    Mismatch,
}

/// <summary>Result of comparing the recipients a filter actually resolves to against the intended selection.</summary>
public sealed record ScopeMembershipComparison(
    ScopeMatchVerdict Verdict,
    IReadOnlyList<ExchangeRecipient> Matched,
    IReadOnlyList<ExchangeRecipient> Unexpected,
    IReadOnlyList<ExchangeRecipient> Missing)
{
    public bool IsExact => Verdict == ScopeMatchVerdict.Exact;

    public static ScopeMembershipComparison Compare(IReadOnlyCollection<ExchangeRecipient> intended, IReadOnlyCollection<ExchangeRecipient> resolved)
    {
        var intendedKeys = intended.Select(r => r.IdentityKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolvedKeys = resolved.Select(r => r.IdentityKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unexpected = resolved.Where(r => !intendedKeys.Contains(r.IdentityKey)).DistinctBy(r => r.IdentityKey).ToList();
        var missing = intended.Where(r => !resolvedKeys.Contains(r.IdentityKey)).DistinctBy(r => r.IdentityKey).ToList();
        var matched = resolved.Where(r => intendedKeys.Contains(r.IdentityKey)).DistinctBy(r => r.IdentityKey).ToList();

        var verdict = (unexpected.Count, missing.Count) switch
        {
            (0, 0) => ScopeMatchVerdict.Exact,
            (> 0, 0) => ScopeMatchVerdict.Overmatch,
            (0, > 0) => ScopeMatchVerdict.Undermatch,
            _ => ScopeMatchVerdict.Mismatch,
        };

        return new ScopeMembershipComparison(verdict, matched, unexpected, missing);
    }
}
