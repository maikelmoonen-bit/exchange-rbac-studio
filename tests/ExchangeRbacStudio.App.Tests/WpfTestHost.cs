using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using ExchangeRbacStudio.App.Composition;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.App.ViewModels.Dialogs;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Execution;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.Tests;

/// <summary>
/// Hosts the real WPF application resources on a single long-lived STA thread with a running dispatcher.
/// All UI tests marshal their work onto this thread.
/// </summary>
public sealed class WpfTestHost
{
    private static readonly Lazy<WpfTestHost> LazyInstance = new(() => new WpfTestHost());
    private readonly Dispatcher _dispatcher;

    private WpfTestHost()
    {
        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        Exception? startupError = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                // After InitializeComponent: App.xaml sets OnMainWindowClose, which would shut the shared application down
                // as soon as a test closes the first window it created.
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch (Exception ex)
            {
                startupError = ex;
            }
            finally
            {
                ready.Set();
            }

            if (startupError is null) Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF test host",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (startupError is not null) ExceptionDispatchInfo.Throw(startupError);
        _dispatcher = dispatcher!;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(BindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
    }

    public static WpfTestHost Instance => LazyInstance.Value;

    public BindingErrorListener BindingErrors { get; } = new();

    public Task RunAsync(Func<Task> action) => _dispatcher.InvokeAsync(action).Task.Unwrap();

    public Task RunAsync(Action action) => _dispatcher.InvokeAsync(action).Task;

    /// <summary>Builds the production service graph with fake Microsoft 365 adapters.</summary>
    public static (ServiceProvider Services, TestEnvironment Env) BuildServices()
    {
        var env = new TestEnvironment();
        var root = Path.Combine(Path.GetTempPath(), "ers-ui-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddExchangeRbacStudio(new AppPaths(root, root, root), "1.0.0-test");
        services.AddSingleton<IExchangeRbacGateway>(env.Exchange);
        services.AddSingleton<IEntraDirectory>(env.Entra);
        services.AddSingleton<ISettingsStore>(env.Settings);
        services.AddSingleton<IAuditLog>(env.Audit);
        services.AddSingleton<IChangeLock>(env.ChangeLock);
        services.AddSingleton<IReportExporter>(new FakeReportExporter());
        services.AddSingleton<IDialogService>(new FakeDialogService());
        return (services.BuildServiceProvider(), env);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string description, int timeoutMs = 10_000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Timed out waiting for: " + description);
            await Task.Delay(20);
        }
    }
}

public sealed class BindingErrorListener : TraceListener
{
    private readonly List<string> _errors = [];

    public IReadOnlyList<string> Snapshot()
    {
        lock (_errors) return _errors.ToList();
    }

    public void Clear()
    {
        lock (_errors) _errors.Clear();
    }

    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message)
    {
        if (message is null) return;
        lock (_errors) _errors.Add(message);
    }
}

public sealed class FakeDialogService : IDialogService
{
    public List<ChangeReviewRequest> Reviews { get; } = [];

    public bool ShowConnectDialog() => false;

    public ExecutionResult? ShowChangeReview(ChangeReviewRequest request)
    {
        Reviews.Add(request);
        return null;
    }

    public bool Confirm(string title, string message, string confirmLabel, bool destructive) => true;

    public void ShowError(UserFacingError error)
    {
    }

    public string? PickFolder(string title, string? initialDirectory) => Path.GetTempPath();

    public string? PickPowerShellExecutable(string? initialDirectory) => null;
}

internal static class SessionExtensions
{
    public static Task ConnectFakesAsync(this IServiceProvider services) =>
        services.GetRequiredService<ConnectionManager>().ConnectAsync(CancellationToken.None);
}
