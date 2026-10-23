using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Settings;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ExchangeRbacStudio.App.Services;

/// <summary>Marshals work to the UI thread. Abstracted so view models can be tested without a dispatcher.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>
/// Captures the UI dispatcher once: Application.Current becomes null when the application shuts down, and UI work must
/// never fall back to running on the calling (background) thread. Work posted after shutdown has started is dropped.
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher = System.Windows.Application.Current?.Dispatcher
        ?? throw new InvalidOperationException("The UI dispatcher must be created while the WPF application is running.");

    public void Post(Action action)
    {
        if (_dispatcher.HasShutdownStarted) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }
}

/// <summary>Applies light, dark or system theme using the WPF-UI Fluent theme dictionaries.</summary>
public sealed class ThemeService(ISettingsStore settings, ILogger<ThemeService> logger)
{
    private Window? _window;

    public ThemePreference Current { get; private set; } = ThemePreference.System;

    public void Attach(Window window)
    {
        _window = window;
        Apply(settings.Current.Theme);
    }

    public void Apply(ThemePreference preference)
    {
        Current = preference;
        try
        {
            if (_window is { IsLoaded: true }) SystemThemeWatcher.UnWatch(_window);
            switch (preference)
            {
                case ThemePreference.Light:
                    ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, true);
                    break;
                case ThemePreference.Dark:
                    ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true);
                    break;
                default:
                    ApplicationThemeManager.ApplySystemTheme(true);
                    if (_window is { IsLoaded: true }) SystemThemeWatcher.Watch(_window, WindowBackdropType.Mica, true);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Theme could not be applied");
        }
    }

    public bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
}

/// <summary>Opens folders, files and links with the shell. Paths are application-generated, never user-supplied URLs.</summary>
public static class ShellLauncher
{
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
    }

    public static void OpenFile(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void SelectFile(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false });
    }

    public static void OpenDocumentation(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttps && uri.Host.EndsWith("microsoft.com", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}

public static class ClipboardHelper
{
    public static bool TrySetText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(50);
            }
        }

        return false;
    }
}
