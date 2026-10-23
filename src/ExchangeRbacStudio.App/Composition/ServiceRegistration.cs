using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.App.ViewModels;
using ExchangeRbacStudio.App.ViewModels.Dialogs;
using ExchangeRbacStudio.App.ViewModels.Pages;
using ExchangeRbacStudio.App.ViewModels.Shared;
using ExchangeRbacStudio.App.ViewModels.Wizard;
using ExchangeRbacStudio.App.Views;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Applications;
using ExchangeRbacStudio.Application.Auditing;
using ExchangeRbacStudio.Application.Authorization;
using ExchangeRbacStudio.Application.Configuration;
using ExchangeRbacStudio.Application.Execution;
using ExchangeRbacStudio.Application.Inventory;
using ExchangeRbacStudio.Application.Mailboxes;
using ExchangeRbacStudio.Application.Permissions;
using ExchangeRbacStudio.Application.Scoping;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Infrastructure.Exchange;
using ExchangeRbacStudio.Infrastructure.Graph;
using ExchangeRbacStudio.Infrastructure.Persistence;
using ExchangeRbacStudio.Infrastructure.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace ExchangeRbacStudio.App.Composition;

/// <summary>Composition root: wires infrastructure adapters to application ports and registers view models.</summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddExchangeRbacStudio(this IServiceCollection services, AppPaths paths, string version)
    {
        // Platform and infrastructure
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new StudioRuntimeInfo(version, Environment.MachineName));
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IAuditLog, JsonlAuditLog>();
        services.AddSingleton<IReportExporter, ReportFileExporter>();
        services.AddSingleton<IChangeLock, FileChangeLock>();
        services.AddSingleton<IEntraDirectory, GraphEntraDirectory>();
        services.AddSingleton(sp => ExchangeHostOptions.Default(paths, () => sp.GetRequiredService<ISettingsStore>().Current.PowerShellPath));
        services.AddSingleton<IExchangeRbacGateway, PowerShellExchangeGateway>();

        // Application services
        services.AddSingleton<AuditRecorder>();
        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<RbacInventoryService>();
        services.AddSingleton<ApplicationIdentityService>();
        services.AddSingleton<ApplicationProfileService>();
        services.AddSingleton<MailboxDirectoryService>();
        services.AddSingleton<RoleCatalogService>();
        services.AddSingleton<ScopeDesigner>();
        services.AddSingleton<ConfigurationStateReader>();
        services.AddSingleton<PreflightService>();
        services.AddSingleton<ChangeExecutor>();
        services.AddSingleton<ManagementPlanService>();
        services.AddSingleton<AuthorizationTestService>();

        // UI services
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();

        // View models
        services.AddTransient<MailboxPickerViewModel>();
        services.AddSingleton<Func<MailboxPickerViewModel>>(sp => () => sp.GetRequiredService<MailboxPickerViewModel>());
        services.AddSingleton<WizardServices>();
        services.AddTransient<ConnectDialogViewModel>();
        services.AddTransient<ChangeReviewViewModel>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ApplicationsViewModel>();
        services.AddTransient<ApplicationDetailViewModel>();
        services.AddSingleton<ScopesViewModel>();
        services.AddSingleton<RoleAssignmentsViewModel>();
        services.AddSingleton<AuthorizationTesterViewModel>();
        services.AddSingleton<ChangeHistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<ConfigureAccessWizardViewModel>();

        services.AddSingleton<SessionBarViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
