using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Scoping;

namespace ExchangeRbacStudio.Application.Scoping;

public enum ScopeVerificationStatus
{
    Verified,
    Overmatch,
    Undermatch,
    Mismatch,
    Invalid,
    Unverifiable,
}

/// <summary>Result of resolving a filter in Exchange Online and comparing the matches with the intended selection.</summary>
public sealed record ScopeVerification(
    ScopeVerificationStatus Status,
    string Message,
    ScopeMembershipComparison? Comparison,
    FilterAnalysis Analysis,
    string? TechnicalDetail,
    DateTimeOffset VerifiedUtc)
{
    public bool IsVerified => Status == ScopeVerificationStatus.Verified;
}

public sealed record ScopeNameCheck(bool IsUsable, ManagementScope? Existing, bool ExistingIsEquivalent, string Message);

/// <summary>
/// Designs and verifies exact-identity management scopes. A scope is accepted only when Exchange resolves the filter
/// to exactly the selected recipients; parsing successfully is never treated as proof of correctness.
/// </summary>
public sealed class ScopeDesigner(IExchangeRbacGateway exchange, TimeProvider time)
{
    /// <summary>Additional preview results requested beyond the intended count, so overmatching is always observable.</summary>
    public const int OvermatchHeadroom = 25;

    public static RecipientFilterBuildResult BuildFilter(IReadOnlyList<ExchangeRecipient> mailboxes, RecipientFilterStrategy strategy) =>
        RecipientFilterBuilder.Build(mailboxes, strategy);

    /// <summary>
    /// Suggests a scope name from the naming template. If a scope with identical membership already exists it is
    /// suggested for reuse; otherwise a unique name is generated.
    /// </summary>
    public static string SuggestName(string template, NamingContext context, string filter, IReadOnlyList<ManagementScope> existingScopes)
    {
        var equivalent = existingScopes.FirstOrDefault(s => RecipientFilterAnalyzer.AreEquivalentExactFilters(s.RecipientFilter, filter));
        if (equivalent is not null) return equivalent.Name;

        var name = ScopeNamingPolicy.RenderScopeName(template, context);
        return ScopeNamingPolicy.EnsureUnique(name, existingScopes.Select(s => s.Name));
    }

    public static ScopeNameCheck CheckName(string name, string filter, IReadOnlyList<ManagementScope> existingScopes, string? updatableScopeName)
    {
        if (!ScopeNamingPolicy.IsValidName(name, out var error))
            return new ScopeNameCheck(false, null, false, error!);

        var existing = existingScopes.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return new ScopeNameCheck(true, null, false, "A new management scope will be created.");

        if (RecipientFilterAnalyzer.AreEquivalentExactFilters(existing.RecipientFilter, filter))
            return new ScopeNameCheck(true, existing, true, "An existing scope with exactly the same membership filter will be reused.");

        if (updatableScopeName is not null && existing.Name.Equals(updatableScopeName, StringComparison.OrdinalIgnoreCase))
            return new ScopeNameCheck(true, existing, false, "The application's existing scope will be updated with the new filter (dependency checks apply).");

        return new ScopeNameCheck(false, existing, false,
            $"A management scope named '{existing.Name}' already exists with a different filter ({existing.RecipientFilter ?? "no filter"}). Choose another name; existing scopes are never overwritten implicitly.");
    }

    public static ManagementScope? FindEquivalentScope(string filter, IReadOnlyList<ManagementScope> existingScopes) =>
        existingScopes.FirstOrDefault(s => RecipientFilterAnalyzer.AreEquivalentExactFilters(s.RecipientFilter, filter));

    public async Task<ScopeVerification> VerifyAsync(string filter, IReadOnlyList<ExchangeRecipient> intended, CancellationToken cancellationToken)
    {
        var analysis = RecipientFilterAnalyzer.Analyze(filter);
        if (!analysis.IsParsed)
            return new ScopeVerification(ScopeVerificationStatus.Invalid, "The filter is not syntactically valid: " + analysis.ParseError, null, analysis, null, time.GetUtcNow());

        if (!analysis.IsExactIdentityFilter)
            return new ScopeVerification(ScopeVerificationStatus.Invalid,
                "Only exact-identity filters (immutable identifier equality) are accepted by this workflow. Broad, attribute-based or wildcard scopes are disabled.",
                null, analysis, null, time.GetUtcNow());

        if (analysis.ExactIdentities.Count != intended.Count)
            return new ScopeVerification(ScopeVerificationStatus.Mismatch,
                $"The filter contains {analysis.ExactIdentities.Count} identities but {intended.Count} mailboxes are selected.", null, analysis, null, time.GetUtcNow());

        RecipientPreviewResult preview;
        try
        {
            preview = await exchange.PreviewRecipientFilterAsync(filter, intended.Count + OvermatchHeadroom, cancellationToken).ConfigureAwait(false);
        }
        catch (StudioOperationException ex) when (ex.Kind == FailureKind.InvalidInput)
        {
            return new ScopeVerification(ScopeVerificationStatus.Invalid, "Exchange Online rejected the filter: " + ex.Message, null, analysis, ex.TechnicalDetail, time.GetUtcNow());
        }
        catch (StudioOperationException ex) when (!ex.IsSessionProblem)
        {
            return new ScopeVerification(ScopeVerificationStatus.Unverifiable,
                "The filter could not be resolved in Exchange Online, so its membership is unverified: " + ex.Message, null, analysis, ex.TechnicalDetail, time.GetUtcNow());
        }

        var comparison = ScopeMembershipComparison.Compare(intended, preview.Recipients);
        var status = comparison.Verdict switch
        {
            ScopeMatchVerdict.Exact when !preview.Truncated => ScopeVerificationStatus.Verified,
            ScopeMatchVerdict.Exact => ScopeVerificationStatus.Overmatch,
            ScopeMatchVerdict.Overmatch => ScopeVerificationStatus.Overmatch,
            ScopeMatchVerdict.Undermatch => ScopeVerificationStatus.Undermatch,
            _ => ScopeVerificationStatus.Mismatch,
        };

        var message = status switch
        {
            ScopeVerificationStatus.Verified => $"Exchange Online resolves the filter to exactly the {intended.Count} selected mailbox(es).",
            ScopeVerificationStatus.Overmatch => $"The filter matches {comparison.Unexpected.Count} recipient(s) that were not selected. The scope is rejected.",
            ScopeVerificationStatus.Undermatch => $"The filter does not match {comparison.Missing.Count} selected mailbox(es). The scope is rejected.",
            _ => $"The filter matches {comparison.Unexpected.Count} unexpected recipient(s) and misses {comparison.Missing.Count} selected mailbox(es). The scope is rejected.",
        };

        return new ScopeVerification(status, message, comparison, analysis, null, time.GetUtcNow());
    }
}
