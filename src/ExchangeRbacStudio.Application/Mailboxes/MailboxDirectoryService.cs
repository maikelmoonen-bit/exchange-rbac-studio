using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Application.Validation;
using ExchangeRbacStudio.Domain.Exchange;

namespace ExchangeRbacStudio.Application.Mailboxes;

public sealed record MailboxCandidate(ExchangeRecipient Recipient, RecipientValidation Validation);

public sealed record MailboxResolution(string Input, MailboxCandidate? Candidate, string? Error);

/// <summary>Searches and validates Exchange recipients. A mailbox is only accepted after its actual type is validated.</summary>
public sealed class MailboxDirectoryService(ConnectionManager connections, IExchangeRbacGateway exchange)
{
    public const int SearchResultLimit = 50;

    public async Task<IReadOnlyList<MailboxCandidate>> SearchAsync(string text, CancellationToken cancellationToken)
    {
        connections.RequireExchange();
        var term = InputValidation.NormalizeSearchText(text);
        var recipients = await exchange.SearchRecipientsAsync(term, SearchResultLimit, cancellationToken).ConfigureAwait(false);
        return recipients.Select(r => new MailboxCandidate(r, RecipientSupport.Validate(r))).ToList();
    }

    public async Task<MailboxResolution> ResolveAsync(string input, CancellationToken cancellationToken)
    {
        connections.RequireExchange();
        if (!InputValidation.TryNormalizeMailboxIdentity(input, out var identity, out var error))
            return new MailboxResolution(input, null, error);

        try
        {
            var recipient = await exchange.GetRecipientAsync(identity, cancellationToken).ConfigureAwait(false);
            return recipient is null
                ? new MailboxResolution(input, null, "No recipient with this identity exists in Exchange Online.")
                : new MailboxResolution(input, new MailboxCandidate(recipient, RecipientSupport.Validate(recipient)), null);
        }
        catch (StudioOperationException ex) when (ex.Kind is FailureKind.NotFound or FailureKind.InvalidInput)
        {
            return new MailboxResolution(input, null, ex.Message);
        }
    }

    /// <summary>Accepts a pasted list (one identity per line, or separated by commas/semicolons) and resolves each.</summary>
    public async Task<IReadOnlyList<MailboxResolution>> ResolveManyAsync(string pastedText, int maxItems, CancellationToken cancellationToken)
    {
        var inputs = (pastedText ?? string.Empty)
            .Split(['\r', '\n', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (inputs.Count > maxItems)
            throw new StudioOperationException(FailureKind.InvalidInput, $"At most {maxItems} mailboxes can be added at once ({inputs.Count} provided).");

        var results = new List<MailboxResolution>(inputs.Count);
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ResolveAsync(input, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Re-reads previously selected recipients by their immutable GUID to confirm they still exist and are supported.</summary>
    public async Task<IReadOnlyList<(ExchangeRecipient Selected, MailboxCandidate? Current, string? Problem)>> RevalidateAsync(
        IReadOnlyList<ExchangeRecipient> selected, CancellationToken cancellationToken)
    {
        var results = new List<(ExchangeRecipient, MailboxCandidate?, string?)>(selected.Count);
        foreach (var recipient in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = recipient.Guid?.ToString("D") ?? recipient.PrimarySmtpAddress ?? recipient.Identity;
            ExchangeRecipient? current;
            try
            {
                current = await exchange.GetRecipientAsync(identity, cancellationToken).ConfigureAwait(false);
            }
            catch (StudioOperationException ex) when (ex.Kind == FailureKind.NotFound)
            {
                current = null;
            }

            if (current is null)
            {
                results.Add((recipient, null, "The recipient no longer exists."));
                continue;
            }

            var candidate = new MailboxCandidate(current, RecipientSupport.Validate(current));
            string? problem = null;
            if (!candidate.Validation.IsSupported) problem = candidate.Validation.Reason;
            else if (current.IdentityKey != recipient.IdentityKey) problem = "The recipient identity changed since it was selected.";
            results.Add((recipient, candidate, problem));
        }

        return results;
    }
}
