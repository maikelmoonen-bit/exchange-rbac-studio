using ExchangeRbacStudio.Application.Applications;
using ExchangeRbacStudio.Application.Configuration;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Scoping;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Scoping;
using ExchangeRbacStudio.Domain.Validation;

namespace ExchangeRbacStudio.Tests.Application;

public sealed class ServicePrincipalIdentityValidationTests
{
    [Fact]
    public async Task Valid_service_principal_without_pointer_can_be_configured()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var details = await env.Identity.ValidateAsync(Fixtures.Polo, CancellationToken.None);
        Assert.True(details.IsValid);
        Assert.Equal(ExchangePointerStatus.Missing, details.PointerStatus);
    }

    [Fact]
    public async Task Existing_matching_pointer_is_reused()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Exchange.SeedPointer(Fixtures.Polo);
        var details = await env.Identity.ValidateAsync(Fixtures.Polo, CancellationToken.None);
        Assert.Equal(ExchangePointerStatus.Present, details.PointerStatus);
        Assert.True(details.IsValid);
    }

    [Fact]
    public async Task Pointer_with_mismatching_object_id_blocks_configuration()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Exchange.ServicePrincipals.Add(new ExchangeServicePrincipal("stale", "stale", "Polo (old)", Fixtures.PoloAppId, Guid.NewGuid()));
        var details = await env.Identity.ValidateAsync(Fixtures.Polo, CancellationToken.None);
        Assert.Equal(ExchangePointerStatus.Mismatch, details.PointerStatus);
        Assert.False(details.IsValid);
    }

    [Fact]
    public async Task Deleted_service_principal_stops_configuration()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Entra.ServicePrincipals.Clear();
        var details = await env.Identity.ValidateAsync(Fixtures.Polo, CancellationToken.None);
        Assert.False(details.IsValid);
        Assert.Contains("no longer exists", details.BlockingError);
    }

    [Fact]
    public async Task Conflicting_app_id_is_detected()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var details = await env.Identity.ValidateAsync(Fixtures.Polo with { AppId = Guid.NewGuid() }, CancellationToken.None);
        Assert.False(details.IsValid);
        Assert.Contains("Identifier conflict", details.BlockingError);
    }

    [Fact]
    public async Task App_registration_object_id_is_never_confused_with_the_service_principal_object_id()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var results = await env.Identity.SearchAsync(Fixtures.PoloAppRegistrationObjectId.ToString(), CancellationToken.None);
        var result = Assert.Single(results);
        Assert.Equal(Fixtures.PoloObjectId, result.ServicePrincipal.ObjectId);
        Assert.Contains("App Registration", result.MatchNote);
        Assert.Contains(Fixtures.PoloObjectId.ToString(), result.MatchNote);
    }

    [Fact]
    public async Task Search_by_app_id_and_object_id()
    {
        var env = await new TestEnvironment().ConnectAsync();
        Assert.Single(await env.Identity.SearchAsync(Fixtures.PoloAppId.ToString(), CancellationToken.None));
        Assert.Single(await env.Identity.SearchAsync(Fixtures.PoloObjectId.ToString(), CancellationToken.None));
        Assert.Single(await env.Identity.SearchAsync("polo", CancellationToken.None));
    }

    [Fact]
    public async Task Searching_requires_a_graph_connection()
    {
        var env = new TestEnvironment();
        var ex = await Assert.ThrowsAsync<StudioOperationException>(() => env.Identity.SearchAsync("polo", CancellationToken.None));
        Assert.Equal(FailureKind.NotConnected, ex.Kind);
    }
}

public sealed class PreflightTests
{
    [Fact]
    public async Task Clean_tenant_passes_all_checks_and_is_securely_scoped()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var outcome = await env.Preflight.RunAsync(env.Request(), null, CancellationToken.None);
        Assert.True(outcome.Report.CanProceed(false), string.Join("; ", outcome.Report.Checks.Where(c => c.IsBlocking).Select(c => c.Summary)));
        Assert.True(outcome.Report.IsSecurelyScoped);
        Assert.Equal(ScopeVerificationStatus.Verified, outcome.Context.ScopeVerification!.Status);
        Assert.DoesNotContain(env.Exchange.ExecutedCommands, _ => true);
    }

    [Fact]
    public async Task Tenant_wide_entra_grant_blocks_secure_scoping_but_can_be_acknowledged()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Entra.Grants[Fixtures.PoloObjectId] = [Fixtures.GraphGrant("Mail.Read")];
        var outcome = await env.Preflight.RunAsync(env.Request(), null, CancellationToken.None);
        var check = outcome.Report.Checks.Single(c => c.Id == PreflightService.CheckIds.EntraGrants);
        Assert.Equal(CheckStatus.Blocked, check.Status);
        Assert.True(check.CanAcknowledge);
        Assert.False(outcome.Report.IsSecurelyScoped);
        Assert.False(outcome.Report.CanProceed(false));
        Assert.True(outcome.Report.CanProceed(true));
    }

    [Fact]
    public async Task Unreadable_entra_grants_are_unverifiable_and_block_execution()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Entra.GrantsFailure = new GraphOperationException(FailureKind.AccessDenied, "Insufficient privileges.", 403, "Authorization_RequestDenied", null);
        var outcome = await env.Preflight.RunAsync(env.Request(), null, CancellationToken.None);
        var check = outcome.Report.Checks.Single(c => c.Id == PreflightService.CheckIds.EntraGrants);
        Assert.Equal(CheckStatus.UnableToVerify, check.Status);
        Assert.True(check.IsHardBlock);
        Assert.False(outcome.Report.CanProceed(true));
    }

    [Fact]
    public async Task Missing_exchange_privileges_block_with_pim_guidance()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Exchange.UnavailableCommands.Add("New-ManagementRoleAssignment");
        var outcome = await env.Preflight.RunAsync(env.Request(), null, CancellationToken.None);
        var check = outcome.Report.Checks.Single(c => c.Id == PreflightService.CheckIds.ExchangePrivileges);
        Assert.Equal(CheckStatus.Blocked, check.Status);
        Assert.Contains("Privileged Identity Management", check.Remediation);
    }

    [Fact]
    public async Task Overmatching_scope_is_rejected()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Exchange.PreviewOverride = filter => [.. env.Exchange.Evaluate(filter), Fixtures.Bob];
        var outcome = await env.Preflight.RunAsync(env.Request(), null, CancellationToken.None);
        var check = outcome.Report.Checks.Single(c => c.Id == PreflightService.CheckIds.ScopePrecision);
        Assert.Equal(CheckStatus.Blocked, check.Status);
        Assert.Contains("not selected", check.Summary);
        Assert.False(outcome.Report.CanProceed(true));
    }

    [Fact]
    public async Task Organization_wide_existing_assignment_is_detected()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var pointer = env.Exchange.SeedPointer(Fixtures.Polo);
        env.Exchange.SeedAssignment(pointer, "Application Mail.ReadWrite", scopeName: null);
        var outcome = await env.Preflight.RunAsync(env.Request(), null, CancellationToken.None);
        var check = outcome.Report.Checks.Single(c => c.Id == PreflightService.CheckIds.Overlap);
        Assert.Equal(CheckStatus.Blocked, check.Status);
        Assert.False(outcome.Report.IsSecurelyScoped);
    }

    [Fact]
    public async Task Deleted_mailbox_fails_revalidation()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var request = env.Request();
        env.Exchange.Recipients.RemoveAll(r => r.IdentityKey == Fixtures.FinanceShared.IdentityKey);
        var outcome = await env.Preflight.RunAsync(request, null, CancellationToken.None);
        Assert.Equal(CheckStatus.Blocked, outcome.Report.Checks.Single(c => c.Id == PreflightService.CheckIds.Mailboxes).Status);
    }

    [Fact]
    public async Task Exchange_session_bound_to_another_tenant_is_blocked()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Exchange.Connection = Fixtures.Connection(Fixtures.OtherTenantId);
        var outcome = await env.Preflight.RunAsync(env.Request(), null, CancellationToken.None);
        Assert.Equal(CheckStatus.Blocked, outcome.Report.Checks.Single(c => c.Id == PreflightService.CheckIds.ExchangeConnection).Status);
    }

    [Fact]
    public async Task Name_collision_with_a_different_filter_is_a_conflict()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Exchange.SeedScope("SCOPE_APP_POLO_TEST_AUTOMATION_MAIL", "CustomAttribute1 -eq 'x'");
        var outcome = await env.Preflight.RunAsync(env.Request(), null, CancellationToken.None);
        Assert.Equal(CheckStatus.Blocked, outcome.Report.Checks.Single(c => c.Id == PreflightService.CheckIds.ScopeName).Status);
        Assert.Equal(CheckStatus.Blocked, outcome.Report.Checks.Single(c => c.Id == PreflightService.CheckIds.Plan).Status);
    }

    [Fact]
    public async Task Preflight_without_connection_is_blocked_immediately()
    {
        var env = new TestEnvironment();
        var outcome = await env.Preflight.RunAsync(env.Request(), null, CancellationToken.None);
        Assert.Single(outcome.Report.Checks);
        Assert.True(outcome.Report.HasHardBlock);
    }
}

public sealed class ScopeDesignerTests
{
    [Fact]
    public async Task Verified_when_exchange_resolves_exactly_the_selection()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var filter = RecipientFilterBuilder.Build([Fixtures.Alice], RecipientFilterStrategy.ObjectGuid).Filter!;
        var verification = await env.ScopeDesigner.VerifyAsync(filter, [Fixtures.Alice], CancellationToken.None);
        Assert.True(verification.IsVerified);
    }

    [Fact]
    public async Task Preview_failure_is_unverifiable_never_verified()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Exchange.ReadFailures["PreviewRecipientFilterAsync"] = new StudioOperationException(FailureKind.Throttled, "Server busy.");
        var filter = RecipientFilterBuilder.Build([Fixtures.Alice], RecipientFilterStrategy.ObjectGuid).Filter!;
        var verification = await env.ScopeDesigner.VerifyAsync(filter, [Fixtures.Alice], CancellationToken.None);
        Assert.Equal(ScopeVerificationStatus.Unverifiable, verification.Status);
    }

    [Fact]
    public async Task Filters_rejected_by_exchange_are_invalid()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Exchange.ReadFailures["PreviewRecipientFilterAsync"] = new StudioOperationException(FailureKind.InvalidInput, "Invalid filter syntax.");
        var filter = RecipientFilterBuilder.Build([Fixtures.Alice], RecipientFilterStrategy.ObjectGuid).Filter!;
        Assert.Equal(ScopeVerificationStatus.Invalid, (await env.ScopeDesigner.VerifyAsync(filter, [Fixtures.Alice], CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Broad_filters_are_refused_by_the_workflow()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var verification = await env.ScopeDesigner.VerifyAsync("Department -eq 'Finance'", [Fixtures.Alice], CancellationToken.None);
        Assert.Equal(ScopeVerificationStatus.Invalid, verification.Status);
    }

    [Fact]
    public async Task Truncated_preview_is_treated_as_overmatch()
    {
        var env = await new TestEnvironment().ConnectAsync();
        env.Exchange.PreviewOverride = _ => Enumerable.Repeat(Fixtures.Alice, 100).ToList();
        var filter = RecipientFilterBuilder.Build([Fixtures.Alice], RecipientFilterStrategy.ObjectGuid).Filter!;
        Assert.Equal(ScopeVerificationStatus.Overmatch, (await env.ScopeDesigner.VerifyAsync(filter, [Fixtures.Alice], CancellationToken.None)).Status);
    }

    [Fact]
    public void Suggested_name_reuses_an_equivalent_scope_and_avoids_collisions()
    {
        var filter = RecipientFilterBuilder.Build([Fixtures.Alice], RecipientFilterStrategy.ObjectGuid).Filter!;
        var context = new NamingContext("Polo Test Automation", Fixtures.PoloAppId, ["Application Mail.Read"], 1, DateTimeOffset.UtcNow);
        var equivalent = new ManagementScope("EXISTING", "EXISTING", Guid.NewGuid(), filter, null, "RecipientScope", false, null);
        Assert.Equal("EXISTING", ScopeDesigner.SuggestName(ScopeNamingPolicy.DefaultScopeTemplate, context, filter, [equivalent]));

        var collision = equivalent with { Name = "SCOPE_APP_POLO_TEST_AUTOMATION_MAIL", RecipientFilter = "Department -eq 'x'" };
        Assert.Equal("SCOPE_APP_POLO_TEST_AUTOMATION_MAIL_2", ScopeDesigner.SuggestName(ScopeNamingPolicy.DefaultScopeTemplate, context, filter, [collision]));
    }
}

public sealed class MailboxValidationServiceTests
{
    [Fact]
    public async Task Search_returns_validation_for_each_candidate()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var results = await env.Mailboxes.SearchAsync("contoso", CancellationToken.None);
        Assert.Contains(results, r => r.Recipient.Alias == "alice" && r.Validation.IsSupported);
        Assert.Contains(results, r => r.Recipient.Alias == "sales" && !r.Validation.IsSupported);
    }

    [Fact]
    public async Task Pasted_lists_resolve_each_entry_and_report_problems()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var results = await env.Mailboxes.ResolveManyAsync("alice@contoso.com; sales@contoso.com\nnobody@contoso.com, not an address!", 10, CancellationToken.None);
        Assert.True(results.Single(r => r.Input == "alice@contoso.com").Candidate!.Validation.IsSupported);
        Assert.False(results.Single(r => r.Input == "sales@contoso.com").Candidate!.Validation.IsSupported);
        Assert.NotNull(results.Single(r => r.Input == "nobody@contoso.com").Error);
        Assert.NotNull(results.Single(r => r.Input == "not an address!").Error);
    }

    [Fact]
    public async Task Wildcards_are_rejected_at_the_trust_boundary()
    {
        var env = await new TestEnvironment().ConnectAsync();
        var ex = await Assert.ThrowsAsync<StudioOperationException>(() => env.Mailboxes.SearchAsync("*", CancellationToken.None));
        Assert.Equal(FailureKind.InvalidInput, ex.Kind);
    }
}
