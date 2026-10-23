using System.Text.RegularExpressions;
using ExchangeRbacStudio.Application.Diagnostics;
using ExchangeRbacStudio.Application.Errors;

namespace ExchangeRbacStudio.Infrastructure.Exchange;

/// <summary>Maps PowerShell/Exchange Online errors to failure kinds with actionable messages.</summary>
internal static partial class ExchangeErrorClassifier
{
    public static ExchangeOperationException Classify(HostError error, string command)
    {
        var message = SensitiveDataRedactor.Redact(error.Message ?? "Unknown error.");
        var technical = SensitiveDataRedactor.Redact(error.ToString());
        var kind = KindOf(error);
        var userMessage = kind switch
        {
            FailureKind.NotFound => $"{command}: the object was not found. {message}",
            FailureKind.AlreadyExists => $"{command}: the object already exists. {message}",
            FailureKind.AccessDenied => $"{command}: access denied. {message}",
            FailureKind.CommandNotAvailable => message,
            FailureKind.SessionExpired => "The Exchange Online session has expired or is no longer authorized. Reconnect to continue.",
            FailureKind.NotConnected => "Exchange Online is not connected. Connect to continue.",
            FailureKind.Throttled => $"Exchange Online is throttling requests ({command}). {message}",
            FailureKind.Network => $"A network problem interrupted {command}. {message}",
            FailureKind.AuthenticationCancelled => "Exchange Online sign-in was cancelled.",
            FailureKind.AuthenticationFailed => $"Exchange Online sign-in failed. {message}",
            _ => $"{command} failed: {message}",
        };

        string? remediation = kind switch
        {
            FailureKind.AuthenticationFailed when message.Contains("AADSTS53003", StringComparison.OrdinalIgnoreCase) =>
                "A Conditional Access policy blocked the sign-in. Sign in from a compliant device or location, or ask your identity team which policy applies.",
            FailureKind.AuthenticationFailed when message.Contains("AADSTS50076", StringComparison.OrdinalIgnoreCase) || message.Contains("AADSTS50079", StringComparison.OrdinalIgnoreCase) =>
                "Multi-factor authentication is required. Complete MFA in the sign-in window.",
            _ => null,
        };

        return new ExchangeOperationException(kind, userMessage, command, error.ErrorId, technical, remediation);
    }

    internal static FailureKind KindOf(HostError error)
    {
        switch (error.Kind)
        {
            case "InvalidRequest": return FailureKind.InvalidInput;
            case "CommandNotAvailable": return FailureKind.CommandNotAvailable;
            case "ModuleNotFound": return FailureKind.ModuleMissing;
            case "ModuleVersion": return FailureKind.UnsupportedModuleVersion;
        }

        var text = $"{error.Message} {error.InnerMessage} {error.ErrorId} {error.ExceptionType} {error.Reason}";

        if (CancelledPattern().IsMatch(text)) return FailureKind.AuthenticationCancelled;
        if (NotConnectedPattern().IsMatch(text)) return FailureKind.NotConnected;
        if (ExpiredPattern().IsMatch(text)) return FailureKind.SessionExpired;
        if (AuthenticationPattern().IsMatch(text)) return FailureKind.AuthenticationFailed;
        if (ThrottledPattern().IsMatch(text)) return FailureKind.Throttled;
        if (NotFoundPattern().IsMatch(text)) return FailureKind.NotFound;
        if (AlreadyExistsPattern().IsMatch(text)) return FailureKind.AlreadyExists;
        if (AccessDeniedPattern().IsMatch(text)) return FailureKind.AccessDenied;
        if (NetworkPattern().IsMatch(text)) return FailureKind.Network;
        if (InvalidPattern().IsMatch(text)) return FailureKind.InvalidInput;
        return FailureKind.Unknown;
    }

    [GeneratedRegex(@"(?i)user\s+(has\s+)?cancel+ed|authentication_canceled|access_denied.*cancel|operation\s+was\s+canceled\s+by\s+the\s+user")]
    private static partial Regex CancelledPattern();

    [GeneratedRegex(@"(?i)must call the Connect-ExchangeOnline|no active (exchange )?session|not connected to exchange")]
    private static partial Regex NotConnectedPattern();

    [GeneratedRegex(@"(?i)token (has )?expired|expired token|lifetime validation failed|\b401\b|unauthorized|re-?authenticate|invalid_grant|AADSTS700082|AADSTS50173")]
    private static partial Regex ExpiredPattern();

    [GeneratedRegex(@"(?i)AADSTS\d+|MsalServiceException|MsalClientException|authentication failed|interaction_required")]
    private static partial Regex AuthenticationPattern();

    [GeneratedRegex(@"(?i)throttl|server busy|too many requests|\b429\b|MicroDelay|budget|backoff|try again later|ServerBusyException")]
    private static partial Regex ThrottledPattern();

    [GeneratedRegex(@"(?i)couldn't be found|could not be found|ManagementObjectNotFoundException|ObjectNotFound|doesn't exist|does not exist")]
    private static partial Regex NotFoundPattern();

    [GeneratedRegex(@"(?i)already exists|ADObjectAlreadyExistsException|is already being used|duplicate|already assigned")]
    private static partial Regex AlreadyExistsPattern();

    [GeneratedRegex(@"(?i)access (is )?denied|isn't within your current write scope|not within your current write scope|doesn't have (the )?permission|insufficient (access|privileges|permissions)|\b403\b|forbidden|not authorized|PermissionDenied")]
    private static partial Regex AccessDeniedPattern();

    [GeneratedRegex(@"(?i)no such host|network|connection (was )?(closed|reset|refused|aborted)|SocketException|HttpRequestException|timed? ?out|name resolution|proxy|SSL connection|remote server")]
    private static partial Regex NetworkPattern();

    [GeneratedRegex(@"(?i)cannot bind|ParameterBindingException|invalid filter|filter (is )?invalid|isn't valid|is not valid|InvalidArgument|cannot convert|ValidationMetadataException|syntax error|unexpected token")]
    private static partial Regex InvalidPattern();
}
