namespace ExchangeRbacStudio.Domain.Security;

public enum FindingSeverity
{
    Info,
    Warning,
    High,
    Critical,
}

/// <summary>A security-relevant observation about an application's effective access.</summary>
public sealed record SecurityFinding(
    string Code,
    FindingSeverity Severity,
    string Title,
    string Detail,
    string? Remediation = null,
    bool UnderminesScoping = false,
    string? Subject = null);
