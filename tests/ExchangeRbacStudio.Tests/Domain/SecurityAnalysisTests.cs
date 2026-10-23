using ExchangeRbacStudio.Domain.Authorization;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Identity;
using ExchangeRbacStudio.Domain.Permissions;
using ExchangeRbacStudio.Domain.Security;

namespace ExchangeRbacStudio.Tests.Domain;

public sealed class OrganizationWideGrantDetectionTests
{
    private static readonly MailboxCapability MailRead = ApplicationRoleCatalog.CapabilitiesOf(["Application Mail.Read"]);

    [Fact]
    public void Tenant_wide_grant_of_the_same_permission_undermines_the_scope()
    {
        var analysis = EntraGrantAnalyzer.Analyze([Fixtures.GraphGrant("Mail.Read")], MailRead);
        Assert.True(analysis.UnderminesRequestedScope);
        var finding = Assert.Single(analysis.Findings, f => f.Code == "ENTRA_TENANT_WIDE_OVERLAP");
        Assert.True(finding.UnderminesScoping);
        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("never revokes", finding.Remediation);
    }

    [Fact]
    public void Broader_tenant_wide_grant_undermines_a_narrower_scoped_role()
    {
        var analysis = EntraGrantAnalyzer.Analyze([Fixtures.GraphGrant("Mail.ReadWrite")], MailRead);
        Assert.True(analysis.UnderminesRequestedScope);
    }

    [Fact]
    public void Narrower_tenant_wide_grant_still_undermines_part_of_the_requested_capabilities()
    {
        var analysis = EntraGrantAnalyzer.Analyze([Fixtures.GraphGrant("Mail.ReadBasic.All")], MailRead);
        Assert.True(analysis.UnderminesRequestedScope);
    }

    [Fact]
    public void Ews_full_access_undermines_every_mailbox_role()
    {
        var analysis = EntraGrantAnalyzer.Analyze([Fixtures.ExchangeGrant("full_access_as_app")], ApplicationRoleCatalog.CapabilitiesOf(["Application Calendars.Read"]));
        Assert.True(analysis.UnderminesRequestedScope);
    }

    [Fact]
    public void Unrelated_tenant_wide_mailbox_grant_is_reported_but_does_not_undermine()
    {
        var analysis = EntraGrantAnalyzer.Analyze([Fixtures.GraphGrant("Calendars.Read")], MailRead);
        Assert.False(analysis.UnderminesRequestedScope);
        Assert.Contains(analysis.Findings, f => f.Code == "ENTRA_TENANT_WIDE_OTHER" && f.Severity == FindingSeverity.High);
        Assert.True(analysis.TenantWideCapabilities.HasFlag(MailboxCapability.CalendarsRead));
    }

    [Fact]
    public void Privilege_escalation_permissions_are_critical()
    {
        var analysis = EntraGrantAnalyzer.Analyze([Fixtures.GraphGrant("AppRoleAssignment.ReadWrite.All")], MailRead);
        Assert.Contains(analysis.Findings, f => f.Code == "ENTRA_PRIVILEGE_ESCALATION" && f.UnderminesScoping);
    }

    [Fact]
    public void Protocol_permissions_needing_mailbox_permissions_are_informational()
    {
        var analysis = EntraGrantAnalyzer.Analyze([Fixtures.ExchangeGrant("IMAP.AccessAsApp")], MailRead);
        Assert.False(analysis.UnderminesRequestedScope);
        Assert.Contains(analysis.Findings, f => f.Severity == FindingSeverity.Info);
    }

    [Fact]
    public void Unresolved_grants_are_never_silently_ignored()
    {
        var unresolved = new EntraAppPermissionGrant(Guid.NewGuid(), Guid.NewGuid(), null, "Unknown API", Guid.NewGuid(), null, null, null);
        var analysis = EntraGrantAnalyzer.Analyze([unresolved], MailRead);
        Assert.Single(analysis.UnresolvedGrants);
        Assert.Contains(analysis.Findings, f => f.Code == "ENTRA_UNRESOLVED_GRANTS");
    }

    [Fact]
    public void No_grants_means_no_findings()
    {
        var analysis = EntraGrantAnalyzer.Analyze([], MailRead);
        Assert.Empty(analysis.Findings);
        Assert.False(analysis.UnderminesRequestedScope);
    }
}

public sealed class DuplicateAssignmentDetectionTests
{
    private static readonly ExchangeServicePrincipal Pointer = new(Fixtures.PoloObjectId.ToString(), Fixtures.PoloObjectId.ToString(), "Polo", Fixtures.PoloAppId, Fixtures.PoloObjectId);

    private static ManagementRoleAssignment Assignment(string role, string? scope, bool enabled = true) =>
        new($"{role}-{scope}", $"{role}-{scope}", role, Pointer.Name, Pointer.Name, "ServicePrincipal", scope, scope is null ? "Organization" : "CustomRecipientScope",
            null, null, null, enabled, null);

    [Fact]
    public void Same_role_on_intended_scope_is_a_duplicate_that_is_reused()
    {
        var analysis = RbacOverlapAnalyzer.Analyze([Assignment("Application Mail.Read", "SCOPE_X")], ["Application Mail.Read"], null, "SCOPE_X");
        Assert.Single(analysis.ExistingOnIntendedScope);
        Assert.Empty(analysis.Findings);
    }

    [Fact]
    public void Organization_wide_overlapping_role_undermines_the_scope()
    {
        var analysis = RbacOverlapAnalyzer.Analyze([Assignment("Application Mail.ReadWrite", null)], ["Application Mail.Read"], null, "SCOPE_X");
        Assert.True(analysis.UnderminesRequestedScope);
        Assert.Contains(analysis.Findings, f => f.Code == "RBAC_ORG_WIDE" && f.UnderminesScoping);
    }

    [Fact]
    public void Overlapping_role_on_another_scope_is_a_warning()
    {
        var analysis = RbacOverlapAnalyzer.Analyze([Assignment("Application Mail Full Access", "SCOPE_OTHER")], ["Application Mail.Read"], null, "SCOPE_X");
        Assert.False(analysis.UnderminesRequestedScope);
        var finding = Assert.Single(analysis.Findings);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Contains("broader", finding.Detail);
    }

    [Fact]
    public void Non_overlapping_roles_are_ignored()
    {
        var analysis = RbacOverlapAnalyzer.Analyze([Assignment("Application Calendars.Read", null)], ["Application Mail.Send"], null, "SCOPE_X");
        Assert.Empty(analysis.Findings);
    }

    [Fact]
    public void Disabled_org_wide_assignment_is_informational()
    {
        var analysis = RbacOverlapAnalyzer.Analyze([Assignment("Application Mail.Read", null, enabled: false)], ["Application Mail.Read"], null, "SCOPE_X");
        Assert.False(analysis.UnderminesRequestedScope);
        Assert.Contains(analysis.Findings, f => f.Severity == FindingSeverity.Info);
    }
}

public sealed class AuthorizationEvaluationTests
{
    private static AuthorizationTestRow Row(string role, InScopeResult inScope, string scope = "SCOPE_X", string scopeType = "CustomRecipientScope") =>
        new(role, role.Replace("Application ", string.Empty), scope, scopeType, inScope);

    [Fact]
    public void Positive_test_passes_when_the_role_is_in_scope()
    {
        var outcomes = AuthorizationEvaluator.Evaluate("alice", TestExpectation.InScope, ["Application Mail.Read"],
            [Row("Application Mail.Read", InScopeResult.InScope)], MailboxCapability.None);
        Assert.Equal(AuthorizationVerdict.Pass, Assert.Single(outcomes).Verdict);
    }

    [Fact]
    public void One_in_scope_role_does_not_establish_other_requested_roles()
    {
        var outcomes = AuthorizationEvaluator.Evaluate("alice", TestExpectation.InScope, ["Application Mail.Read", "Application Mail.Send"],
            [Row("Application Mail.Read", InScopeResult.InScope)], MailboxCapability.None);
        Assert.Equal(AuthorizationVerdict.Pass, outcomes.Single(o => o.RoleName == "Application Mail.Read").Verdict);
        var send = outcomes.Single(o => o.RoleName == "Application Mail.Send");
        Assert.Equal(AuthorizationVerdict.Fail, send.Verdict);
        Assert.False(send.RoleAssigned);
        Assert.Equal(AuthorizationVerdict.Fail, AuthorizationEvaluator.Summarize(outcomes));
    }

    [Fact]
    public void Positive_test_fails_when_the_mailbox_is_outside_every_scope_of_the_role()
    {
        var outcomes = AuthorizationEvaluator.Evaluate("bob", TestExpectation.InScope, ["Application Mail.Read"],
            [Row("Application Mail.Read", InScopeResult.NotInScope)], MailboxCapability.None);
        Assert.Equal(AuthorizationVerdict.Fail, outcomes[0].Verdict);
    }

    [Fact]
    public void Positive_test_with_not_run_result_is_inconclusive()
    {
        var outcomes = AuthorizationEvaluator.Evaluate("alice", TestExpectation.InScope, ["Application Mail.Read"],
            [Row("Application Mail.Read", InScopeResult.NotRun)], MailboxCapability.None);
        Assert.Equal(AuthorizationVerdict.Inconclusive, outcomes[0].Verdict);
    }

    [Fact]
    public void Negative_test_passes_when_out_of_scope_for_every_overlapping_role()
    {
        var outcomes = AuthorizationEvaluator.Evaluate("bob", TestExpectation.OutOfScope, ["Application Mail.Read"],
            [Row("Application Mail.Read", InScopeResult.NotInScope), Row("Application Calendars.Read", InScopeResult.InScope)], MailboxCapability.None);
        Assert.Equal(AuthorizationVerdict.Pass, outcomes[0].Verdict);
    }

    [Fact]
    public void Negative_test_fails_when_a_broader_role_is_in_scope()
    {
        var outcomes = AuthorizationEvaluator.Evaluate("bob", TestExpectation.OutOfScope, ["Application Mail.Read"],
            [Row("Application Mail.Read", InScopeResult.NotInScope), Row("Application Mail.ReadWrite", InScopeResult.InScope, "Organization", "Organization")], MailboxCapability.None);
        var outcome = Assert.Single(outcomes);
        Assert.Equal(AuthorizationVerdict.Fail, outcome.Verdict);
        Assert.Contains("Application Mail.ReadWrite", outcome.Explanation);
    }

    [Fact]
    public void Negative_test_fails_when_entra_grants_tenant_wide_access()
    {
        var outcomes = AuthorizationEvaluator.Evaluate("bob", TestExpectation.OutOfScope, ["Application Mail.Read"],
            [Row("Application Mail.Read", InScopeResult.NotInScope)], MailboxCapability.MailRead);
        var outcome = Assert.Single(outcomes);
        Assert.Equal(AuthorizationVerdict.Fail, outcome.Verdict);
        Assert.True(outcome.EntraTenantWideGrant);
    }

    [Fact]
    public void Negative_test_with_no_assigned_roles_passes()
    {
        var outcomes = AuthorizationEvaluator.Evaluate("bob", TestExpectation.OutOfScope, ["Application Mail.Read"], [], MailboxCapability.None);
        Assert.Equal(AuthorizationVerdict.Pass, outcomes[0].Verdict);
    }

    [Theory]
    [InlineData("True", InScopeResult.InScope)]
    [InlineData("False", InScopeResult.NotInScope)]
    [InlineData("Not Run", InScopeResult.NotRun)]
    [InlineData("", InScopeResult.Unknown)]
    [InlineData(null, InScopeResult.Unknown)]
    public void InScope_values_are_parsed_from_cmdlet_output(string? raw, InScopeResult expected) =>
        Assert.Equal(expected, AuthorizationTestRow.ParseInScope(raw));
}

public sealed class EffectiveAccessMatrixTests
{
    [Fact]
    public void Matrix_distinguishes_configured_observed_and_entra_access()
    {
        var pointer = new ExchangeServicePrincipal("p", "p", "Polo", Fixtures.PoloAppId, Fixtures.PoloObjectId);
        var assignment = new ManagementRoleAssignment("ra", "ra", "Application Mail.Read", "p", "p", "ServicePrincipal", "SCOPE_X", "CustomRecipientScope", null, null, null, true, null);
        var access = new AssignmentAccess(assignment, [Fixtures.Alice, Fixtures.FinanceShared]);
        var tested = new Dictionary<string, IReadOnlyList<AuthorizationTestRow>>
        {
            [Fixtures.Alice.Label] = [new AuthorizationTestRow("Application Mail.Read", "Mail.Read", "SCOPE_X", "CustomRecipientScope", InScopeResult.InScope)],
        };
        var entra = new Dictionary<string, MailboxCapability> { ["Calendars.Read"] = MailboxCapability.CalendarsRead };

        var matrix = EffectiveAccessMatrixBuilder.Build([access], tested, entra);

        Assert.Equal(["Calendars.Read", "Mail.Read"], matrix.Permissions);
        var alice = matrix.Rows.Single(r => r.Mailbox == Fixtures.Alice.Label);
        Assert.Contains(AccessSource.RbacObserved, alice.Cells.Single(c => c.Permission == "Mail.Read").Sources);
        var finance = matrix.Rows.Single(r => r.Mailbox == Fixtures.FinanceShared.Label);
        Assert.Contains(AccessSource.RbacConfigured, finance.Cells.Single(c => c.Permission == "Mail.Read").Sources);
        var others = matrix.Rows.Single(r => r.IsAllOtherMailboxes);
        Assert.False(others.Cells.Single(c => c.Permission == "Mail.Read").HasAccess);
        Assert.Contains(AccessSource.EntraTenantWide, others.Cells.Single(c => c.Permission == "Calendars.Read").Sources);
        Assert.NotNull(pointer);
    }

    [Fact]
    public void Organization_wide_assignment_reaches_all_other_mailboxes()
    {
        var assignment = new ManagementRoleAssignment("ra", "ra", "Application Mail.Send", "p", "p", "ServicePrincipal", null, "Organization", null, null, null, true, null);
        var matrix = EffectiveAccessMatrixBuilder.Build([new AssignmentAccess(assignment, null)], new Dictionary<string, IReadOnlyList<AuthorizationTestRow>>(), new Dictionary<string, MailboxCapability>());
        var others = matrix.Rows.Single(r => r.IsAllOtherMailboxes);
        Assert.Contains(AccessSource.RbacOrganizationWide, others.Cells.Single().Sources);
    }
}
