using System.Windows;
using ExchangeRbacStudio.App.Services;

namespace ExchangeRbacStudio.App.Tests;

[Collection("WPF")]
public sealed class UiInfrastructureTests
{
    [Fact]
    public Task Work_posted_from_a_background_thread_runs_on_the_UI_thread() => WpfTestHost.Instance.RunAsync(async () =>
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var dispatcher = new WpfUiDispatcher();
        int? ranOn = null;

        await Task.Run(() => dispatcher.Post(() => ranOn = Environment.CurrentManagedThreadId));
        await WpfTestHost.WaitUntilAsync(() => ranOn is not null, "posted work");

        Assert.Equal(uiThread, ranOn);
    });

    /// <summary>App.xaml's OnMainWindowClose must not survive in the shared test application, or closing a test window ends it.</summary>
    [Fact]
    public Task The_shared_test_application_only_shuts_down_explicitly() => WpfTestHost.Instance.RunAsync(() =>
        Assert.Equal(ShutdownMode.OnExplicitShutdown, System.Windows.Application.Current.ShutdownMode));
}
