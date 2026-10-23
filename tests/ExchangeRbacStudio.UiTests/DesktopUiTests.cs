using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace ExchangeRbacStudio.UiTests;

/// <summary>
/// Drives the real, built application through UI Automation. These tests take over the desktop, so they are opt-in:
/// set ERS_UI_TESTS=1. No sign-in is attempted; the Connect dialog is opened and closed without connecting.
/// </summary>
[Collection("Desktop")]
public sealed class DesktopUiTests : IDisposable
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("ERS_UI_TESTS") == "1";
    private readonly FlaUI.Core.Application? _app;
    private readonly UIA3Automation? _automation;
    private readonly Window? _window;

    public DesktopUiTests()
    {
        if (!Enabled) return;
        var exe = LocateExecutable();
        _app = FlaUI.Core.Application.Launch(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)! });
        _automation = new UIA3Automation();
        _window = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(30));
        Assert.NotNull(_window);
        _window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Normal);
        _window.Patterns.Transform.Pattern.Resize(1400, 880);
        _window.Focus();
    }

    public void Dispose()
    {
        try
        {
            _app?.Close();
            if (_app is { HasExited: false }) _app.Kill();
        }
        catch (Exception)
        {
            _app?.Kill();
        }

        _automation?.Dispose();
        _app?.Dispose();
    }

    private Window MainWindow()
    {
        Assert.SkipUnless(Enabled, "Set ERS_UI_TESTS=1 to run desktop UI automation tests.");
        return _window!;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Clicks into the application and verifies it is the foreground window before any keyboard input is sent,
    /// so keystrokes can never reach another application on the desktop.
    /// </summary>
    private void EnsureKeyboardFocus(Window window)
    {
        try
        {
            WaitFor(window, "NavDashboard").Click();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Assert.Skip($"Synthetic input is not permitted in this session ({ex.Message}). Keyboard behaviour is covered by the WPF tests in ExchangeRbacStudio.App.Tests.");
        }

        Wait.UntilInputIsProcessed();
        var foreground = Retry.WhileFalse(() => GetForegroundWindow() == window.Properties.NativeWindowHandle.ValueOrDefault, TimeSpan.FromSeconds(5)).Result;
        Assert.SkipUnless(foreground, "The application could not become the foreground window; keyboard tests skipped to avoid typing into other applications.");
    }

    private static AutomationElement WaitFor(AutomationElement root, string automationId, int timeoutSeconds = 10)
    {
        var element = Retry.WhileNull(() => root.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), TimeSpan.FromSeconds(timeoutSeconds)).Result;
        Assert.True(element is not null, $"Element '{automationId}' was not found.");
        return element!;
    }

    [Fact]
    public void Application_launches_disconnected_with_separate_service_status()
    {
        var window = MainWindow();
        Assert.Equal("Exchange RBAC Studio", window.Title);
        WaitFor(window, "DashboardPage");
        Assert.Equal("Not connected", WaitFor(window, "TenantName").Name);
        Assert.Contains("Entra ID", WaitFor(window, "GraphStatus").Name);
        Assert.Contains("Exchange Online", WaitFor(window, "ExchangeStatus").Name);
        Assert.NotNull(WaitFor(window, "ConnectButton"));
    }

    [Theory]
    [InlineData("NavApplications", "ApplicationsPage")]
    [InlineData("NavScopes", "ScopesPage")]
    [InlineData("NavRoleAssignments", "RoleAssignmentsPage")]
    [InlineData("NavAuthorizationTester", "AuthorizationTesterPage")]
    [InlineData("NavChangeHistory", "ChangeHistoryPage")]
    [InlineData("NavSettings", "SettingsPage")]
    [InlineData("NavDashboard", "DashboardPage")]
    public void Every_navigation_item_opens_its_page(string navId, string pageId)
    {
        var window = MainWindow();
        var item = WaitFor(window, navId).AsListBoxItem();
        var stopwatch = Stopwatch.StartNew();
        item.Select();
        WaitFor(window, pageId, timeoutSeconds: 5);
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"Navigation took {stopwatch.ElapsedMilliseconds} ms.");
        Assert.False(_app!.HasExited);
    }

    [Fact]
    public void Keyboard_shortcuts_navigate_and_open_the_wizard()
    {
        var window = MainWindow();
        EnsureKeyboardFocus(window);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_3);
        WaitFor(window, "ScopesPage");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_N);
        WaitFor(window, "ConfigureAccessWizard");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);
        WaitFor(window, "DashboardPage");
    }

    [Fact]
    public void Tab_moves_keyboard_focus_through_the_shell()
    {
        var window = MainWindow();
        EnsureKeyboardFocus(window);
        var focused = new HashSet<string>();
        for (var i = 0; i < 12; i++)
        {
            Keyboard.Type(VirtualKeyShort.TAB);
            Wait.UntilInputIsProcessed();
            var element = _automation!.FocusedElement();
            if (element is not null)
                focused.Add(element.Properties.AutomationId.ValueOrDefault + "|" + element.Properties.Name.ValueOrDefault + "|" + element.Properties.BoundingRectangle.ValueOrDefault);
        }

        Assert.True(focused.Count >= 4, "Keyboard focus should move between several controls.");
    }

    [Fact]
    public void Theme_toggle_and_resizing_keep_the_application_responsive()
    {
        var window = MainWindow();
        WaitFor(window, "ThemeToggle").AsButton().Invoke();
        Wait.UntilInputIsProcessed();
        WaitFor(window, "ThemeToggle").AsButton().Invoke();
        Wait.UntilInputIsProcessed();

        window.Patterns.Transform.Pattern.Resize(980, 660);
        Wait.UntilInputIsProcessed();
        WaitFor(window, "NavScopes").AsListBoxItem().Select();
        WaitFor(window, "ScopesPage");

        window.Patterns.Transform.Pattern.Resize(1600, 1000);
        Wait.UntilInputIsProcessed();
        WaitFor(window, "NavDashboard").AsListBoxItem().Select();
        WaitFor(window, "DashboardPage");
        Assert.False(_app!.HasExited);
    }

    [Fact]
    public void Connect_dialog_opens_and_closes_without_signing_in()
    {
        var window = MainWindow();
        WaitFor(window, "ConnectButton").AsButton().Invoke();
        var dialog = Retry.WhileNull(() => window.ModalWindows.FirstOrDefault(), TimeSpan.FromSeconds(10)).Result;
        Assert.NotNull(dialog);
        Assert.NotNull(WaitFor(dialog, "ConnectDialogConnect"));
        WaitFor(dialog, "ConnectDialogClose").AsButton().Invoke();
        Retry.WhileTrue(() => window.ModalWindows.Length > 0, TimeSpan.FromSeconds(10));
        Assert.Empty(window.ModalWindows);
        Assert.Equal("Not connected", WaitFor(window, "TenantName").Name);
    }

    [Fact]
    public void Wizard_requires_a_connection_and_offers_to_connect()
    {
        var window = MainWindow();
        WaitFor(window, "ConfigureAccessButton").AsButton().Invoke();
        var wizard = WaitFor(window, "ConfigureAccessWizard");
        Assert.NotNull(wizard.FindFirstDescendant(cf => cf.ByName("Connect")));
        // The wizard body (and its Next button) is not rendered until both services are connected.
        Assert.Null(wizard.FindFirstDescendant(cf => cf.ByAutomationId("WizardNext")));
    }

    private static string LocateExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ExchangeRbacStudio.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var exe = Path.Combine(directory.FullName, "src", "ExchangeRbacStudio.App", "bin", configuration, "net10.0-windows", "ExchangeRbacStudio.exe");
            if (File.Exists(exe)) return exe;
        }

        throw new FileNotFoundException("Build the application before running UI tests.");
    }
}

[CollectionDefinition("Desktop", DisableParallelization = true)]
public sealed class DesktopCollection;
