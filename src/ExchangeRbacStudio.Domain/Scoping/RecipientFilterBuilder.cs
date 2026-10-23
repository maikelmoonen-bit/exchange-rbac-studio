using System.Globalization;
using ExchangeRbacStudio.Domain.Exchange;

namespace ExchangeRbacStudio.Domain.Scoping;

/// <summary>
/// The immutable recipient property an exact-identity scope filter matches on.
/// <para>
/// PrimarySmtpAddress is deliberately not offered: Microsoft documents that any filter on PrimarySmtpAddress also
/// searches the EmailAddresses (proxy address) property, and addresses can be reassigned to other recipients over
/// time. GUID-valued identifiers are unique and never change for the lifetime of the object.
/// </para>
/// </summary>
public enum RecipientFilterStrategy
{
    /// <summary>The Microsoft Entra object ID of the mailbox owner (msExchExternalDirectoryObjectId).</summary>
    ExternalDirectoryObjectId,

    /// <summary>The Exchange directory object GUID of the recipient (objectGuid).</summary>
    ObjectGuid,

    /// <summary>The mailbox GUID (msExchMailboxGuid).</summary>
    ExchangeGuid,
}

public sealed record RecipientFilterBuildResult(
    string? Filter,
    RecipientFilterStrategy Strategy,
    IReadOnlyList<Guid> IdentityValues,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Filter is not null && Errors.Count == 0;
}

public static class RecipientFilterBuilder
{
    /// <summary>Upper bound for generated filter length; larger selections should use a group or administrative unit design.</summary>
    public const int MaxFilterLength = 16_000;

    public static string PropertyName(RecipientFilterStrategy strategy) => strategy switch
    {
        RecipientFilterStrategy.ExternalDirectoryObjectId => "ExternalDirectoryObjectId",
        RecipientFilterStrategy.ObjectGuid => "Guid",
        RecipientFilterStrategy.ExchangeGuid => "ExchangeGuid",
        _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null),
    };

    public static bool TryGetIdentityValue(ExchangeRecipient recipient, RecipientFilterStrategy strategy, out Guid value, out string? error)
    {
        value = Guid.Empty;
        error = null;
        switch (strategy)
        {
            case RecipientFilterStrategy.ExternalDirectoryObjectId:
                if (!Guid.TryParse(recipient.ExternalDirectoryObjectId, out value) || value == Guid.Empty)
                {
                    error = $"{recipient.Label} has no Microsoft Entra object ID (ExternalDirectoryObjectId).";
                    return false;
                }
                return true;
            case RecipientFilterStrategy.ObjectGuid:
                if (recipient.Guid is not { } g || g == Guid.Empty)
                {
                    error = $"{recipient.Label} has no directory object GUID.";
                    return false;
                }
                value = g;
                return true;
            case RecipientFilterStrategy.ExchangeGuid:
                if (recipient.ExchangeGuid is not { } eg || eg == Guid.Empty)
                {
                    error = $"{recipient.Label} has no mailbox GUID (ExchangeGuid).";
                    return false;
                }
                value = eg;
                return true;
            default:
                error = $"Unsupported filter strategy {strategy}.";
                return false;
        }
    }

    /// <summary>
    /// Builds an OPATH recipient restriction filter that matches exactly the given recipients by an immutable identifier.
    /// Values are formatted from parsed GUIDs, so no user-supplied text ever reaches the filter expression.
    /// </summary>
    public static RecipientFilterBuildResult Build(IEnumerable<ExchangeRecipient> recipients, RecipientFilterStrategy strategy)
    {
        var errors = new List<string>();
        var values = new SortedSet<Guid>();

        foreach (var recipient in recipients)
        {
            var validation = RecipientSupport.Validate(recipient);
            if (!validation.IsSupported)
            {
                errors.Add($"{recipient.Label}: {validation.Reason}");
                continue;
            }

            if (!TryGetIdentityValue(recipient, strategy, out var value, out var error))
            {
                errors.Add(error!);
                continue;
            }

            if (!values.Add(value))
                errors.Add($"{recipient.Label} is selected more than once.");
        }

        if (values.Count == 0 && errors.Count == 0)
            errors.Add("At least one mailbox must be selected.");

        if (errors.Count > 0)
            return new RecipientFilterBuildResult(null, strategy, values.ToList(), errors);

        var filter = Compose(strategy, values);
        if (filter.Length > MaxFilterLength)
        {
            errors.Add($"The generated filter is {filter.Length} characters long, which exceeds the supported maximum of {MaxFilterLength}. Reduce the number of mailboxes per scope.");
            return new RecipientFilterBuildResult(null, strategy, values.ToList(), errors);
        }

        return new RecipientFilterBuildResult(filter, strategy, values.ToList(), errors);
    }

    public static string Compose(RecipientFilterStrategy strategy, IEnumerable<Guid> values)
    {
        var property = PropertyName(strategy);
        var clauses = values
            .OrderBy(v => v)
            .Select(v => $"{property} -eq '{v.ToString("D", CultureInfo.InvariantCulture)}'")
            .ToList();

        return clauses.Count == 1 ? clauses[0] : string.Join(" -or ", clauses.Select(c => $"({c})"));
    }
}
