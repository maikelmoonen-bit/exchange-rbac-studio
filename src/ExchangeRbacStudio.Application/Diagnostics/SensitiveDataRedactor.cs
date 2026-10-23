using System.Text.RegularExpressions;

namespace ExchangeRbacStudio.Application.Diagnostics;

/// <summary>
/// Removes credentials and tokens from text before it is logged, displayed in diagnostics or written to reports.
/// Applied defensively at every output boundary; the application itself never handles secrets intentionally.
/// </summary>
public static partial class SensitiveDataRedactor
{
    public const string Placeholder = "[REDACTED]";

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var result = JwtPattern().Replace(text, Placeholder);
        result = BearerPattern().Replace(result, "$1" + Placeholder);
        result = KeyValuePattern().Replace(result, "$1" + Placeholder);
        result = AuthorizationCodePattern().Replace(result, "$1" + Placeholder);
        result = JsonPropertyPattern().Replace(result, "$1" + Placeholder + "\"");
        result = AuthorizationHeaderPattern().Replace(result, "$1" + Placeholder);
        return result;
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"(?i)(bearer\s+)[A-Za-z0-9\-._~+/]{16,}=*")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)\b((?:access_token|refresh_token|id_token|client_secret|client_assertion|password|pwd|assertion)\s*=\s*)[^&\s""']+")]
    private static partial Regex KeyValuePattern();

    [GeneratedRegex(@"(?i)\b(code\s*=\s*)[A-Za-z0-9\-._~]{20,}")]
    private static partial Regex AuthorizationCodePattern();

    [GeneratedRegex(@"(?i)(""(?:access_?token|refresh_?token|id_?token|client_?secret|password|secret)""\s*:\s*"")[^""]*""")]
    private static partial Regex JsonPropertyPattern();

    [GeneratedRegex(@"(?i)(authorization\s*:\s*)(?:(?:bearer|basic|negotiate)\s+)?[^\s""',;\]\}]+")]
    private static partial Regex AuthorizationHeaderPattern();
}
