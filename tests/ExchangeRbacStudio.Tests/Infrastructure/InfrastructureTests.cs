using System.Text.Json;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Settings;
using ExchangeRbacStudio.Application.Reporting;
using ExchangeRbacStudio.Domain.Auditing;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Infrastructure.Exchange;
using ExchangeRbacStudio.Infrastructure.Persistence;
using ExchangeRbacStudio.Infrastructure.Reporting;

namespace ExchangeRbacStudio.Tests.Infrastructure;

public sealed class ExchangeErrorClassifierTests
{
    [Theory]
    [InlineData("The operation couldn't be performed because object 'SCOPE_X' couldn't be found on 'AM6PR01A001DC01'.", FailureKind.NotFound)]
    [InlineData("The management scope \"SCOPE_X\" already exists.", FailureKind.AlreadyExists)]
    [InlineData("The role assignment isn't within your current write scope.", FailureKind.AccessDenied)]
    [InlineData("You must call the Connect-ExchangeOnline cmdlet before calling any other cmdlets.", FailureKind.NotConnected)]
    [InlineData("UnAuthorized. Token has expired.", FailureKind.SessionExpired)]
    [InlineData("Server Busy: the request was throttled, please try again later.", FailureKind.Throttled)]
    [InlineData("No such host is known. (outlook.office365.com:443)", FailureKind.Network)]
    [InlineData("Cannot bind parameter 'RecipientRestrictionFilter'. Invalid filter syntax.", FailureKind.InvalidInput)]
    [InlineData("AADSTS53003: Access has been blocked by Conditional Access policies.", FailureKind.AuthenticationFailed)]
    [InlineData("User canceled authentication.", FailureKind.AuthenticationCancelled)]
    public void Exchange_errors_are_classified(string message, FailureKind expected) =>
        Assert.Equal(expected, ExchangeErrorClassifier.KindOf(new HostError { Message = message }));

    [Theory]
    [InlineData("InvalidRequest", FailureKind.InvalidInput)]
    [InlineData("CommandNotAvailable", FailureKind.CommandNotAvailable)]
    [InlineData("ModuleNotFound", FailureKind.ModuleMissing)]
    [InlineData("ModuleVersion", FailureKind.UnsupportedModuleVersion)]
    public void Host_error_kinds_take_precedence(string kind, FailureKind expected) =>
        Assert.Equal(expected, ExchangeErrorClassifier.KindOf(new HostError { Kind = kind, Message = "x" }));

    [Fact]
    public void Conditional_access_failures_include_remediation()
    {
        var error = ExchangeErrorClassifier.Classify(new HostError { Message = "AADSTS53003: Access has been blocked by Conditional Access policies." }, "Connect-ExchangeOnline");
        Assert.Contains("Conditional Access", error.Remediation);
        Assert.Equal("Connect-ExchangeOnline", error.CommandName);
    }
}

public sealed class HostRecordMappingTests
{
    private static HostRecord Record(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!);

    [Fact]
    public void Role_assignment_records_map_to_scope_kinds()
    {
        var scoped = PowerShellExchangeGateway.ToAssignment(Record("""{"Name":"RA1","Identity":"RA1","Role":"Application Mail.Read","RoleAssignee":"6233fba6","RoleAssigneeName":"6233fba6","RoleAssigneeType":"ServicePrincipal","CustomResourceScope":"SCOPE_X","RecipientWriteScope":"CustomRecipientScope","Enabled":true}"""));
        Assert.Equal(AssignmentScopeKind.ManagementScope, scoped.ScopeKind);
        Assert.Equal("SCOPE_X", scoped.ScopeReference);

        var orgWide = PowerShellExchangeGateway.ToAssignment(Record("""{"Name":"RA2","Role":"Application Mail.Read","RoleAssigneeType":"ServicePrincipal","CustomResourceScope":null,"RecipientWriteScope":"Organization","Enabled":"True"}"""));
        Assert.Equal(AssignmentScopeKind.Organization, orgWide.ScopeKind);
        Assert.True(orgWide.Enabled);
    }

    [Fact]
    public void Recipient_and_connection_records_are_parsed_tolerantly()
    {
        var recipient = PowerShellExchangeGateway.ToRecipient(Record("""{"Identity":"alice","DisplayName":"Alice","PrimarySmtpAddress":"alice@contoso.com","RecipientTypeDetails":"UserMailbox","ExchangeGuid":"11111111-aaaa-4aaa-8aaa-111111111111","Guid":"aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa","ExternalDirectoryObjectId":"e1e1e1e1-0001-4001-8001-000000000001"}"""));
        Assert.Equal(Guid.Parse("aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa"), recipient.Guid);
        Assert.True(RecipientSupport.Validate(recipient).IsSupported);

        var connection = PowerShellExchangeGateway.ToConnectionInfo(Record("""{"State":"Connected","TokenStatus":"Active","TenantID":"8f2b6a1e-4c1d-4f3a-9d7e-2b5c6a7d8e9f","UserPrincipalName":"admin@contoso.com","TokenExpiryTimeUTC":"2026-10-23T10:30:00.0000000Z"}"""));
        Assert.True(connection.IsConnected);
        Assert.Equal(Fixtures.TenantId, connection.TenantId);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 10, 30, 0, TimeSpan.Zero), connection.TokenExpiryUtc);
    }
}

public sealed class AuditLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ers-audit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static AuditEntry Entry(string action) => new()
    {
        EntryId = Guid.NewGuid(),
        TimestampUtc = DateTimeOffset.UtcNow,
        TenantId = Fixtures.TenantId,
        Category = AuditCategories.Change,
        Action = action,
        Outcome = AuditOutcome.Succeeded,
        Message = action + " completed",
    };

    [Fact]
    public async Task Entries_are_hash_chained_and_verifiable()
    {
        var log = new JsonlAuditLog(new AppPaths(_root, _root, _root), TimeProvider.System);
        await log.AppendAsync(Entry("New-ServicePrincipal"));
        await log.AppendAsync(Entry("New-ManagementScope"));
        await log.AppendAsync(Entry("New-ManagementRoleAssignment"));

        var entries = await log.ReadAsync(new AuditQuery(Fixtures.TenantId));
        Assert.Equal(3, entries.Count);
        Assert.Equal("New-ManagementRoleAssignment", entries[0].Action);
        Assert.Null(entries[^1].PreviousHash);
        Assert.Equal(entries[^1].Hash, entries[^2].PreviousHash);

        var integrity = await log.VerifyIntegrityAsync();
        Assert.True(integrity.IsIntact);
        Assert.Equal(3, integrity.EntriesVerified);
    }

    [Fact]
    public async Task Tampering_is_detected()
    {
        var log = new JsonlAuditLog(new AppPaths(_root, _root, _root), TimeProvider.System);
        await log.AppendAsync(Entry("Remove-ManagementRoleAssignment"));
        await log.AppendAsync(Entry("Remove-ManagementScope"));

        var file = Directory.GetFiles(Path.Combine(_root, "Audit")).Single();
        var lines = await File.ReadAllLinesAsync(file);
        lines[0] = lines[0].Replace("Remove-ManagementRoleAssignment", "Get-ManagementRoleAssignment", StringComparison.Ordinal);
        await File.WriteAllLinesAsync(file, lines);

        var integrity = await log.VerifyIntegrityAsync();
        Assert.False(integrity.IsIntact);
        Assert.Contains(integrity.Problems, p => p.Contains("does not match its hash"));
    }

    [Fact]
    public async Task Deleted_entries_break_the_chain()
    {
        var log = new JsonlAuditLog(new AppPaths(_root, _root, _root), TimeProvider.System);
        await log.AppendAsync(Entry("a"));
        await log.AppendAsync(Entry("b"));
        await log.AppendAsync(Entry("c"));
        var file = Directory.GetFiles(Path.Combine(_root, "Audit")).Single();
        var lines = (await File.ReadAllLinesAsync(file)).ToList();
        lines.RemoveAt(1);
        await File.WriteAllLinesAsync(file, lines);

        Assert.Contains((await log.VerifyIntegrityAsync()).Problems, p => p.Contains("chain broken"));
    }
}

public sealed class ChangeLockAndSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ers-lock-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Lock_is_exclusive_per_tenant_and_released_on_dispose()
    {
        var changeLock = new FileChangeLock(new AppPaths(_root, _root, _root));
        using (var first = changeLock.TryAcquire(Fixtures.TenantId))
        {
            Assert.NotNull(first);
            Assert.Null(changeLock.TryAcquire(Fixtures.TenantId));
            using var other = changeLock.TryAcquire(Fixtures.OtherTenantId);
            Assert.NotNull(other);
        }

        using var again = changeLock.TryAcquire(Fixtures.TenantId);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Settings_round_trip_and_invalid_files_fall_back_to_defaults()
    {
        var paths = new AppPaths(_root, _root, _root);
        var store = new JsonSettingsStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonSettingsStore>.Instance);
        await store.SaveAsync(store.Current with { MaxMailboxesPerScope = 25, Theme = ThemePreference.Dark });

        var reloaded = new JsonSettingsStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonSettingsStore>.Instance);
        Assert.Equal(25, reloaded.Current.MaxMailboxesPerScope);
        Assert.DoesNotContain("token", await File.ReadAllTextAsync(paths.SettingsFile), StringComparison.OrdinalIgnoreCase);

        await File.WriteAllTextAsync(paths.SettingsFile, "{ not json");
        Assert.Equal(50, new JsonSettingsStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonSettingsStore>.Instance).Current.MaxMailboxesPerScope);

        await Assert.ThrowsAsync<StudioOperationException>(() => store.SaveAsync(store.Current with { MaxMailboxesPerScope = 0 }));
    }
}

public sealed class ReportExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ers-report-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ChangeReport Report(string appName) => new()
    {
        ReportId = Guid.NewGuid(),
        Title = "Change report",
        GeneratedUtc = DateTimeOffset.UtcNow,
        ToolVersion = "1.0.0",
        DryRun = false,
        Tenant = new ReportTenant(Fixtures.TenantId, Fixtures.TenantName, "contoso.onmicrosoft.com", "Microsoft 365"),
        Administrator = new ReportActor(Fixtures.AdminUpn, "Admin", "WS01"),
        Application = new ReportApplication(appName, Fixtures.PoloAppId, Fixtures.PoloObjectId, "Created"),
        SecurityVerdict = new ReportSecurityVerdict(false, "NOT confirmed as securely scoped.", ["Tests not run."]),
        RemainingVerification = ["Run negative tests."],
    };

    [Fact]
    public void Html_report_encodes_all_values()
    {
        var html = HtmlReportRenderer.Render(Report("<script>alert('x')</script>"));
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains(Fixtures.TenantId.ToString(), html);
        Assert.Contains("NOT confirmed as securely scoped", html);
    }

    [Fact]
    public async Task Export_writes_json_and_html_without_secrets()
    {
        var exporter = new ReportFileExporter(new AppPaths(_root, _root, _root));
        var report = Report("Polo") with { RemainingVerification = ["Authorization: Bearer eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.eyJhdWQiOiJodHRwczovL2dyYXBoIn0.c2lnbmF0dXJlc2lnbmF0dXJl"] };
        var result = await exporter.ExportAsync(report, _root, "report:1");

        Assert.True(File.Exists(result.JsonPath));
        Assert.True(File.Exists(result.HtmlPath));
        Assert.DoesNotContain(":", Path.GetFileName(result.JsonPath));
        var json = await File.ReadAllTextAsync(result.JsonPath);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(Fixtures.TenantId.ToString(), document.RootElement.GetProperty("tenant").GetProperty("tenantId").GetString());
        Assert.DoesNotContain("eyJ0eXAi", json);
        Assert.DoesNotContain("eyJ0eXAi", await File.ReadAllTextAsync(result.HtmlPath));
    }
}
