using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ExchangeRbacStudio.Domain.Permissions;

namespace ExchangeRbacStudio.Domain.Scoping;

public sealed record NamingContext(
    string ApplicationDisplayName,
    Guid AppId,
    IReadOnlyList<string> RoleNames,
    int MailboxCount,
    DateTimeOffset Timestamp);

/// <summary>
/// Generates descriptive, Exchange-safe names for management scopes and role assignments from configurable templates.
/// Supported tokens: {APP}, {APPID}, {APPID8}, {CATEGORY}, {ROLE}, {COUNT}, {DATE}.
/// </summary>
public static partial class ScopeNamingPolicy
{
    /// <summary>Exchange limits management scope and role assignment names to 64 characters.</summary>
    public const int MaxLength = 64;

    public const string DefaultScopeTemplate = "SCOPE_APP_{APP}_{CATEGORY}";
    public const string DefaultAssignmentTemplate = "RA_{APP}_{ROLE}";

    public static IReadOnlyList<string> SupportedTokens { get; } = ["{APP}", "{APPID}", "{APPID8}", "{CATEGORY}", "{ROLE}", "{COUNT}", "{DATE}"];

    public static string RenderScopeName(string template, NamingContext context) =>
        Render(string.IsNullOrWhiteSpace(template) ? DefaultScopeTemplate : template, context, roleName: null);

    public static string RenderAssignmentName(string template, NamingContext context, string roleName) =>
        Render(string.IsNullOrWhiteSpace(template) ? DefaultAssignmentTemplate : template, context, roleName);

    public static string CategoryToken(IReadOnlyList<string> roleNames)
    {
        var tokens = roleNames
            .Select(r => ApplicationRoleCatalog.TryGet(r)?.NameToken ?? "APP")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return tokens.Count switch
        {
            0 => "NONE",
            1 => tokens[0],
            _ => "MULTI",
        };
    }

    public static string RoleToken(string roleName)
    {
        var permission = roleName.StartsWith("Application ", StringComparison.OrdinalIgnoreCase) ? roleName["Application ".Length..] : roleName;
        return SanitizeSegment(permission);
    }

    /// <summary>Validates an administrator-supplied object name.</summary>
    public static bool IsValidName(string? name, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "A name is required.";
            return false;
        }

        if (name.Length > MaxLength)
        {
            error = $"The name must be {MaxLength} characters or fewer (currently {name.Length}).";
            return false;
        }

        if (!AllowedNamePattern().IsMatch(name))
        {
            error = "Use only letters, digits, spaces, underscores, hyphens and periods.";
            return false;
        }

        if (name != name.Trim())
        {
            error = "The name cannot start or end with a space.";
            return false;
        }

        return true;
    }

    /// <summary>Appends a numeric suffix until the name does not collide with an existing name.</summary>
    public static string EnsureUnique(string name, IEnumerable<string> existingNames)
    {
        var existing = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(name)) return name;

        for (var i = 2; i < 1000; i++)
        {
            var suffix = "_" + i.ToString(CultureInfo.InvariantCulture);
            var candidate = Truncate(name, MaxLength - suffix.Length).TrimEnd('_', '-', '.') + suffix;
            if (!existing.Contains(candidate)) return candidate;
        }

        throw new InvalidOperationException("Unable to generate a unique name.");
    }

    private static string Render(string template, NamingContext context, string? roleName)
    {
        var appToken = SanitizeSegment(context.ApplicationDisplayName);
        if (string.IsNullOrEmpty(appToken)) appToken = context.AppId.ToString("N")[..8].ToUpperInvariant();

        var rendered = template
            .Replace("{APPID8}", context.AppId.ToString("N")[..8].ToUpperInvariant(), StringComparison.OrdinalIgnoreCase)
            .Replace("{APPID}", context.AppId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            .Replace("{APP}", appToken, StringComparison.OrdinalIgnoreCase)
            .Replace("{CATEGORY}", CategoryToken(context.RoleNames), StringComparison.OrdinalIgnoreCase)
            .Replace("{ROLE}", roleName is null ? CategoryToken(context.RoleNames) : RoleToken(roleName), StringComparison.OrdinalIgnoreCase)
            .Replace("{COUNT}", context.MailboxCount.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{DATE}", context.Timestamp.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        var sanitized = SanitizeName(rendered);
        return string.IsNullOrEmpty(sanitized) ? "SCOPE_APP_" + context.AppId.ToString("N")[..8].ToUpperInvariant() : sanitized;
    }

    /// <summary>Uppercases and reduces a free-text value to [A-Z0-9_].</summary>
    public static string SanitizeSegment(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }

        return MultipleUnderscores().Replace(sb.ToString(), "_").Trim('_');
    }

    private static string SanitizeName(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        var collapsed = MultipleUnderscores().Replace(sb.ToString(), "_").Trim('_', '-', '.');
        return Truncate(collapsed, MaxLength).TrimEnd('_', '-', '.');
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    [GeneratedRegex("^[A-Za-z0-9 _.-]+$")]
    private static partial Regex AllowedNamePattern();

    [GeneratedRegex("_{2,}")]
    private static partial Regex MultipleUnderscores();
}
