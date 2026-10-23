using ExchangeRbacStudio.Application.Authorization;
using ExchangeRbacStudio.Application.Diagnostics;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Execution;
using ExchangeRbacStudio.Application.Reporting;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Application.Settings;
using ExchangeRbacStudio.Application.Validation;
using ExchangeRbacStudio.Domain.Authorization;
using ExchangeRbacStudio.Domain.Permissions;

namespace ExchangeRbacStudio.Tests.Application;

public sealed class SessionTests
{
    [Fact]
    public async Task Connect_binds_both_services_to_the_same_tenant()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var session = env.Connections.Current;
        Assert.True(session.IsFullyConnected);
        Assert.Equal(Fixtures.TenantId, session.TenantId);
        Assert.Empty(session.MissingExchangeCommands);
    }

    [Fact]
    public async Task Exchange_sign_in_to_a_different_tenant_is_rejected()
    {
        var env = new TestEnvironment();
        env.Exchange.Connection = Fixtures.Connection(Fixtures.OtherTenantId);
        await env.Connections.ConnectGraphAsync(CancellationToken.None);
        var ex = await Assert.ThrowsAsync<StudioOperationException>(() => env.Connections.ConnectExchangeAsync(CancellationToken.None));
        Assert.Equal(FailureKind.TenantMismatch, ex.Kind);
        Assert.False(env.Connections.Current.IsExchangeConnected);
        Assert.Equal(ServiceConnectionState.Failed, env.Connections.Current.Exchange);
    }

    [Fact]
    public async Task Graph_connection_never_implies_an_exchange_session()
    {
        var env = new TestEnvironment();
        await env.Connections.ConnectGraphAsync(CancellationToken.None);
        Assert.True(env.Connections.Current.IsGraphConnected);
        Assert.False(env.Connections.Current.IsExchangeConnected);
        Assert.Throws<StudioOperationException>(() => env.Connections.RequireExchange());
    }

    [Fact]
    public async Task Session_problem_marks_exchange_expired()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Exchange.RaiseSessionProblem(new StudioOperationException(FailureKind.SessionExpired, "Token expired."));
        Assert.Equal(ServiceConnectionState.Expired, env.Connections.Current.Exchange);
        var ex = Assert.Throws<StudioOperationException>(() => env.Connections.RequireExchange());
        Assert.Contains("expired", ex.Message);
    }

    [Fact]
    public async Task Health_check_detects_expired_tokens()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Exchange.Connection = env.Exchange.Connection! with { TokenStatus = "Expired" };
        var snapshot = await env.Connections.RefreshHealthAsync(CancellationToken.None);
        Assert.Equal(ServiceConnectionState.Expired, snapshot.Exchange);
    }

    [Fact]
    public async Task Missing_cmdlets_are_reported_after_connecting()
    {
        var env = new TestEnvironment();
        env.Exchange.UnavailableCommands.Add("New-ServicePrincipal");
        await env.ConnectAsync();
        Assert.Contains("New-ServicePrincipal", env.Connections.Current.MissingExchangeCommands);
        Assert.False(env.Connections.Current.CanConfigure);
    }

    [Fact]
    public async Task Disconnect_creates_a_new_session_and_drops_tenant_data()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var inventory = await env.Inventory.RefreshAsync(true, null, CancellationToken.None);
        var before = env.Connections.Current.SessionId;
        await env.Connections.DisconnectAsync();
        Assert.NotEqual(before, env.Connections.Current.SessionId);
        Assert.Null(env.Connections.Current.TenantId);
        Assert.Null(env.Inventory.Current);
        Assert.NotNull(inventory);
    }

    [Fact]
    public async Task Sign_in_requires_an_explicitly_chosen_client()
    {
        var env = new TestEnvironment();
        await env.Settings.SaveAsync(new StudioSettings());
        var ex = await Assert.ThrowsAsync<StudioOperationException>(() => env.Connections.ConnectGraphAsync(CancellationToken.None));
        Assert.Equal(FailureKind.InvalidInput, ex.Kind);
    }
}

public sealed class AuthorizationTestServiceTests
{
    private static async Task<TestEnvironment> ConfiguredAsync()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var request = env.Request(roles: ["Application Mail.Read"]);
        var (_, plan) = await env.PrepareAsync(request);
        await env.ExecuteAsync(plan, request);
        return env;
    }

    [Fact]
    public async Task Positive_and_negative_tests_pass_for_a_correct_configuration()
    {
        var env = await ConfiguredAsync();
        var report = await env.Tests.RunAsync(new AuthorizationTestRequest(Fixtures.PoloObjectId, "Polo", [Fixtures.Alice, Fixtures.FinanceShared], [Fixtures.Bob],
            ["Application Mail.Read"], MailboxCapability.None, EntraGrantsVerified: true), null, CancellationToken.None);
        Assert.Equal(AuthorizationVerdict.Pass, report.OverallVerdict);
        Assert.Equal(3, report.Passed);
        Assert.Contains(env.Audit.Entries, e => e.Action == "Test-ServicePrincipalAuthorization");
    }

    [Fact]
    public async Task Unverified_entra_grants_make_the_overall_result_inconclusive()
    {
        var env = await ConfiguredAsync();
        var report = await env.Tests.RunAsync(new AuthorizationTestRequest(Fixtures.PoloObjectId, "Polo", [Fixtures.Alice], [Fixtures.Bob],
            ["Application Mail.Read"], MailboxCapability.None, EntraGrantsVerified: false), null, CancellationToken.None);
        Assert.Equal(AuthorizationVerdict.Inconclusive, report.OverallVerdict);
    }

    [Fact]
    public async Task Tenant_wide_entra_grant_fails_negative_tests()
    {
        var env = await ConfiguredAsync();
        var report = await env.Tests.RunAsync(new AuthorizationTestRequest(Fixtures.PoloObjectId, "Polo", [Fixtures.Alice], [Fixtures.Bob],
            ["Application Mail.Read"], MailboxCapability.MailRead, EntraGrantsVerified: true), null, CancellationToken.None);
        Assert.Equal(AuthorizationVerdict.Fail, report.OverallVerdict);
    }

    [Fact]
    public async Task A_mailbox_cannot_be_both_positive_and_negative()
    {
        var env = await ConfiguredAsync();
        await Assert.ThrowsAsync<StudioOperationException>(() => env.Tests.RunAsync(new AuthorizationTestRequest(Fixtures.PoloObjectId, "Polo", [Fixtures.Alice], [Fixtures.Alice],
            ["Application Mail.Read"], MailboxCapability.None, true), null, CancellationToken.None));
    }
}

public sealed class ReportBuilderTests
{
    [Fact]
    public async Task Verdict_is_secure_only_after_execution_and_passing_tests()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var request = env.Request();
        var (outcome, plan) = await env.PrepareAsync(request);

        ReportInput Input(ExecutionResult? execution, AuthorizationTestReport? tests) => new(env.Connections.Current, env.Runtime, "Report", plan, execution,
            outcome.Report, request, outcome.Context.ScopeVerification, outcome.Context.GrantAnalysis, tests);

        var before = ReportBuilder.Build(Input(null, null), env.Clock.GetUtcNow());
        Assert.False(before.SecurityVerdict.SecurelyScoped);

        var execution = await env.ExecuteAsync(plan, request);
        var untested = ReportBuilder.Build(Input(execution, null), env.Clock.GetUtcNow());
        Assert.False(untested.SecurityVerdict.SecurelyScoped);
        Assert.Contains(untested.SecurityVerdict.Reasons, r => r.Contains("have not been run"));

        var tests = await env.Tests.RunAsync(new AuthorizationTestRequest(Fixtures.PoloObjectId, "Polo", request.Mailboxes, [Fixtures.Bob], request.Roles,
            MailboxCapability.None, true), null, CancellationToken.None);
        var report = ReportBuilder.Build(Input(execution, tests), env.Clock.GetUtcNow());
        Assert.True(report.SecurityVerdict.SecurelyScoped, string.Join("; ", report.SecurityVerdict.Reasons));
        Assert.Equal(Fixtures.TenantId, report.Tenant.TenantId);
        Assert.Equal(Fixtures.AdminUpn, report.Administrator.UserPrincipalName);
        Assert.Equal(2, report.Scope!.IntendedMailboxes.Count);
        Assert.NotEmpty(report.ExampleGraphRequests);
        Assert.Contains(report.RemainingVerification, r => r.Contains("2 hours"));
        Assert.Contains("New-ManagementScope", report.PowerShellEquivalent);
    }

    [Fact]
    public async Task Undermining_entra_grant_is_never_reported_as_securely_scoped()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Entra.Grants[Fixtures.PoloObjectId] = [Fixtures.GraphGrant("Mail.Read")];
        var request = env.Request();
        var (outcome, plan) = await env.PrepareAsync(request);
        var execution = await env.ExecuteAsync(plan, request);
        var tests = await env.Tests.RunAsync(new AuthorizationTestRequest(Fixtures.PoloObjectId, "Polo", request.Mailboxes, [], request.Roles,
            outcome.Context.GrantAnalysis!.TenantWideCapabilities, true), null, CancellationToken.None);
        var report = ReportBuilder.Build(new ReportInput(env.Connections.Current, env.Runtime, "Report", plan, execution, outcome.Report, request,
            outcome.Context.ScopeVerification, outcome.Context.GrantAnalysis, tests), env.Clock.GetUtcNow());

        Assert.False(report.SecurityVerdict.SecurelyScoped);
        Assert.Contains(report.RemainingVerification, r => r.Contains("Mail.Read"));
        Assert.Contains(report.SecurityFindings, f => f.Code == "ENTRA_TENANT_WIDE_OVERLAP");
    }

    [Fact]
    public void Example_requests_use_the_cloud_specific_graph_endpoint()
    {
        var requests = GraphRequestExamples.For(["Application Mail.Send"], "https://graph.microsoft.us/v1.0");
        var send = Assert.Single(requests);
        Assert.StartsWith("https://graph.microsoft.us/v1.0/users/", send.Url);
        Assert.Equal("POST", send.Method);
    }
}

public sealed class RedactionAndValidationTests
{
    [Theory]
    [InlineData("Authorization: Bearer eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.eyJhdWQiOiJodHRwczovL2dyYXBoIn0.c2lnbmF0dXJlc2lnbmF0dXJl")]
    [InlineData("token=eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.eyJhdWQiOiJodHRwczovL2dyYXBoIn0.c2lnbmF0dXJlc2lnbmF0dXJl")]
    [InlineData("client_secret=Abc~123.secretvalue&grant_type=client_credentials")]
    [InlineData("{\"access_token\":\"opaque-token-value-123\"}")]
    [InlineData("password=P@ssw0rd!")]
    public void Secrets_and_tokens_are_redacted(string input)
    {
        var output = SensitiveDataRedactor.Redact(input);
        Assert.Contains(SensitiveDataRedactor.Placeholder, output);
        Assert.DoesNotContain("c2lnbmF0dXJl", output);
        Assert.DoesNotContain("secretvalue", output);
        Assert.DoesNotContain("opaque-token-value", output);
        Assert.DoesNotContain("P@ssw0rd", output);
    }

    [Fact]
    public void Redaction_keeps_json_documents_valid()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            note = "Authorization: Bearer eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.eyJhdWQiOiJodHRwczovL2dyYXBoIn0.c2lnbmF0dXJlc2lnbmF0dXJl",
            items = new[] { "Authorization: Basic dXNlcjpwYXNz", "client_secret=abc&x=1" },
        });
        var redacted = SensitiveDataRedactor.Redact(json);
        using var document = System.Text.Json.JsonDocument.Parse(redacted);
        Assert.DoesNotContain("eyJ0eXAi", redacted);
        Assert.DoesNotContain("dXNlcjpwYXNz", redacted);
        Assert.Equal(2, document.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public void Ordinary_diagnostics_are_preserved()
    {
        const string message = "New-ManagementScope failed with HTTP status code=403 for scope SCOPE_X.";
        Assert.Equal(message, SensitiveDataRedactor.Redact(message));
    }

    [Theory]
    [InlineData("alice@contoso.com", true)]
    [InlineData("6233fba6-0198-4277-892f-9275bf728bcc", true)]
    [InlineData("alice", true)]
    [InlineData("al*ce", false)]
    [InlineData("alice@contoso", false)]
    [InlineData("a\u0000b", false)]
    [InlineData("", false)]
    public void Mailbox_identities_are_validated(string input, bool valid) =>
        Assert.Equal(valid, InputValidation.TryNormalizeMailboxIdentity(input, out _, out _));

    [Fact]
    public void Settings_validation_rejects_unsafe_values()
    {
        var settings = new StudioSettings
        {
            GraphClient = GraphClientMode.Custom,
            CustomGraphClientId = "not-a-guid",
            TenantHint = "contoso; drop",
            MaxMailboxesPerScope = 0,
            ScopeNameTemplate = "SCOPE",
            AssignmentNameTemplate = "RA",
            PowerShellPath = @"C:\Windows\System32\cmd.exe",
        };
        Assert.Equal(6, settings.Validate().Count);
        Assert.Empty(new StudioSettings().Validate());
    }

    [Fact]
    public void User_facing_errors_keep_sanitized_technical_details()
    {
        var error = UserFacingError.From(new ExchangeOperationException(FailureKind.AccessDenied, "Access denied.", "New-ManagementScope", "ERR_X",
            "Bearer eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9.eyJhdWQiOiJodHRwczovL2dyYXBoIn0.c2lnbmF0dXJlc2lnbmF0dXJl"));
        Assert.Equal("Insufficient permissions", error.Title);
        Assert.Contains("New-ManagementScope", error.TechnicalDetails);
        Assert.DoesNotContain("eyJ0eXAi", error.TechnicalDetails);
        Assert.NotNull(error.Remediation);
    }
}
