using ExchangeRbacStudio.App.ViewModels;
using ExchangeRbacStudio.App.ViewModels.Dialogs;
using ExchangeRbacStudio.App.ViewModels.Pages;
using ExchangeRbacStudio.App.ViewModels.Shared;
using ExchangeRbacStudio.App.ViewModels.Wizard;
using ExchangeRbacStudio.Application.Configuration;
using ExchangeRbacStudio.Application.Execution;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Application.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace ExchangeRbacStudio.App.Tests;

[Collection("WPF")]
public sealed class WizardWorkflowTests
{
    /// <summary>Drives the real wizard view models end to end against the fake Microsoft 365 adapters.</summary>
    [Fact]
    public Task Wizard_enforces_each_gate_and_completes_a_verified_configuration() => WpfTestHost.Instance.RunAsync(async () =>
    {
        var (services, env) = WpfTestHost.BuildServices();
        await services.ConnectFakesAsync();
        var wizard = services.GetRequiredService<ConfigureAccessWizardViewModel>();
        await wizard.NavigatedToAsync(null);

        // Step 1: application.
        var app = Assert.IsType<ApplicationStepViewModel>(wizard.CurrentStep);
        Assert.False(wizard.NextCommand.CanExecute(null));
        app.SearchText = "Polo";
        await app.SearchCommand.ExecuteAsync(null);
        app.SelectedResult = Assert.Single(app.Results);
        await WpfTestHost.WaitUntilAsync(() => app.Details is not null && !app.IsBusy, "identity validation");
        Assert.True(app.CanAdvance);
        Assert.True(wizard.NextCommand.CanExecute(null));
        await wizard.NextCommand.ExecuteAsync(null);

        // Step 2: mailboxes, unsupported types cannot be added.
        var mailboxes = Assert.IsType<MailboxesStepViewModel>(wizard.CurrentStep);
        Assert.False(wizard.NextCommand.CanExecute(null));
        mailboxes.Picker.SearchText = "contoso";
        await mailboxes.Picker.SearchCommand.ExecuteAsync(null);
        var group = mailboxes.Picker.Results.Single(r => r.TypeLabel == "MailUniversalDistributionGroup");
        Assert.False(group.AddCommand.CanExecute(null));
        Assert.NotNull(group.Reason);
        mailboxes.Picker.Results.Single(r => r.Alias == "alice").AddCommand.Execute(null);
        mailboxes.Picker.Results.Single(r => r.Alias == "finance").AddCommand.Execute(null);
        Assert.Equal(2, mailboxes.Picker.SelectedCount);
        Assert.True(wizard.NextCommand.CanExecute(null));
        await wizard.NextCommand.ExecuteAsync(null);

        // Step 3: permissions with least-privilege advice.
        var permissions = Assert.IsType<PermissionsStepViewModel>(wizard.CurrentStep);
        await WpfTestHost.WaitUntilAsync(() => permissions.Groups.Count > 0, "roles discovered");
        Assert.False(permissions.CanAdvance);
        var all = permissions.Groups.SelectMany(g => g.Roles).ToList();
        all.Single(r => r.RoleName == "Application Mail.ReadWrite").IsSelected = true;
        Assert.Contains(permissions.Advice, a => a.Title.Contains("does not include Mail.Send"));
        all.Single(r => r.RoleName == "Application Mail.ReadWrite").IsSelected = false;
        all.Single(r => r.RoleName == "Application Mail.Read").IsSelected = true;
        Assert.True(wizard.NextCommand.CanExecute(null));
        await wizard.NextCommand.ExecuteAsync(null);

        // Step 4: validation.
        var validation = Assert.IsType<ValidationStepViewModel>(wizard.CurrentStep);
        await WpfTestHost.WaitUntilAsync(() => !validation.IsBusy && validation.Checks.Count > 5, "preflight");
        Assert.True(validation.CanAdvance, validation.Verdict);
        Assert.Equal(StatusKind.Success, validation.VerdictKind);
        await wizard.NextCommand.ExecuteAsync(null);

        // Step 5: scope.
        var scope = Assert.IsType<ScopeStepViewModel>(wizard.CurrentStep);
        Assert.Equal("SCOPE_APP_POLO_TEST_AUTOMATION_MAIL", scope.ScopeName);
        Assert.True(scope.IsVerified);
        Assert.Equal(2, scope.Matched.Count);
        scope.ScopeName = "bad 'name'";
        Assert.False(wizard.NextCommand.CanExecute(null));
        scope.ScopeName = "SCOPE_APP_POLO_TEST_AUTOMATION_MAIL";
        Assert.True(wizard.NextCommand.CanExecute(null));
        await wizard.NextCommand.ExecuteAsync(null);

        // Step 6: review requires explicit validation.
        var review = Assert.IsType<ReviewStepViewModel>(wizard.CurrentStep);
        Assert.False(wizard.NextCommand.CanExecute(null));
        Assert.Contains("New-ManagementScope", review.Script);
        await review.ValidateCommand.ExecuteAsync(null);
        Assert.True(review.IsValidated, review.ValidationMessage);
        Assert.Empty(env.Exchange.ExecutedCommands);
        await wizard.NextCommand.ExecuteAsync(null);

        // Step 7: explicit confirmation, then execution.
        var execute = Assert.IsType<ExecuteStepViewModel>(wizard.CurrentStep);
        Assert.Equal(Fixtures.TenantName, execute.TenantName);
        Assert.False(execute.ExecuteCommand.CanExecute(null));
        execute.IsConfirmed = true;
        Assert.True(execute.ExecuteCommand.CanExecute(null));
        await execute.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(StatusKind.Success, execute.OutcomeKind);
        Assert.Equal(3, env.Exchange.ExecutedCommands.Count);
        Assert.False(execute.CanGoBack);
        Assert.False(execute.ExecuteCommand.CanExecute(null));
        await wizard.NextCommand.ExecuteAsync(null);

        // Step 8: verification and report.
        var verify = Assert.IsType<VerifyStepViewModel>(wizard.CurrentStep);
        await WpfTestHost.WaitUntilAsync(() => !verify.IsBusy && verify.OverallLabel is not null, "authorization tests");
        Assert.Equal(StatusKind.Success, verify.OverallKind);
        Assert.Equal(2, verify.Results.Count);
        Assert.NotNull(verify.ReportPath);
        Assert.False(wizard.NextCommand.CanExecute(null));
    });

    [Fact]
    public Task Wizard_resets_when_the_tenant_session_changes() => WpfTestHost.Instance.RunAsync(async () =>
    {
        var (services, env) = WpfTestHost.BuildServices();
        await services.ConnectFakesAsync();
        var wizard = services.GetRequiredService<ConfigureAccessWizardViewModel>();
        await wizard.NavigatedToAsync(new WizardStartOptions(Fixtures.Polo));
        var app = Assert.IsType<ApplicationStepViewModel>(wizard.CurrentStep);
        await WpfTestHost.WaitUntilAsync(() => app.Details is not null && !app.IsBusy, "prefilled validation");

        await services.GetRequiredService<ConnectionManager>().DisconnectAsync();
        await WpfTestHost.WaitUntilAsync(() => wizard.ResetMessage is not null, "reset");
        Assert.Null(((ApplicationStepViewModel)wizard.CurrentStep!).Details);
    });
}

[Collection("WPF")]
public sealed class ComponentViewModelTests
{
    [Fact]
    public Task Change_review_requires_confirmation_and_typed_object_name() => WpfTestHost.Instance.RunAsync(async () =>
    {
        var (services, env) = WpfTestHost.BuildServices();
        await services.ConnectFakesAsync();
        env.Exchange.SeedScope("SCOPE_UNUSED", "Guid -eq 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa'");
        var prepared = await services.GetRequiredService<ManagementPlanService>().RemoveScopeAsync("SCOPE_UNUSED", CancellationToken.None);
        var review = services.GetRequiredService<ChangeReviewViewModel>();
        review.Initialize(new ChangeReviewRequest(prepared.Plan, prepared.Replan, "Remove scope", "test", "SCOPE_UNUSED"));

        Assert.False(review.ExecuteCommand.CanExecute(null));
        review.IsConfirmed = true;
        Assert.False(review.ExecuteCommand.CanExecute(null));
        review.ConfirmationInput = "SCOPE_WRONG";
        Assert.False(review.ExecuteCommand.CanExecute(null));
        review.ConfirmationInput = "SCOPE_UNUSED";
        Assert.True(review.ExecuteCommand.CanExecute(null));

        await review.ExecuteCommand.ExecuteAsync(null);
        Assert.Equal(ExecutionOutcome.Completed, review.Result!.Outcome);
        Assert.Empty(env.Exchange.Scopes);
        Assert.False(review.ExecuteCommand.CanExecute(null));
    });

    [Fact]
    public Task Plans_with_conflicts_can_never_be_executed() => WpfTestHost.Instance.RunAsync(async () =>
    {
        var (services, env) = WpfTestHost.BuildServices();
        await services.ConnectFakesAsync();
        var pointer = env.Exchange.SeedPointer(Fixtures.Polo);
        env.Exchange.SeedScope("SCOPE_X", "Guid -eq 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa'");
        env.Exchange.SeedAssignment(pointer, "Application Mail.Read", "SCOPE_X");
        var prepared = await services.GetRequiredService<ManagementPlanService>().RemoveScopeAsync("SCOPE_X", CancellationToken.None);
        var review = services.GetRequiredService<ChangeReviewViewModel>();
        review.Initialize(new ChangeReviewRequest(prepared.Plan, prepared.Replan, "Remove", "test", null));
        review.IsConfirmed = true;
        Assert.Single(review.Conflicts);
        Assert.False(review.ExecuteCommand.CanExecute(null));
    });

    [Fact]
    public Task Mailbox_picker_validates_pasted_entries_and_enforces_the_limit() => WpfTestHost.Instance.RunAsync(async () =>
    {
        var (services, _) = WpfTestHost.BuildServices();
        await services.ConnectFakesAsync();
        var picker = services.GetRequiredService<MailboxPickerViewModel>();
        picker.MaxItems = 1;
        picker.PasteText = "alice@contoso.com\nsales@contoso.com\nfinance@contoso.com";
        await picker.AddPastedCommand.ExecuteAsync(null);

        Assert.Equal(1, picker.SelectedCount);
        Assert.Contains(picker.ResolutionMessages, m => m.Kind == StatusKind.Success && m.Input == "alice@contoso.com");
        Assert.Contains(picker.ResolutionMessages, m => m.Kind == StatusKind.Critical && m.Input == "sales@contoso.com");
        Assert.NotNull(picker.Error);
    });

    [Fact]
    public Task Negative_test_targets_cannot_overlap_positive_targets() => WpfTestHost.Instance.RunAsync(async () =>
    {
        var (services, _) = WpfTestHost.BuildServices();
        await services.ConnectFakesAsync();
        var tester = services.GetRequiredService<AuthorizationTesterViewModel>();
        tester.PositivePicker.SearchText = "alice";
        await tester.PositivePicker.SearchCommand.ExecuteAsync(null);
        tester.PositivePicker.Results.Single().AddCommand.Execute(null);

        tester.NegativePicker.SearchText = "alice";
        await tester.NegativePicker.SearchCommand.ExecuteAsync(null);
        var alice = tester.NegativePicker.Results.Single();
        Assert.False(alice.IsSupported);
        Assert.Contains("positive", alice.Reason);
    });

    [Fact]
    public Task Settings_reject_invalid_values_and_persist_valid_ones() => WpfTestHost.Instance.RunAsync(async () =>
    {
        var (services, env) = WpfTestHost.BuildServices();
        var settings = services.GetRequiredService<SettingsViewModel>();
        settings.ClientMode = GraphClientMode.Custom;
        settings.CustomClientId = "not-a-guid";
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.NotEmpty(settings.ValidationErrors);
        Assert.Equal(GraphClientMode.MicrosoftGraphCommandLineTools, env.Settings.Current.GraphClient);

        settings.CustomClientId = Guid.NewGuid().ToString();
        settings.MaxMailboxes = 20;
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Empty(settings.ValidationErrors);
        Assert.Equal(GraphClientMode.Custom, env.Settings.Current.GraphClient);
        Assert.Equal(20, env.Settings.Current.MaxMailboxesPerScope);
        Assert.Contains("SCOPE_APP_SAMPLE_APP_MAIL", settings.NamePreview);
    });

    [Fact]
    public Task Dashboard_metrics_come_from_retrieved_data() => WpfTestHost.Instance.RunAsync(async () =>
    {
        var (services, env) = WpfTestHost.BuildServices();
        var pointer = env.Exchange.SeedPointer(Fixtures.Polo);
        env.Exchange.SeedScope("SCOPE_X", "Guid -eq 'aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa'");
        env.Exchange.SeedAssignment(pointer, "Application Mail.Read", "SCOPE_X");
        env.Exchange.SeedAssignment(pointer, "Application Mail.Send", null);
        await services.ConnectFakesAsync();

        var dashboard = services.GetRequiredService<DashboardViewModel>();
        await dashboard.NavigatedToAsync(null);
        await WpfTestHost.WaitUntilAsync(() => dashboard.Inventory is not null && !dashboard.IsBusy, "dashboard load");

        Assert.Equal("1", dashboard.ServicePrincipalCount);
        Assert.Equal("1", dashboard.ScopeCount);
        Assert.Equal("2", dashboard.AssignmentCount);
        Assert.Contains("1 organization-wide", dashboard.AssignmentDetail);
        Assert.NotEqual("0", dashboard.UnsafeCount);
        Assert.Contains(dashboard.Findings, f => f.Title.Contains("every mailbox"));
    });
}
