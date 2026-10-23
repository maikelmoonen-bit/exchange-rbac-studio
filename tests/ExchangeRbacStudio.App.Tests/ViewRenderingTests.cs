using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.App.ViewModels;
using ExchangeRbacStudio.App.ViewModels.Dialogs;
using ExchangeRbacStudio.App.ViewModels.Pages;
using ExchangeRbacStudio.App.ViewModels.Wizard;
using ExchangeRbacStudio.App.Views;
using ExchangeRbacStudio.App.Views.Dialogs;
using ExchangeRbacStudio.Application.Settings;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace ExchangeRbacStudio.App.Tests;

[Collection("WPF")]
public sealed class ViewRenderingTests
{
    private static async Task RenderAsync(object content, double width = 1400, double height = 900)
    {
        var window = new Window
        {
            Content = new ContentControl { Content = content },
            Width = width,
            Height = height,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
        };
        window.Show();
        window.UpdateLayout();
        await Task.Delay(50);
        window.UpdateLayout();
        window.Close();
    }

    public static TheoryData<string> Pages => new()
    {
        nameof(DashboardViewModel), nameof(ApplicationsViewModel), nameof(ScopesViewModel), nameof(RoleAssignmentsViewModel),
        nameof(AuthorizationTesterViewModel), nameof(ChangeHistoryViewModel), nameof(SettingsViewModel), nameof(ConfigureAccessWizardViewModel),
        nameof(ApplicationDetailViewModel),
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public Task Every_page_renders_disconnected_and_connected_without_binding_errors(string pageName) => WpfTestHost.Instance.RunAsync(async () =>
    {
        var (services, _) = WpfTestHost.BuildServices();
        var type = typeof(PageViewModel).Assembly.GetTypes().Single(t => t.Name == pageName);
        WpfTestHost.Instance.BindingErrors.Clear();

        var disconnected = (PageViewModel)services.GetRequiredService(type);
        await RenderAsync(disconnected);

        await services.ConnectFakesAsync();
        var connected = (PageViewModel)services.GetRequiredService(type);
        await connected.NavigatedToAsync(pageName == nameof(ApplicationDetailViewModel) ? new ApplicationDetailRequest(Fixtures.PoloObjectId, Fixtures.PoloAppId) : null);
        await WpfTestHost.WaitUntilAsync(() => !connected.IsBusy, "page load");
        Assert.Null(connected.Error);
        await RenderAsync(connected);
        await RenderAsync(connected, width: 980, height: 640);

        Assert.Empty(WpfTestHost.Instance.BindingErrors.Snapshot());
    });

    [Fact]
    public Task Every_wizard_step_template_renders() => WpfTestHost.Instance.RunAsync(async () =>
    {
        var (services, _) = WpfTestHost.BuildServices();
        await services.ConnectFakesAsync();
        WpfTestHost.Instance.BindingErrors.Clear();
        var wizard = services.GetRequiredService<ConfigureAccessWizardViewModel>();
        foreach (var step in wizard.Steps) await RenderAsync(step);
        Assert.Empty(WpfTestHost.Instance.BindingErrors.Snapshot());
    });

    [Fact]
    public Task Main_window_and_dialogs_load_in_light_and_dark_themes() => WpfTestHost.Instance.RunAsync(async () =>
    {
        var (services, env) = WpfTestHost.BuildServices();
        WpfTestHost.Instance.BindingErrors.Clear();
        var window = services.GetRequiredService<MainWindow>();
        window.Left = -20000;
        window.ShowActivated = false;
        window.Show();
        await WpfTestHost.WaitUntilAsync(() => services.GetRequiredService<ShellViewModel>().CurrentPage is not null, "initial navigation");

        var theme = services.GetRequiredService<ThemeService>();
        theme.Apply(ThemePreference.Dark);
        window.UpdateLayout();
        Assert.True(theme.IsDark);
        theme.Apply(ThemePreference.Light);
        window.UpdateLayout();
        Assert.False(theme.IsDark);

        window.Width = 980;
        window.UpdateLayout();
        await WpfTestHost.WaitUntilAsync(() => services.GetRequiredService<ShellViewModel>().IsPaneCompact, "compact pane at narrow width");
        window.Width = 1500;
        window.UpdateLayout();
        await WpfTestHost.WaitUntilAsync(() => !services.GetRequiredService<ShellViewModel>().IsPaneCompact, "full pane at wide width");

        await services.ConnectFakesAsync();
        foreach (var key in Enum.GetValues<PageKey>().Where(k => k != PageKey.ApplicationDetail))
        {
            services.GetRequiredService<NavigationService>().Navigate(key);
            window.UpdateLayout();
            await Task.Delay(30);
        }

        var connect = new ConnectDialog { DataContext = services.GetRequiredService<ConnectDialogViewModel>(), Left = -20000, ShowActivated = false };
        connect.Show();
        connect.UpdateLayout();
        connect.Close();

        var review = services.GetRequiredService<ChangeReviewViewModel>();
        var prepared = await services.GetRequiredService<ExchangeRbacStudio.Application.Configuration.ManagementPlanService>().RemoveScopeAsync("missing", CancellationToken.None);
        review.Initialize(new ChangeReviewRequest(prepared.Plan, prepared.Replan, "Remove", "Test", "missing"));
        var reviewWindow = new ChangeReviewDialog { DataContext = review, Left = -20000, ShowActivated = false };
        reviewWindow.Show();
        reviewWindow.UpdateLayout();
        reviewWindow.Close();

        var confirm = new ConfirmDialog("Title", "Message", "OK", destructive: true) { Left = -20000, ShowActivated = false };
        confirm.Show();
        confirm.Close();

        window.Close();
        Assert.Empty(WpfTestHost.Instance.BindingErrors.Snapshot());
    });
}

public sealed class IconGlyphTests
{
    [Fact]
    public void Every_icon_used_by_the_application_has_a_glyph_in_the_fluent_font()
    {
        var used = UsedSymbols();
        Assert.NotEmpty(used);

        var fontPath = ExtractFont("resources/fonts/fluentsystemicons-regular.ttf");
        var typeface = new GlyphTypeface(new Uri(fontPath));
        var missing = new List<string>();
        foreach (var name in used)
        {
            Assert.True(Enum.TryParse<SymbolRegular>(name, out var symbol), $"'{name}' is not a SymbolRegular value.");
            // Supplementary-plane code points have glyphs but do not render in SymbolIcon, so they count as missing.
            if ((int)symbol > 0xFFFF || !typeface.CharacterToGlyphMap.ContainsKey((int)symbol)) missing.Add($"{name} (U+{(int)symbol:X})");
        }

        Assert.True(missing.Count == 0, "Icons that cannot render: " + string.Join(", ", missing));
    }

    private static HashSet<string> UsedSymbols()
    {
        var root = FindRepositoryRoot();
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        var app = Path.Combine(root, "src", "ExchangeRbacStudio.App");
        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"SymbolIcon (\w+)\}")) symbols.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"Symbol=""(\w+)""")) symbols.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"Property=""Symbol"" Value=""(\w+)""")) symbols.Add(m.Groups[1].Value);
        }

        foreach (var file in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"SymbolRegular\.(\w+)")) symbols.Add(m.Groups[1].Value);

        return symbols;
    }

    private static string ExtractFont(string resourceKey)
    {
        var assembly = typeof(SymbolIcon).Assembly;
        using var resources = assembly.GetManifestResourceStream(assembly.GetName().Name + ".g.resources")!;
        using var reader = new ResourceReader(resources);
        reader.GetResourceData(resourceKey, out _, out var data);
        var path = Path.Combine(Path.GetTempPath(), $"ers-{Guid.NewGuid():N}-" + Path.GetFileName(resourceKey));
        // Stream resources are prefixed with a 4-byte length.
        File.WriteAllBytes(path, data[4..]);
        return path;
    }

    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ExchangeRbacStudio.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
