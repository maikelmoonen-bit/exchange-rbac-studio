using System.Net;
using Azure.Identity;
using ExchangeRbacStudio.Application.Diagnostics;
using ExchangeRbacStudio.Application.Errors;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;

namespace ExchangeRbacStudio.Infrastructure.Graph;

/// <summary>Translates Microsoft Graph SDK and Azure.Identity exceptions into classified failures.</summary>
internal static class GraphErrorTranslator
{
    public static Exception Translate(Exception exception, string operation)
    {
        switch (exception)
        {
            case OperationCanceledException:
                return exception;

            case ODataError odata:
            {
                var status = odata.ResponseStatusCode;
                var code = odata.Error?.Code;
                var message = SensitiveDataRedactor.Redact(odata.Error?.Message ?? odata.Message);
                var kind = status switch
                {
                    401 => FailureKind.SessionExpired,
                    403 => FailureKind.AccessDenied,
                    404 => FailureKind.NotFound,
                    429 => FailureKind.Throttled,
                    >= 500 => FailureKind.Network,
                    400 => FailureKind.InvalidInput,
                    _ => FailureKind.Unknown,
                };

                var userMessage = kind switch
                {
                    FailureKind.AccessDenied => $"Microsoft Graph denied {operation}: {message}",
                    FailureKind.SessionExpired => "The Microsoft Graph session has expired. Reconnect to continue.",
                    FailureKind.Throttled => $"Microsoft Graph is throttling requests ({operation}). Retry in a few minutes.",
                    _ => $"Microsoft Graph {operation} failed: {message}",
                };

                var remediation = kind == FailureKind.AccessDenied
                    ? "Reading service principals and their application permissions requires the delegated Application.Read.All permission and a directory role such as Directory Readers, Global Reader or Cloud Application Administrator. Activate the role (PIM) and reconnect if needed."
                    : null;
                return new GraphOperationException(kind, userMessage, status, code, $"HTTP {status} {code}: {message}", remediation, odata);
            }

            case AuthenticationRequiredException:
                return new GraphOperationException(FailureKind.SessionExpired, "Your Microsoft Graph sign-in must be renewed. Reconnect to continue.", null, null,
                    SensitiveDataRedactor.Redact(exception.Message), inner: exception);

            case AuthenticationFailedException auth:
            {
                var text = SensitiveDataRedactor.Redact(auth.Message + " " + auth.InnerException?.Message);
                var remediation = text.Contains("AADSTS53003", StringComparison.OrdinalIgnoreCase)
                    ? "A Conditional Access policy blocked the sign-in. Use a compliant device or location."
                    : text.Contains("AADSTS65001", StringComparison.OrdinalIgnoreCase)
                        ? "The sign-in client has not been granted consent for the required delegated permissions (User.Read, Application.Read.All). Ask an administrator to grant consent, or use your organization's own app registration."
                        : text.Contains("AADSTS700016", StringComparison.OrdinalIgnoreCase)
                            ? "The configured sign-in client ID does not exist in this tenant or cloud. Check the client ID in the sign-in options."
                            : text.Contains("AADSTS50011", StringComparison.OrdinalIgnoreCase)
                                ? "The app registration does not allow the redirect URI http://localhost. Add it as a 'Mobile and desktop applications' redirect URI."
                                : null;
                var cancelled = text.Contains("cancel", StringComparison.OrdinalIgnoreCase);
                return new GraphOperationException(cancelled ? FailureKind.AuthenticationCancelled : FailureKind.AuthenticationFailed,
                    cancelled ? "Sign-in was cancelled." : "Sign-in to Microsoft Entra ID failed: " + FirstLine(text), null, null, text, remediation, exception);
            }

            case ApiException api:
                return new GraphOperationException(api.ResponseStatusCode == (int)HttpStatusCode.TooManyRequests ? FailureKind.Throttled : FailureKind.Unknown,
                    $"Microsoft Graph {operation} failed (HTTP {api.ResponseStatusCode}).", api.ResponseStatusCode, null, SensitiveDataRedactor.Redact(api.Message), inner: api);

            case HttpRequestException http:
                return new GraphOperationException(FailureKind.Network, $"A network problem interrupted {operation}: {SensitiveDataRedactor.Redact(http.Message)}", null, null,
                    SensitiveDataRedactor.Redact(http.ToString()), "Check network connectivity and proxy settings.", http);

            case StudioOperationException:
                return exception;

            default:
                return new GraphOperationException(FailureKind.Unknown, $"Microsoft Graph {operation} failed: {SensitiveDataRedactor.Redact(exception.Message)}", null, null,
                    SensitiveDataRedactor.Redact(exception.ToString()), inner: exception);
        }
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        return index < 0 ? text : text[..index];
    }
}
