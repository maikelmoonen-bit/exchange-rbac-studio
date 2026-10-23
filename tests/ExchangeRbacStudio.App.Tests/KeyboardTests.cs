using System.Windows;
using System.Windows.Input;
using ExchangeRbacStudio.App.ViewModels;
using ExchangeRbacStudio.App.ViewModels.Pages;
using ExchangeRbacStudio.App.ViewModels.Wizard;
using ExchangeRbacStudio.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ExchangeRbacStudio.App.Tests;

/// <summary>Keyboard accessibility verified through WPF's own input bindings and focus traversal (no synthetic OS input).</summary>
[Collection("WPF")]
public sealed class KeyboardTests
{
    private static async Task<(MainWindow Window, ShellViewModel Shell)> ShowAsync(IServiceProvider services)
    {
        var window = services.GetRequiredService<MainWindow>();
        window.Left = -20000;
        window.ShowActivated = false;
        window.Show();
        var shell = services.GetRequiredService<ShellViewModel>();
        await WpfTestHost.WaitUntilAsync(() => shell.CurrentPage is not null, "initial page");
        return (window, shell);
    }

    private static bool Execute(Window window, Key key, ModifierKeys modifiers)
    {
        var binding = window.InputBindings.OfType<KeyBinding>().SingleOrDefault(b => b.Key == key && b.Modifiers == modifiers);
        if (binding?.Command is null || !binding.Command.CanExecute(binding.CommandParameter)) return false;
        binding.Command.Execute(binding.CommandParameter);
        return true;
    }

    [Fact]
    public Task Shortcuts_navigate_to_every_section() => WpfTestHost.Instance.RunAsync(async () =>
    {
        var (services, _) = WpfTestHost.BuildServices();
        var (window, shell) = await ShowAsync(services);

        var expected = new (Key Key, Type Page)[]
        {
            (Key.D2, typeof(ApplicationsViewModel)),
            (Key.D3, typeof(ScopesViewModel)),
            (Key.D4, typeof(RoleAssignmentsViewModel)),
            (Key.D5, typeof(AuthorizationTesterViewModel)),
            (Key.D6, typeof(ChangeHistoryViewModel)),
            (Key.D7, typeof(SettingsViewModel)),
            (Key.D1, typeof(DashboardViewModel)),
        };

        foreach (var (key, page) in expected)
        {
            Assert.True(Execute(window, key, ModifierKeys.Control), $"Ctrl+{key} is not bound.");
            Assert.IsType(page, shell.CurrentPage);
        }

        Assert.True(Execute(window, Key.N, ModifierKeys.Control));
        Assert.IsType<ConfigureAccessWizardViewModel>(shell.CurrentPage);
        Assert.True(Execute(window, Key.F5, ModifierKeys.None));
        window.Close();
    });

    [Fact]
    public Task Tab_order_reaches_navigation_session_controls_and_page_content() => WpfTestHost.Instance.RunAsync(async () =>
    {
        var (services, _) = WpfTestHost.BuildServices();
        var (window, _) = await ShowAsync(services);
        window.UpdateLayout();

        var visited = new List<string>();
        var start = (UIElement)window.Content;
        start.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        for (var i = 0; i < 25; i++)
        {
            if (Keyboard.FocusedElement is FrameworkElement focused)
            {
                var id = System.Windows.Automation.AutomationProperties.GetAutomationId(focused);
                visited.Add(string.IsNullOrEmpty(id) ? focused.GetType().Name + ":" + (focused as System.Windows.Controls.ContentControl)?.Content : id);
            }

            (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        var order = string.Join(", ", visited);
        var cycle = visited.Take(visited.IndexOf(visited[0], 1) is > 0 and var end ? end : visited.Count).ToList();

        // Primary action, then main navigation before footer navigation, then the session bar, then page content.
        Assert.Equal("ConfigureAccessButton", cycle[0]);
        Assert.True(cycle.IndexOf("NavDashboard") is > 0 and var dashboard && dashboard < cycle.IndexOf("NavSettings"), "Tab order: " + order);
        Assert.True(cycle.IndexOf("NavSettings") < cycle.IndexOf("ConnectButton"), "Tab order: " + order);
        Assert.True(cycle.IndexOf("ThemeToggle") < cycle.IndexOf("Button:Configure scoped access"), "Tab order: " + order);
        Assert.Contains("Button:Connect", cycle);
        window.Close();
    });
}
