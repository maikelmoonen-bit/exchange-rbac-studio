namespace ExchangeRbacStudio.Application.Errors;

public enum FailureKind
{
    Unknown,
    NotConnected,
    SessionExpired,
    AuthenticationFailed,
    AuthenticationCancelled,
    AccessDenied,
    CommandNotAvailable,
    NotFound,
    AlreadyExists,
    Throttled,
    Network,
    Timeout,
    InvalidInput,
    ModuleMissing,
    UnsupportedModuleVersion,
    HostUnavailable,
    TenantMismatch,
    Conflict,
    Cancelled,
    ConcurrentChange,
}

/// <summary>
/// A classified operational failure. <see cref="Exception.Message"/> is suitable for administrators;
/// <see cref="TechnicalDetail"/> holds the raw (already sanitized) provider error for the diagnostics panel.
/// </summary>
public class StudioOperationException : Exception
{
    public StudioOperationException(FailureKind kind, string message, string? technicalDetail = null, string? remediation = null, string? origin = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        TechnicalDetail = technicalDetail;
        Remediation = remediation;
        Origin = origin;
    }

    public FailureKind Kind { get; }

    public string? TechnicalDetail { get; }

    public string? Remediation { get; }

    /// <summary>The service that reported the failure (Microsoft Graph, Exchange Online, ...).</summary>
    public string? Origin { get; }

    public bool IsTransient => Kind is FailureKind.Throttled or FailureKind.Network or FailureKind.Timeout;

    public bool IsSessionProblem => Kind is FailureKind.SessionExpired or FailureKind.NotConnected or FailureKind.HostUnavailable;
}

public sealed class ExchangeOperationException : StudioOperationException
{
    public ExchangeOperationException(FailureKind kind, string message, string? commandName, string? errorId, string? technicalDetail, string? remediation = null, Exception? inner = null)
        : base(kind, message, technicalDetail, remediation, "Exchange Online", inner)
    {
        CommandName = commandName;
        ErrorId = errorId;
    }

    public string? CommandName { get; }

    public string? ErrorId { get; }
}

public sealed class GraphOperationException : StudioOperationException
{
    public GraphOperationException(FailureKind kind, string message, int? statusCode, string? errorCode, string? technicalDetail, string? remediation = null, Exception? inner = null)
        : base(kind, message, technicalDetail, remediation, "Microsoft Graph", inner)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int? StatusCode { get; }

    public string? ErrorCode { get; }
}
