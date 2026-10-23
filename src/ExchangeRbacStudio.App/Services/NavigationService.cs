using ExchangeRbacStudio.App.ViewModels;
using ExchangeRbacStudio.App.ViewModels.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace ExchangeRbacStudio.App.Services;

public enum PageKey
{
    Dashboard,
    Applications,
    ApplicationDetail,
    Scopes,
    RoleAssignments,
    AuthorizationTester,
    ChangeHistory,
    Settings,
    ConfigureAccess,
}

/// <summary>View-model-first navigation. Pages are resolved from DI; main pages keep their state between visits.</summary>
public sealed class NavigationService(IServiceProvider services)
{
    private static readonly Dictionary<PageKey, Type> Pages = new()
    {
        [PageKey.Dashboard] = typeof(DashboardViewModel),
        [PageKey.Applications] = typeof(ApplicationsViewModel),
        [PageKey.ApplicationDetail] = typeof(ApplicationDetailViewModel),
        [PageKey.Scopes] = typeof(ScopesViewModel),
        [PageKey.RoleAssignments] = typeof(RoleAssignmentsViewModel),
        [PageKey.AuthorizationTester] = typeof(AuthorizationTesterViewModel),
        [PageKey.ChangeHistory] = typeof(ChangeHistoryViewModel),
        [PageKey.Settings] = typeof(SettingsViewModel),
        [PageKey.ConfigureAccess] = typeof(ViewModels.Wizard.ConfigureAccessWizardViewModel),
    };

    public event EventHandler<PageViewModel>? Navigated;

    public PageKey? CurrentKey { get; private set; }

    public PageViewModel? CurrentPage { get; private set; }

    public void Navigate(PageKey key, object? parameter = null)
    {
        var page = (PageViewModel)services.GetRequiredService(Pages[key]);
        if (CurrentPage is { KeepAlive: false } previous && !ReferenceEquals(previous, page)) previous.Dispose();
        CurrentKey = key;
        CurrentPage = page;
        Navigated?.Invoke(this, page);
        _ = page.NavigatedToAsync(parameter);
    }
}
