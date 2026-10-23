using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Planning;
using ExchangeRbacStudio.Domain.Scoping;
using ExchangeRbacStudio.Domain.Validation;

namespace ExchangeRbacStudio.Tests.Domain;

public sealed class ScopeFilterGenerationTests
{
    [Fact]
    public void Single_mailbox_filter_uses_the_immutable_entra_object_id()
    {
        var result = RecipientFilterBuilder.Build([Fixtures.Alice], RecipientFilterStrategy.ExternalDirectoryObjectId);
        Assert.True(result.Succeeded);
        Assert.Equal("ExternalDirectoryObjectId -eq 'e1e1e1e1-0001-4001-8001-000000000001'", result.Filter);
    }

    [Fact]
    public void Multiple_mailboxes_produce_a_sorted_parenthesized_disjunction()
    {
        var result = RecipientFilterBuilder.Build([Fixtures.FinanceShared, Fixtures.Alice], RecipientFilterStrategy.ObjectGuid);
        Assert.Equal("(Guid -eq 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa') -or (Guid -eq 'bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb')", result.Filter);
    }

    [Fact]
    public void Generated_filters_never_use_PrimarySmtpAddress()
    {
        foreach (var strategy in Enum.GetValues<RecipientFilterStrategy>())
        {
            var result = RecipientFilterBuilder.Build([Fixtures.Alice, Fixtures.FinanceShared], strategy);
            Assert.DoesNotContain("PrimarySmtpAddress", result.Filter);
            Assert.DoesNotContain("@", result.Filter);
        }
    }

    [Fact]
    public void Unsupported_recipients_and_duplicates_are_rejected()
    {
        var result = RecipientFilterBuilder.Build([Fixtures.Alice, Fixtures.Alice, Fixtures.SalesDistributionGroup], RecipientFilterStrategy.ExternalDirectoryObjectId);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("more than once"));
        Assert.Contains(result.Errors, e => e.Contains("group", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_identifier_for_the_strategy_is_an_error()
    {
        var result = RecipientFilterBuilder.Build([Fixtures.Alice with { ExternalDirectoryObjectId = null }], RecipientFilterStrategy.ExternalDirectoryObjectId);
        Assert.False(result.Succeeded);
        Assert.Contains("ExternalDirectoryObjectId", Assert.Single(result.Errors));
    }

    [Fact]
    public void Empty_selection_is_an_error()
    {
        Assert.False(RecipientFilterBuilder.Build([], RecipientFilterStrategy.ObjectGuid).Succeeded);
    }

    [Fact]
    public void Generated_filters_are_recognized_as_exact_identity_filters()
    {
        var filter = RecipientFilterBuilder.Build([Fixtures.Alice, Fixtures.FinanceShared], RecipientFilterStrategy.ExchangeGuid).Filter;
        var analysis = RecipientFilterAnalyzer.Analyze(filter);
        Assert.True(analysis.IsExactIdentityFilter);
        Assert.Equal(FilterRisk.ExactIdentity, analysis.OverallRisk);
        Assert.Equal(2, analysis.ExactIdentities.Count);
    }

    [Theory]
    [InlineData("CustomAttribute1 -eq 'finance'", "MUTABLE_ATTRIBUTE")]
    [InlineData("Alias -ne 'mbxa'", "NEGATION")]
    [InlineData("DisplayName -like 'Fin*'", "WILDCARD")]
    [InlineData("RecipientTypeDetails -eq 'UserMailbox'", "TYPE_ONLY")]
    [InlineData("MemberOfGroup -eq 'CN=Finance,OU=contoso.onmicrosoft.com,DC=prod'", "GROUP_MEMBERSHIP")]
    [InlineData("PrimarySmtpAddress -eq 'alice@contoso.com'", "ADDRESS_MATCH")]
    [InlineData("-not (Department -eq 'HR')", "NEGATION")]
    [InlineData("Department -eq $null", "NULL_COMPARISON")]
    public void Potentially_broad_filters_are_flagged(string filter, string expectedFlag)
    {
        var analysis = RecipientFilterAnalyzer.Analyze(filter);
        Assert.True(analysis.IsParsed, analysis.ParseError);
        Assert.False(analysis.IsExactIdentityFilter);
        Assert.Contains(analysis.Flags, f => f.Code == expectedFlag);
    }

    [Fact]
    public void Mixed_identity_and_attribute_conditions_are_not_exact()
    {
        var analysis = RecipientFilterAnalyzer.Analyze("(Guid -eq 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa') -or (Department -eq 'Finance')");
        Assert.False(analysis.IsExactIdentityFilter);
    }

    [Fact]
    public void Unparseable_filters_are_unknown_risk()
    {
        var analysis = RecipientFilterAnalyzer.Analyze("Guid -eq 'unterminated");
        Assert.False(analysis.IsParsed);
        Assert.Equal(FilterRisk.Unknown, analysis.OverallRisk);
    }

    [Fact]
    public void Equivalent_exact_filters_compare_by_identity_set_not_text()
    {
        var a = "(Guid -eq 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa') -or (Guid -eq 'bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb')";
        var b = "Guid -eq 'BBBBBBBB-2222-4222-8222-BBBBBBBBBBBB' -or Guid -eq 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa'";
        Assert.True(RecipientFilterAnalyzer.AreEquivalentExactFilters(a, b));
        Assert.False(RecipientFilterAnalyzer.AreEquivalentExactFilters(a, "Guid -eq 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa'"));
    }
}

public sealed class ScopeNamingTests
{
    private static readonly NamingContext Context = new("Polo Test Automation", Fixtures.PoloAppId, ["Application Mail.Read", "Application Mail.Send"], 2,
        new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Default_template_produces_the_documented_example()
    {
        Assert.Equal("SCOPE_APP_POLO_TEST_AUTOMATION_MAIL", ScopeNamingPolicy.RenderScopeName(ScopeNamingPolicy.DefaultScopeTemplate, Context));
    }

    [Fact]
    public void Mixed_categories_render_as_multi_and_tokens_are_supported()
    {
        var context = Context with { RoleNames = ["Application Mail.Read", "Application Calendars.Read"] };
        Assert.Equal("SCOPE_71487ACD_MULTI_2_20261023", ScopeNamingPolicy.RenderScopeName("SCOPE_{APPID8}_{CATEGORY}_{COUNT}_{DATE}", context));
        Assert.Equal("RA_POLO_TEST_AUTOMATION_MAIL_SEND", ScopeNamingPolicy.RenderAssignmentName(ScopeNamingPolicy.DefaultAssignmentTemplate, Context, "Application Mail.Send"));
    }

    [Fact]
    public void Names_are_sanitized_and_limited_to_64_characters()
    {
        var context = Context with { ApplicationDisplayName = "Ünïcödé app; DROP 'scope' \"x\" " + new string('x', 120) };
        var name = ScopeNamingPolicy.RenderScopeName(ScopeNamingPolicy.DefaultScopeTemplate, context);
        Assert.True(name.Length <= 64);
        Assert.Matches("^[A-Za-z0-9_.-]+$", name);
        Assert.StartsWith("SCOPE_APP_UNICODE_APP_DROP_SCOPE_X", name);
    }

    [Fact]
    public void Unique_names_append_a_suffix_within_the_length_limit()
    {
        var existing = new[] { "SCOPE_A", "SCOPE_A_2" };
        Assert.Equal("SCOPE_A_3", ScopeNamingPolicy.EnsureUnique("SCOPE_A", existing));
        var longName = new string('B', 64);
        var unique = ScopeNamingPolicy.EnsureUnique(longName, [longName]);
        Assert.Equal(64, unique.Length);
        Assert.EndsWith("_2", unique);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("Valid_Name-1.0", true)]
    [InlineData("Has 'quote'", false)]
    [InlineData("Wild*card", false)]
    [InlineData(" leading", false)]
    public void Administrator_supplied_names_are_validated(string name, bool expected) =>
        Assert.Equal(expected, ScopeNamingPolicy.IsValidName(name, out _));
}

public sealed class ScopeMatchingTests
{
    [Fact]
    public void Exact_match()
    {
        var comparison = ScopeMembershipComparison.Compare([Fixtures.Alice, Fixtures.FinanceShared], [Fixtures.FinanceShared, Fixtures.Alice]);
        Assert.True(comparison.IsExact);
    }

    [Fact]
    public void Additional_recipients_are_an_overmatch()
    {
        var comparison = ScopeMembershipComparison.Compare([Fixtures.Alice], [Fixtures.Alice, Fixtures.Bob]);
        Assert.Equal(ScopeMatchVerdict.Overmatch, comparison.Verdict);
        Assert.Equal(Fixtures.Bob.Label, Assert.Single(comparison.Unexpected).Label);
    }

    [Fact]
    public void Missing_recipients_are_an_undermatch()
    {
        var comparison = ScopeMembershipComparison.Compare([Fixtures.Alice, Fixtures.FinanceShared], [Fixtures.Alice]);
        Assert.Equal(ScopeMatchVerdict.Undermatch, comparison.Verdict);
    }

    [Fact]
    public void Both_directions_are_a_mismatch()
    {
        var comparison = ScopeMembershipComparison.Compare([Fixtures.Alice], [Fixtures.Bob]);
        Assert.Equal(ScopeMatchVerdict.Mismatch, comparison.Verdict);
    }
}

public sealed class ExchangeCommandTests
{
    [Fact]
    public void Preview_renders_typed_parameters_in_order()
    {
        var command = ExchangeCommands.CreateRoleAssignment("RA_POLO_MAIL_READ", Fixtures.PoloObjectId, "Application Mail.Read", "SCOPE_X");
        Assert.Equal("New-ManagementRoleAssignment -Name 'RA_POLO_MAIL_READ' -App '6233fba6-0198-4277-892f-9275bf728bcc' -Role 'Application Mail.Read' -CustomResourceScope 'SCOPE_X'",
            command.ToPreview());
    }

    [Fact]
    public void Filters_are_rendered_readably_and_removals_are_explicit_about_confirmation()
    {
        Assert.Equal("New-ManagementScope -Name 'S' -RecipientRestrictionFilter \"Guid -eq 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa'\"",
            ExchangeCommands.CreateManagementScope("S", "Guid -eq 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa'").ToPreview());
        Assert.Equal("Remove-ManagementScope -Identity 'S' -Confirm:$false", ExchangeCommands.DeleteManagementScope("S").ToPreview());
    }

    [Theory]
    [InlineData("O'Brien", "\"O'Brien\"")]
    [InlineData("a $b `c", "'a $b `c'")]
    [InlineData("Mix 'q' and \"d\"", "'Mix ''q'' and \"d\"'")]
    public void Quoting_never_allows_expansion(string value, string expected) => Assert.Equal(expected, PowerShellQuoting.Quote(value));

    [Fact]
    public void Only_allowlisted_write_commands_can_be_constructed()
    {
        Assert.Throws<ArgumentException>(() => new ExchangeCommand("Invoke-Expression", []));
        Assert.Throws<ArgumentException>(() => new ExchangeCommand("Set-Mailbox", []));
    }

    [Fact]
    public void Plan_fingerprint_changes_when_a_command_changes()
    {
        var subject = new OperationSubject();
        ChangePlan Plan(string scope) => new(Guid.NewGuid(), ChangePlanKind.ConfigureAccess, "t", Fixtures.TenantId, "Contoso", DateTimeOffset.UtcNow,
            [new PlannedOperation(1, OperationKind.CreateManagementScope, OperationDisposition.Create, scope, "s", ExchangeCommands.CreateManagementScope(scope, "Guid -eq 'x'"), subject)], []);

        Assert.Equal(Plan("A").Fingerprint, Plan("A").Fingerprint);
        Assert.NotEqual(Plan("A").Fingerprint, Plan("B").Fingerprint);
    }

    [Fact]
    public void Plan_script_lists_reused_objects_and_commands_in_execution_order()
    {
        var plan = new ChangePlan(Guid.NewGuid(), ChangePlanKind.ConfigureAccess, "Configure", Fixtures.TenantId, "Contoso", DateTimeOffset.UtcNow,
        [
            new PlannedOperation(1, OperationKind.CreateServicePrincipal, OperationDisposition.Reuse, "Polo", "Reuse pointer", null, new OperationSubject()),
            new PlannedOperation(2, OperationKind.CreateManagementScope, OperationDisposition.Create, "S", "Create scope", ExchangeCommands.CreateManagementScope("S", "Guid -eq 'x'"), new OperationSubject()),
        ], []);
        var script = plan.ToPowerShellScript();
        Assert.Contains(Fixtures.TenantId.ToString(), script);
        Assert.Contains("(no change - existing object is reused)", script);
        Assert.True(script.IndexOf("Reuse pointer", StringComparison.Ordinal) < script.IndexOf("New-ManagementScope", StringComparison.Ordinal));
    }
}

public sealed class PreflightGatingTests
{
    [Fact]
    public void Hard_blocks_prevent_execution_even_when_acknowledged()
    {
        var report = new PreflightReport([new PreflightCheck("a", "A", CheckStatus.Blocked, "x")], DateTimeOffset.UtcNow);
        Assert.False(report.CanProceed(acknowledged: true));
    }

    [Fact]
    public void Unverifiable_security_critical_checks_are_blocking_and_never_silently_pass()
    {
        var check = new PreflightCheck("a", "A", CheckStatus.UnableToVerify, "x", IsSecurityCritical: true);
        var report = new PreflightReport([check], DateTimeOffset.UtcNow);
        Assert.True(check.IsHardBlock);
        Assert.False(report.CanProceed(true));
        Assert.False(report.IsSecurelyScoped);
    }

    [Fact]
    public void Acknowledgeable_blocks_require_explicit_acknowledgement_and_are_never_securely_scoped()
    {
        var report = new PreflightReport([new PreflightCheck("a", "A", CheckStatus.Blocked, "x", IsSecurityCritical: true, CanAcknowledge: true)], DateTimeOffset.UtcNow);
        Assert.False(report.CanProceed(false));
        Assert.True(report.CanProceed(true));
        Assert.False(report.IsSecurelyScoped);
    }

    [Fact]
    public void Warnings_do_not_block()
    {
        var report = new PreflightReport([new PreflightCheck("a", "A", CheckStatus.Warning, "x"), new PreflightCheck("b", "B", CheckStatus.Passed, "y", IsSecurityCritical: true)], DateTimeOffset.UtcNow);
        Assert.True(report.CanProceed(false));
        Assert.True(report.IsSecurelyScoped);
    }
}
