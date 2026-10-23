using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.App.ViewModels;
using ExchangeRbacStudio.App.ViewModels.Pages;
using ExchangeRbacStudio.App.ViewModels.Wizard;
using ExchangeRbacStudio.App.Views;
using ExchangeRbacStudio.Application.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace ExchangeRbacStudio.App.Tests;

/// <summary>
/// Renders the real views over test fixtures to PNG files for visual review and documentation.
/// Opt-in: set ERS_SNAPSHOT_DIR to an output folder. The images show fixture data ("Contoso"), never tenant data.
/// </summary>
[Collection("WPF")]
public sealed class SnapshotRenderer
{
    private static readonly string? OutputDirectory = Environment.GetEnvironmentVariable("ERS_SNAPSHOT_DIR");

    [Fact]
    public Task Render_page_snapshots() => WpfTestHost.Instance.RunAsync(async () =>
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(OutputDirectory), "Set ERS_SNAPSHOT_DIR to render snapshots.");
        Directory.CreateDirectory(OutputDirectory!);

        var (services, env) = WpfTestHost.BuildServices();
        SeedRealisticTenant(env);
        await services.ConnectFakesAsync();

        var window = services.GetRequiredService<MainWindow>();
        window.Width = 1440;
        window.Height = 920;
        window.Left = 0;
        window.Top = 0;
        window.Show();
        var shell = services.GetRequiredService<ShellViewModel>();
        var navigation = services.GetRequiredService<NavigationService>();
        var theme = services.GetRequiredService<ThemeService>();

        foreach (var (key, parameter, name) in new (PageKey, object?, string)[]
                 {
                     (PageKey.Dashboard, null, "dashboard"),
                     (PageKey.Applications, null, "applications"),
                     (PageKey.ApplicationDetail, new ApplicationDetailRequest(Fixtures.PoloObjectId, Fixtures.PoloAppId), "application-detail"),
                     (PageKey.Scopes, null, "scopes"),
                     (PageKey.RoleAssignments, null, "role-assignments"),
                     (PageKey.ChangeHistory, null, "change-history"),
                     (PageKey.Settings, null, "settings"),
                 })
        {
            navigation.Navigate(key, parameter);
            await WpfTestHost.WaitUntilAsync(() => shell.CurrentPage is { IsBusy: false }, name, 15_000);
            await Task.Delay(400);
            if (shell.CurrentPage is ScopesViewModel scopes) scopes.SelectedRow = scopes.Rows.FirstOrDefault(r => r.RiskKind == StatusKind.Critical) ?? scopes.Rows.FirstOrDefault();
            if (shell.CurrentPage is RoleAssignmentsViewModel assignments) assignments.SelectedRow = assignments.Rows.FirstOrDefault();
            if (shell.CurrentPage is ChangeHistoryViewModel history) history.SelectedItem = history.Items.FirstOrDefault();
            await Save(window, name);
        }

        await RenderWizardAsync(services, window, shell, navigation);

        theme.Apply(ThemePreference.Dark);
        navigation.Navigate(PageKey.Dashboard);
        await WpfTestHost.WaitUntilAsync(() => shell.CurrentPage is { IsBusy: false }, "dark dashboard");
        await Task.Delay(400);
        await Save(window, "dashboard-dark");
        navigation.Navigate(PageKey.ApplicationDetail, new ApplicationDetailRequest(Fixtures.PoloObjectId, Fixtures.PoloAppId));
        await WpfTestHost.WaitUntilAsync(() => shell.CurrentPage is { IsBusy: false }, "dark detail");
        await Task.Delay(400);
        await Save(window, "application-detail-dark");
        theme.Apply(ThemePreference.Light);

        window.Width = 1000;
        navigation.Navigate(PageKey.Applications);
        await Task.Delay(600);
        await Save(window, "applications-compact");
        window.Close();
    });

    private static async Task RenderWizardAsync(IServiceProvider services, Window window, ShellViewModel shell, NavigationService navigation)
    {
        navigation.Navigate(PageKey.ConfigureAccess);
        var wizard = (ConfigureAccessWizardViewModel)shell.CurrentPage!;
        await WpfTestHost.WaitUntilAsync(() => wizard.CurrentStep is not null, "wizard");

        var app = (ApplicationStepViewModel)wizard.CurrentStep!;
        app.SearchText = "Polo";
        await app.SearchCommand.ExecuteAsync(null);
        app.SelectedResult = app.Results.First();
        await WpfTestHost.WaitUntilAsync(() => app.Details is not null && !app.IsBusy, "app validation");
        await Save(window, "wizard-1-application");
        await wizard.NextCommand.ExecuteAsync(null);

        var mailboxes = (MailboxesStepViewModel)wizard.CurrentStep!;
        mailboxes.Picker.SearchText = "contoso";
        await mailboxes.Picker.SearchCommand.ExecuteAsync(null);
        mailboxes.Picker.Results.Single(r => r.Alias == "alice").AddCommand.Execute(null);
        mailboxes.Picker.Results.Single(r => r.Alias == "finance").AddCommand.Execute(null);
        await Save(window, "wizard-2-mailboxes");
        await wizard.NextCommand.ExecuteAsync(null);

        var permissions = (PermissionsStepViewModel)wizard.CurrentStep!;
        await WpfTestHost.WaitUntilAsync(() => permissions.Groups.Count > 0, "roles");
        var roles = permissions.Groups.SelectMany(g => g.Roles).ToList();
        roles.Single(r => r.RoleName == "Application Mail.ReadWrite").IsSelected = true;
        roles.Single(r => r.RoleName == "Application Mail.Send").IsSelected = true;
        await Save(window, "wizard-3-permissions");
        roles.Single(r => r.RoleName == "Application Mail.ReadWrite").IsSelected = false;
        roles.Single(r => r.RoleName == "Application Mail.Send").IsSelected = false;
        roles.Single(r => r.RoleName == "Application Calendars.Read").IsSelected = true;
        await wizard.NextCommand.ExecuteAsync(null);

        var validation = (ValidationStepViewModel)wizard.CurrentStep!;
        await WpfTestHost.WaitUntilAsync(() => !validation.IsBusy && validation.Checks.Count > 5, "preflight");
        await Save(window, "wizard-4-validation");
        if (validation.RequiresAcknowledgement) validation.IsAcknowledged = true;
        await wizard.NextCommand.ExecuteAsync(null);

        await Save(window, "wizard-5-scope");
        await wizard.NextCommand.ExecuteAsync(null);

        var review = (ReviewStepViewModel)wizard.CurrentStep!;
        await review.ValidateCommand.ExecuteAsync(null);
        await Save(window, "wizard-6-review");
        await wizard.NextCommand.ExecuteAsync(null);

        var execute = (ExecuteStepViewModel)wizard.CurrentStep!;
        execute.IsConfirmed = true;
        await execute.ExecuteCommand.ExecuteAsync(null);
        await Save(window, "wizard-7-execute");
        await wizard.NextCommand.ExecuteAsync(null);

        var verify = (VerifyStepViewModel)wizard.CurrentStep!;
        await WpfTestHost.WaitUntilAsync(() => !verify.IsBusy && verify.OverallLabel is not null, "verify");
        await Save(window, "wizard-8-verify");
    }

    /// <summary>A tenant with one well-scoped application and one dangerously configured application.</summary>
    private static void SeedRealisticTenant(TestEnvironment env)
    {
        var ex = env.Exchange;
        var polo = ex.SeedPointer(Fixtures.Polo);
        var filter = Domain.Scoping.RecipientFilterBuilder.Build([Fixtures.Alice, Fixtures.FinanceShared], Domain.Scoping.RecipientFilterStrategy.ExternalDirectoryObjectId).Filter!;
        ex.SeedScope("SCOPE_APP_POLO_TEST_AUTOMATION_MAIL", filter);
        ex.SeedAssignment(polo, "Application Mail.Read", "SCOPE_APP_POLO_TEST_AUTOMATION_MAIL", "RA_POLO_TEST_AUTOMATION_MAIL.READ");

        env.Entra.ServicePrincipals[Fixtures.ReportingApp.ObjectId] = Fixtures.ReportingApp;
        var reporting = ex.SeedPointer(Fixtures.ReportingApp);
        ex.SeedScope("Finance department", "CustomAttribute1 -eq 'Finance'");
        ex.SeedAssignment(reporting, "Application Mail.Send", null, "Application Mail.Send-MailboxReporting");
        ex.SeedAssignment(reporting, "Application Calendars.Read", "Finance department", "Application Calendars.Read-MailboxReporting");
        env.Entra.Grants[Fixtures.ReportingApp.ObjectId] = [Fixtures.GraphGrant("Mail.Read"), Fixtures.GraphGrant("User.Read.All")];
        env.Entra.Grants[Fixtures.PoloObjectId] = [Fixtures.GraphGrant("Calendars.Read")];
    }

    private static async Task Save(Window window, string name)
    {
        window.UpdateLayout();
        await Task.Delay(250);
        var content = (FrameworkElement)window.Content;
        var width = (int)content.ActualWidth;
        var height = (int)content.ActualHeight;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var background = window.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.White;
            context.DrawRectangle(background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var stream = File.Create(Path.Combine(OutputDirectory!, name + ".png"));
        encoder.Save(stream);
    }
}
