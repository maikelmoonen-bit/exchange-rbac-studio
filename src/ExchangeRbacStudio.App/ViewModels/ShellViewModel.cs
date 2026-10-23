using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Application.Settings;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace ExchangeRbacStudio.App.ViewModels;

public sealed record NavItem(PageKey Key, string Title, SymbolRegular Symbol, string Shortcut)
{
    public string AutomationId => "Nav" + Key;

    public string ToolTip => $"{Title} ({Shortcut})";
}

/// <summary>Top session bar: tenant identity, separate Entra ID and Exchange Online status, and session controls.</summary>
public sealed partial class SessionBarViewModel : ObservableObject
{
    private readonly ConnectionManager _connections;
    private readonly IDialogService _dialogs;
    private readonly ThemeService _theme;
    private readonly ISettingsStore _settings;
    private readonly ILogger<SessionBarViewModel> _logger;
    private readonly DispatcherTimer _healthTimer;

    public SessionBarViewModel(ConnectionManager connections, IDialogService dialogs, ThemeService theme, ISettingsStore settings, IUiDispatcher dispatcher, ILogger<SessionBarViewModel> logger)
    {
        _connections = connections;
        _dialogs = dialogs;
        _theme = theme;
        _settings = settings;
        _logger = logger;
        connections.SessionChanged += (_, s) => dispatcher.Post(() => Apply(s));
        Apply(connections.Current);

        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _healthTimer.Tick += async (_, _) => await CheckHealthAsync();
        _healthTimer.Start();
    }

    [ObservableProperty] private string _tenantName = "Not connected";
    [ObservableProperty] private string? _tenantId;
    [ObservableProperty] private string? _administrator;
    [ObservableProperty] private string _environmentName = string.Empty;
    [ObservableProperty] private string _graphLabel = "Not connected";
    [ObservableProperty] private StatusKind _graphKind;
    [ObservableProperty] private string? _graphToolTip;
    [ObservableProperty] private string _exchangeLabel = "Not connected";
    [ObservableProperty] private StatusKind _exchangeKind;
    [ObservableProperty] private string? _exchangeToolTip;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isAnyConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasPrivilegeGap;
    [ObservableProperty] private string? _privilegeGapText;

    [RelayCommand]
    private void Connect() => _dialogs.ShowConnectDialog();

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (!_dialogs.Confirm("Disconnect", $"Disconnect from {TenantName}? Cached configuration for this tenant is discarded.", "Disconnect", destructive: false)) return;
        IsBusy = true;
        try
        {
            await _connections.DisconnectAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Re-establishes the Exchange session so that newly activated (PIM) roles are applied.</summary>
    [RelayCommand]
    private async Task ReconnectAsync()
    {
        IsBusy = true;
        try
        {
            if (!_connections.Current.IsGraphConnected) await _connections.ConnectGraphAsync(CancellationToken.None);
            await _connections.ReconnectExchangeAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconnect failed");
            _dialogs.ShowError(UserFacingError.From(ex, "Reconnect"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CopyTenantId() => ClipboardHelper.TrySetText(TenantId);

    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        var next = _theme.IsDark ? ThemePreference.Light : ThemePreference.Dark;
        _theme.Apply(next);
        try
        {
            await _settings.SaveAsync(_settings.Current with { Theme = next });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Theme preference could not be saved");
        }
    }

    private async Task CheckHealthAsync()
    {
        try
        {
            await _connections.RefreshHealthAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Health check failed");
        }
    }

    private void Apply(SessionSnapshot s)
    {
        TenantName = s.TenantId is null ? "Not connected" : s.TenantDisplayName;
        TenantId = s.TenantId?.ToString();
        Administrator = s.AdministratorUpn;
        EnvironmentName = s.Environment.DisplayName;
        GraphLabel = "Entra ID: " + StatusMapper.Label(s.Graph);
        GraphKind = StatusMapper.From(s.Graph);
        GraphToolTip = s.GraphStatusMessage ?? "Microsoft Graph (delegated: User.Read, Application.Read.All)";
        ExchangeLabel = "Exchange Online: " + StatusMapper.Label(s.Exchange);
        ExchangeKind = StatusMapper.From(s.Exchange);
        ExchangeToolTip = s.ExchangeStatusMessage ?? "Exchange Online PowerShell (isolated PowerShell 7 host)";
        if (s.IsExchangeConnected && s.ExchangeConnection?.TokenExpiryUtc is { } expiry)
            ExchangeToolTip += $"\nToken valid until {expiry.ToLocalTime():g}";
        IsConnected = s.IsFullyConnected;
        IsAnyConnected = s.IsGraphConnected || s.IsExchangeConnected || s.Graph == ServiceConnectionState.Failed || s.Exchange is ServiceConnectionState.Failed or ServiceConnectionState.Expired;
        HasPrivilegeGap = s.IsExchangeConnected && s.MissingExchangeCommands.Count > 0;
        PrivilegeGapText = HasPrivilegeGap
            ? $"Your Exchange roles do not allow: {string.Join(", ", s.MissingExchangeCommands)}. Activate the required role (PIM) and choose Reconnect."
            : null;
    }
}

/// <summary>Main window: navigation, current page and session bar.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly NavigationService _navigation;
    private bool _syncingSelection;

    public ShellViewModel(NavigationService navigation, SessionBarViewModel session)
    {
        _navigation = navigation;
        Session = session;
        navigation.Navigated += (_, page) =>
        {
            CurrentPage = page;
            _syncingSelection = true;
            var key = navigation.CurrentKey == PageKey.ApplicationDetail ? PageKey.Applications : navigation.CurrentKey;
            SelectedNavItem = NavItems.FirstOrDefault(n => n.Key == key);
            SelectedFooterNavItem = FooterNavItems.FirstOrDefault(n => n.Key == key);
            _syncingSelection = false;
        };
    }

    [ObservableProperty] private NavItem? _selectedFooterNavItem;

    partial void OnSelectedFooterNavItemChanged(NavItem? value)
    {
        if (_syncingSelection || value is null) return;
        if (_navigation.CurrentKey != value.Key) _navigation.Navigate(value.Key);
    }

    public SessionBarViewModel Session { get; }

    public ObservableCollection<NavItem> NavItems { get; } =
    [
        new(PageKey.Dashboard, "Dashboard", SymbolRegular.Home24, "Ctrl+1"),
        new(PageKey.Applications, "Applications", SymbolRegular.AppGeneric24, "Ctrl+2"),
        new(PageKey.Scopes, "Management Scopes", SymbolRegular.Target24, "Ctrl+3"),
        new(PageKey.RoleAssignments, "Role Assignments", SymbolRegular.Key24, "Ctrl+4"),
        new(PageKey.AuthorizationTester, "Authorization Tester", SymbolRegular.ShieldCheckmark24, "Ctrl+5"),
        new(PageKey.ChangeHistory, "Change History", SymbolRegular.History24, "Ctrl+6"),
    ];

    public ObservableCollection<NavItem> FooterNavItems { get; } =
    [
        new(PageKey.Settings, "Settings", SymbolRegular.Settings24, "Ctrl+7"),
    ];

    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private NavItem? _selectedNavItem;
    [ObservableProperty] private bool _isPaneCompact;

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (_syncingSelection || value is null) return;
        if (_navigation.CurrentKey != value.Key) _navigation.Navigate(value.Key);
    }

    [RelayCommand]
    private void NavigateTo(string key)
    {
        if (Enum.TryParse<PageKey>(key, out var page)) _navigation.Navigate(page);
    }

    [RelayCommand]
    private void ConfigureAccess() => _navigation.Navigate(PageKey.ConfigureAccess);

    [RelayCommand]
    private Task RefreshAsync() => CurrentPage?.RefreshAsync() ?? Task.CompletedTask;

    public void Start() => _navigation.Navigate(PageKey.Dashboard);
}
