using ExchangeRbacStudio.Application.Diagnostics;

namespace ExchangeRbacStudio.Application.Errors;

/// <summary>Presentation model for an error: understandable message plus sanitized technical diagnostics.</summary>
public sealed record UserFacingError(string Title, string Message, string? Remediation, string TechnicalDetails, FailureKind Kind)
{
    public static UserFacingError From(Exception exception, string? context = null)
    {
        var title = context ?? "The operation failed";
        switch (exception)
        {
            case StudioOperationException studio:
                return new UserFacingError(
                    TitleFor(studio.Kind, title),
                    SensitiveDataRedactor.Redact(studio.Message),
                    studio.Remediation ?? DefaultRemediation(studio.Kind),
                    BuildTechnical(studio),
                    studio.Kind);
            case OperationCanceledException:
                return new UserFacingError("Cancelled", "The operation was cancelled.", null, "OperationCanceledException", FailureKind.Cancelled);
            case TimeoutException:
                return new UserFacingError("Timed out", "The operation did not complete in time.", "Check the network connection and try again.",
                    SensitiveDataRedactor.Redact(exception.ToString()), FailureKind.Timeout);
            default:
                return new UserFacingError(
                    title,
                    SensitiveDataRedactor.Redact(exception.Message),
                    "If the problem persists, export diagnostics from Settings and review the log.",
                    SensitiveDataRedactor.Redact(exception.ToString()),
                    FailureKind.Unknown);
        }
    }

    private static string BuildTechnical(StudioOperationException ex)
    {
        var lines = new List<string> { $"Kind: {ex.Kind}" };
        if (ex.Origin is not null) lines.Add($"Source: {ex.Origin}");
        switch (ex)
        {
            case ExchangeOperationException exo:
                if (exo.CommandName is not null) lines.Add($"Command: {exo.CommandName}");
                if (exo.ErrorId is not null) lines.Add($"ErrorId: {exo.ErrorId}");
                break;
            case GraphOperationException graph:
                if (graph.StatusCode is not null) lines.Add($"HTTP status: {graph.StatusCode}");
                if (graph.ErrorCode is not null) lines.Add($"Error code: {graph.ErrorCode}");
                break;
        }

        if (!string.IsNullOrWhiteSpace(ex.TechnicalDetail)) lines.Add("Detail: " + ex.TechnicalDetail);
        if (ex.InnerException is not null) lines.Add("Inner: " + ex.InnerException.GetType().FullName + ": " + ex.InnerException.Message);
        return SensitiveDataRedactor.Redact(string.Join(Environment.NewLine, lines));
    }

    private static string TitleFor(FailureKind kind, string fallback) => kind switch
    {
        FailureKind.NotConnected => "Not connected",
        FailureKind.SessionExpired => "Session expired",
        FailureKind.AuthenticationFailed => "Sign-in failed",
        FailureKind.AuthenticationCancelled => "Sign-in cancelled",
        FailureKind.AccessDenied => "Insufficient permissions",
        FailureKind.CommandNotAvailable => "Insufficient Exchange permissions",
        FailureKind.Throttled => "Service is throttling requests",
        FailureKind.Network => "Network problem",
        FailureKind.Timeout => "Timed out",
        FailureKind.ModuleMissing => "ExchangeOnlineManagement module not found",
        FailureKind.UnsupportedModuleVersion => "Unsupported module version",
        FailureKind.HostUnavailable => "PowerShell host unavailable",
        FailureKind.TenantMismatch => "Tenant mismatch",
        FailureKind.ConcurrentChange => "Another change is in progress",
        FailureKind.Conflict => "Configuration conflict",
        _ => fallback,
    };

    private static string? DefaultRemediation(FailureKind kind) => kind switch
    {
        FailureKind.NotConnected => "Connect to Microsoft Entra ID and Exchange Online from the session bar.",
        FailureKind.SessionExpired => "Use Reconnect in the session bar. If your role is time-bound (PIM), re-activate it first.",
        FailureKind.AccessDenied or FailureKind.CommandNotAvailable =>
            "Activate the required role (Exchange Administrator or Organization Management for Exchange; Directory Readers or Global Reader for Entra ID) in Privileged Identity Management if applicable, then choose 'Reconnect & revalidate'.",
        FailureKind.Throttled => "Wait a few minutes and retry. Read operations are retried automatically.",
        FailureKind.Network => "Check network connectivity and proxy settings, then retry.",
        FailureKind.ModuleMissing => "Install the module in PowerShell 7: Install-Module ExchangeOnlineManagement -Scope CurrentUser",
        FailureKind.UnsupportedModuleVersion => "Update the module: Update-Module ExchangeOnlineManagement",
        FailureKind.HostUnavailable => "Verify that PowerShell 7 (pwsh.exe) is installed, or configure its path in Settings.",
        FailureKind.TenantMismatch => "Sign in to Microsoft Entra ID and Exchange Online with accounts from the same tenant.",
        _ => null,
    };
}
