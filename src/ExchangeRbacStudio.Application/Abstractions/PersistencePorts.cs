using ExchangeRbacStudio.Application.Reporting;
using ExchangeRbacStudio.Application.Settings;
using ExchangeRbacStudio.Domain.Auditing;

namespace ExchangeRbacStudio.Application.Abstractions;

public interface ISettingsStore
{
    StudioSettings Current { get; }

    event EventHandler<StudioSettings>? Changed;

    Task SaveAsync(StudioSettings settings, CancellationToken cancellationToken = default);
}

public sealed record AuditQuery(Guid? TenantId = null, DateTimeOffset? SinceUtc = null, int MaxEntries = 2000);

public sealed record AuditIntegrityResult(bool IsIntact, int EntriesVerified, IReadOnlyList<string> Problems);

/// <summary>Append-only, hash-chained local audit log.</summary>
public interface IAuditLog
{
    string StorageLocation { get; }

    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEntry>> ReadAsync(AuditQuery query, CancellationToken cancellationToken = default);

    Task<AuditIntegrityResult> VerifyIntegrityAsync(CancellationToken cancellationToken = default);
}

public sealed record ReportExportResult(string JsonPath, string HtmlPath);

public interface IReportExporter
{
    string DefaultDirectory { get; }

    Task<ReportExportResult> ExportAsync(ChangeReport report, string directory, string baseFileName, CancellationToken cancellationToken = default);
}

/// <summary>Cross-process lock that prevents concurrent change execution against the same tenant from this machine.</summary>
public interface IChangeLock
{
    /// <summary>Returns a handle that releases the lock when disposed, or null when another execution holds it.</summary>
    IDisposable? TryAcquire(Guid tenantId);
}

public sealed record StudioRuntimeInfo(string Version, string MachineName);
