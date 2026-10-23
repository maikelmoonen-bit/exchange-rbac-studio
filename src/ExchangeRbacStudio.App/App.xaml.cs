using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using ExchangeRbacStudio.App.Composition;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.App.Views;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Infrastructure.Logging;
using ExchangeRbacStudio.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace ExchangeRbacStudio.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private Serilog.Core.Logger? _log;

    internal IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("The application host is not running.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths();
        paths.EnsureCreated();
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";
        var level = e.Args.Contains("--verbose", StringComparer.OrdinalIgnoreCase) ? LogEventLevel.Debug : LogEventLevel.Information;
        _log = LoggingConfiguration.CreateLogger(paths, level, version);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Args = e.Args });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddSerilog(_log, dispose: false);
        builder.Services.AddExchangeRbacStudio(paths, version);
        _host = builder.Build();
        _host.Start();

        _log.Information("Exchange RBAC Studio {Version} started", version);
        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                // Clean disconnection: shuts down the PowerShell host, which runs Disconnect-ExchangeOnline.
                var gateway = _host.Services.GetRequiredService<IExchangeRbacGateway>();
                Task.Run(async () => await gateway.DisposeAsync()).Wait(TimeSpan.FromSeconds(25));
                _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                _host.Dispose();
            }
        }
        catch (Exception ex)
        {
            _log?.Warning(ex, "Shutdown did not complete cleanly");
        }
        finally
        {
            _log?.Information("Exchange RBAC Studio exited");
            _log?.Dispose();
            base.OnExit(e);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        try
        {
            _host?.Services.GetService<IDialogService>()?.ShowError(UserFacingError.From(e.Exception, "Unexpected error"));
        }
        catch (Exception dialogError)
        {
            _log?.Error(dialogError, "Could not display the error dialog");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.Error(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _log?.Fatal(e.ExceptionObject as Exception, "Fatal unhandled exception (terminating: {Terminating})", e.IsTerminating);
        _log?.Dispose();
    }
}
