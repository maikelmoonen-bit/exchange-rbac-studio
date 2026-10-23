using System.Text.RegularExpressions;
using ExchangeRbacStudio.Application.Errors;

namespace ExchangeRbacStudio.Application.Validation;

/// <summary>Validation of administrator-supplied identifiers at the trust boundary between UI and services.</summary>
public static partial class InputValidation
{
    public const int MaxSearchLength = 128;

    /// <summary>Normalizes free-text search input; throws for input that cannot be a legitimate search term.</summary>
    public static string NormalizeSearchText(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
            throw new StudioOperationException(FailureKind.InvalidInput, "Enter a search term.");
        if (value.Length > MaxSearchLength)
            throw new StudioOperationException(FailureKind.InvalidInput, $"Search terms are limited to {MaxSearchLength} characters.");
        if (value.Any(char.IsControl))
            throw new StudioOperationException(FailureKind.InvalidInput, "The search term contains control characters.");
        if (value.Contains('*'))
            throw new StudioOperationException(FailureKind.InvalidInput, "Wildcards are not supported. Enter part of a name, alias or address.");
        return value;
    }

    /// <summary>Accepts an SMTP address, a GUID or an alias as a mailbox identity.</summary>
    public static bool TryNormalizeMailboxIdentity(string? input, out string identity, out string? error)
    {
        identity = (input ?? string.Empty).Trim().Trim('<', '>', ';', ',').Trim();
        error = null;

        if (identity.Length == 0)
        {
            error = "Empty value.";
            return false;
        }

        if (identity.Length > 320 || identity.Any(char.IsControl) || identity.Contains('*'))
        {
            error = "Not a valid mailbox identity.";
            return false;
        }

        if (Guid.TryParse(identity, out var guid))
        {
            identity = guid.ToString("D");
            return true;
        }

        if (identity.Contains('@'))
        {
            if (SmtpAddress().IsMatch(identity)) return true;
            error = "Not a valid email address.";
            return false;
        }

        if (Alias().IsMatch(identity)) return true;
        error = "Enter an email address, alias or GUID.";
        return false;
    }

    /// <summary>Parses a GUID typed or pasted by the administrator (tolerates braces and whitespace).</summary>
    public static bool TryParseGuid(string? input, out Guid value) =>
        Guid.TryParse((input ?? string.Empty).Trim().Trim('{', '}'), out value);

    [GeneratedRegex(@"^[^\s@<>()\[\]\\,;:""']+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\.[A-Za-z]{2,}$")]
    private static partial Regex SmtpAddress();

    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'+\-/=?^_`{|}~.]{1,64}$")]
    private static partial Regex Alias();
}
