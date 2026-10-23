using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Reporting;
using ExchangeRbacStudio.Application.Settings;
using ExchangeRbacStudio.Domain.Auditing;
using ExchangeRbacStudio.Domain.Identity;

namespace ExchangeRbacStudio.TestSupport;

public sealed class FakeEntraDirectory : IEntraDirectory
{
    public Dictionary<Guid, EntraServicePrincipal> ServicePrincipals { get; } = new() { [Fixtures.PoloObjectId] = Fixtures.Polo };
    public Dictionary<Guid, EntraApplicationObject> Applications { get; } = new()
    {
        [Fixtures.PoloAppRegistrationObjectId] = new EntraApplicationObject(Fixtures.PoloAppRegistrationObjectId, Fixtures.PoloAppId, "Polo Test Automation"),
    };
    public Dictionary<Guid, List<EntraAppPermissionGrant>> Grants { get; } = [];
    public StudioOperationException? GrantsFailure { get; set; }
    public StudioOperationException? SignInFailure { get; set; }
    public TenantIdentity Tenant { get; set; } = Fixtures.Tenant;
    public bool IsSignedIn { get; private set; }

    public Task<EntraSignInResult> SignInAsync(EntraSignInOptions options, CancellationToken cancellationToken)
    {
        if (SignInFailure is not null) throw SignInFailure;
        IsSignedIn = true;
        return Task.FromResult(new EntraSignInResult(Tenant, Fixtures.Administrator));
    }

    public void SignOut() => IsSignedIn = false;

    public Task<IReadOnlyList<EntraServicePrincipal>> SearchServicePrincipalsAsync(string text, int maxResults, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EntraServicePrincipal>>(ServicePrincipals.Values
            .Where(sp => sp.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(maxResults).ToList());

    public Task<EntraServicePrincipal?> GetServicePrincipalByObjectIdAsync(Guid objectId, CancellationToken cancellationToken) =>
        Task.FromResult(ServicePrincipals.GetValueOrDefault(objectId));

    public Task<EntraServicePrincipal?> GetServicePrincipalByAppIdAsync(Guid appId, CancellationToken cancellationToken) =>
        Task.FromResult(ServicePrincipals.Values.FirstOrDefault(sp => sp.AppId == appId));

    public Task<EntraApplicationObject?> GetApplicationByObjectIdAsync(Guid objectId, CancellationToken cancellationToken) =>
        Task.FromResult(Applications.GetValueOrDefault(objectId));

    public Task<IReadOnlyList<EntraAppPermissionGrant>> GetAppPermissionGrantsAsync(Guid servicePrincipalObjectId, CancellationToken cancellationToken)
    {
        if (GrantsFailure is not null) return Task.FromException<IReadOnlyList<EntraAppPermissionGrant>>(GrantsFailure);
        return Task.FromResult<IReadOnlyList<EntraAppPermissionGrant>>(Grants.GetValueOrDefault(servicePrincipalObjectId)?.ToList() ?? []);
    }
}

public sealed class FakeSettingsStore : ISettingsStore
{
    public StudioSettings Current { get; private set; } = new() { GraphClient = GraphClientMode.MicrosoftGraphCommandLineTools };

    public event EventHandler<StudioSettings>? Changed;

    public Task SaveAsync(StudioSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        Changed?.Invoke(this, settings);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryAuditLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];
    public bool FailWrites { get; set; }
    public string StorageLocation => "memory";

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        if (FailWrites) throw new IOException("The audit log is not writable.");
        lock (Entries) Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ReadAsync(AuditQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(Entries.Where(e => query.TenantId is null || e.TenantId == query.TenantId).Reverse().Take(query.MaxEntries).ToList());

    public Task<AuditIntegrityResult> VerifyIntegrityAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AuditIntegrityResult(true, Entries.Count, []));
}

public sealed class FakeChangeLock : IChangeLock
{
    public bool Available { get; set; } = true;

    public IDisposable? TryAcquire(Guid tenantId) => Available ? new Releaser() : null;

    private sealed class Releaser : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

public sealed class FakeReportExporter : IReportExporter
{
    public List<ChangeReport> Exported { get; } = [];
    public string DefaultDirectory => Path.Combine(Path.GetTempPath(), "ers-tests");

    public Task<ReportExportResult> ExportAsync(ChangeReport report, string directory, string baseFileName, CancellationToken cancellationToken = default)
    {
        Exported.Add(report);
        return Task.FromResult(new ReportExportResult(Path.Combine(directory, baseFileName + ".json"), Path.Combine(directory, baseFileName + ".html")));
    }
}

/// <summary>A clock that starts at a fixed instant and can be advanced.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public TestClock() : this(new DateTimeOffset(2026, 10, 23, 9, 30, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
