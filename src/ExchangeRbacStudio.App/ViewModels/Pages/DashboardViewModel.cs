using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Inventory;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Auditing;
using ExchangeRbacStudio.Domain.Exchange;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.ViewModels.Pages;

public sealed record FindingItem(InventoryFinding Finding)
{
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public string? Remediation => Finding.Remediation;
    public string Category => Finding.Category;
    public StatusKind Kind => StatusMapper.From(Finding.Severity);
    public string SeverityLabel => Finding.Severity.ToString();
}

public sealed record RecentOperationItem(DateTimeOffset Timestamp, string Title, string? Target, string OutcomeLabel, StatusKind Kind, string? Administrator);

/// <summary>Operational overview. Every number shown is computed from data retrieved from the tenant or the local audit log.</summary>
public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly RbacInventoryService _inventoryService;
    private readonly IAuditLog _audit;
    private readonly NavigationService _navigation;
    private readonly IDialogService _dialogs;

    public DashboardViewModel(ConnectionManager connections, IUiDispatcher dispatcher, RbacInventoryService inventory, IAuditLog audit,
        NavigationService navigation, IDialogService dialogs, ILogger<DashboardViewModel> logger)
        : base(connections, dispatcher, logger)
    {
        _inventoryService = inventory;
        _audit = audit;
        _navigation = navigation;
        _dialogs = dialogs;
        inventory.Changed += (_, current) => dispatcher.Post(() =>
        {
            if (current is null) IsStale = Inventory is not null;
            else Apply(current);
        });
        ApplyHealth(connections.Current);
    }

    public override string Title => "Dashboard";

    public override string Subtitle => "Operational overview of Exchange Online RBAC for Applications in the connected tenant.";

    public ObservableCollection<FindingItem> Findings { get; } = [];

    public ObservableCollection<RecentOperationItem> RecentOperations { get; } = [];

    [ObservableProperty] private RbacInventory? _inventory;
    [ObservableProperty] private bool _isStale;
    [ObservableProperty] private string _servicePrincipalCount = "—";
    [ObservableProperty] private string _scopeCount = "—";
    [ObservableProperty] private string _scopeDetail = string.Empty;
    [ObservableProperty] private string _assignmentCount = "—";
    [ObservableProperty] private string _assignmentDetail = string.Empty;
    [ObservableProperty] private string _unsafeCount = "—";
    [ObservableProperty] private string _unsafeDetail = string.Empty;
    [ObservableProperty] private StatusKind _unsafeKind = StatusKind.Neutral;
    [ObservableProperty] private string? _lastRefreshed;
    [ObservableProperty] private string _graphHealth = "Not connected";
    [ObservableProperty] private StatusKind _graphHealthKind;
    [ObservableProperty] private string? _graphHealthDetail;
    [ObservableProperty] private string _exchangeHealth = "Not connected";
    [ObservableProperty] private StatusKind _exchangeHealthKind;
    [ObservableProperty] private string? _exchangeHealthDetail;
    [ObservableProperty] private string? _hostDetail;
    [ObservableProperty] private string? _entraAnalysisNote;

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        await LoadRecentOperationsAsync();
        if (_inventoryService.Current is { } current) Apply(current);
        else if (IsExchangeConnected) await RefreshAsync();
    }

    public override Task RefreshAsync() => RefreshCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task Refresh()
    {
        if (!IsExchangeConnected)
        {
            await LoadRecentOperationsAsync();
            return;
        }

        await RunAsync(async ct =>
        {
            var progress = new Progress<string>(m => BusyMessage = m);
            var inventory = await _inventoryService.RefreshAsync(IsGraphConnected, progress, ct);
            Apply(inventory);
        }, "Loading configuration…", "Refresh dashboard");
        await LoadRecentOperationsAsync();
    }

    [RelayCommand]
    private void Connect() => _dialogs.ShowConnectDialog();

    [RelayCommand]
    private void ConfigureAccess() => _navigation.Navigate(PageKey.ConfigureAccess);

    [RelayCommand]
    private void Open(string key)
    {
        if (Enum.TryParse<PageKey>(key, out var page)) _navigation.Navigate(page);
    }

    protected override void OnTenantSessionChanged(SessionSnapshot previous, SessionSnapshot current)
    {
        ApplyHealth(current);
        if (previous.SessionId != current.SessionId || !current.IsExchangeConnected)
            Clear();
        if (!previous.IsExchangeConnected && current.IsExchangeConnected)
            _ = RefreshAsync();
        else
            _ = LoadRecentOperationsAsync();
    }

    private void Apply(RbacInventory inventory)
    {
        Inventory = inventory;
        IsStale = false;
        ServicePrincipalCount = inventory.ServicePrincipals.Count.ToString("N0");
        ScopeCount = inventory.Scopes.Count.ToString("N0");
        var appScopes = inventory.Scopes.Count(s => inventory.AssignmentsReferencing(s).Count > 0);
        ScopeDetail = $"{appScopes:N0} used by application role assignments";
        AssignmentCount = inventory.Assignments.Count.ToString("N0");
        var orgWide = inventory.Assignments.Count(a => a.Enabled && a.ScopeKind == AssignmentScopeKind.Organization);
        AssignmentDetail = orgWide == 0 ? "None organization-wide" : $"{orgWide:N0} organization-wide";
        UnsafeCount = inventory.UnsafeFindingCount.ToString("N0");
        UnsafeDetail = $"{inventory.WarningFindingCount:N0} additional warning(s)";
        UnsafeKind = inventory.UnsafeFindingCount > 0 ? StatusKind.Critical : inventory.WarningFindingCount > 0 ? StatusKind.Warning : StatusKind.Success;
        LastRefreshed = $"Retrieved {inventory.RetrievedUtc.ToLocalTime():g}";
        EntraAnalysisNote = inventory.Entra is null
            ? "Microsoft Entra ID permissions were not analyzed (Entra ID not connected). Tenant-wide grants that bypass scopes are not reflected in this count."
            : inventory.Entra.Failed.Count > 0 ? $"Entra ID permissions could not be verified for {inventory.Entra.Failed.Count} application(s)." : null;

        Findings.Clear();
        foreach (var finding in inventory.Findings.Where(f => f.Severity >= Domain.Security.FindingSeverity.Warning).Take(12))
            Findings.Add(new FindingItem(finding));
    }

    private void Clear()
    {
        Inventory = null;
        ServicePrincipalCount = ScopeCount = AssignmentCount = UnsafeCount = "—";
        ScopeDetail = AssignmentDetail = UnsafeDetail = string.Empty;
        UnsafeKind = StatusKind.Neutral;
        LastRefreshed = null;
        EntraAnalysisNote = null;
        Findings.Clear();
    }

    private void ApplyHealth(SessionSnapshot s)
    {
        GraphHealth = StatusMapper.Label(s.Graph);
        GraphHealthKind = StatusMapper.From(s.Graph);
        GraphHealthDetail = s.IsGraphConnected ? $"{s.TenantDisplayName} · {s.Administrator?.UserPrincipalName}" : s.GraphStatusMessage;
        ExchangeHealth = StatusMapper.Label(s.Exchange);
        ExchangeHealthKind = s.IsExchangeConnected && s.MissingExchangeCommands.Count > 0 ? StatusKind.Warning : StatusMapper.From(s.Exchange);
        ExchangeHealthDetail = s.IsExchangeConnected
            ? $"{s.ExchangeConnection?.UserPrincipalName}" + (s.ExchangeConnection?.TokenExpiryUtc is { } exp ? $" · token valid until {exp.ToLocalTime():t}" : string.Empty)
              + (s.MissingExchangeCommands.Count > 0 ? $" · {s.MissingExchangeCommands.Count} required cmdlet(s) unavailable" : string.Empty)
            : s.ExchangeStatusMessage;
        HostDetail = s.PowerShellVersion is null ? null : $"PowerShell {s.PowerShellVersion} · ExchangeOnlineManagement {s.ExchangeModuleVersion ?? "not loaded"}";
    }

    private async Task LoadRecentOperationsAsync()
    {
        try
        {
            var entries = await _audit.ReadAsync(new AuditQuery(Session.TenantId, MaxEntries: 300));
            RecentOperations.Clear();
            var runs = entries.Where(e => e.RunId is not null && e.Category == AuditCategories.Change)
                .GroupBy(e => e.RunId)
                .Select(g =>
                {
                    var ordered = g.OrderBy(e => e.TimestampUtc).ToList();
                    var start = ordered.First();
                    var end = ordered.LastOrDefault(e => e.Action is "Execution completed" or "Dry run completed");
                    var outcome = end?.Outcome ?? AuditOutcome.Started;
                    return new RecentOperationItem(start.TimestampUtc, start.Message, start.Target,
                        end is null ? "Incomplete" : outcome.ToString(),
                        outcome switch
                        {
                            AuditOutcome.Succeeded => StatusKind.Success,
                            AuditOutcome.DryRun => StatusKind.Info,
                            AuditOutcome.Warning => StatusKind.Warning,
                            AuditOutcome.Failed => StatusKind.Critical,
                            _ => StatusKind.Warning,
                        },
                        start.Administrator);
                });

            var verifications = entries.Where(e => e.Category == AuditCategories.Verification)
                .Select(e => new RecentOperationItem(e.TimestampUtc, "Authorization test: " + e.Message, e.Target, e.Outcome.ToString(),
                    e.Outcome == AuditOutcome.Succeeded ? StatusKind.Success : e.Outcome == AuditOutcome.Failed ? StatusKind.Critical : StatusKind.Warning, e.Administrator));

            foreach (var item in runs.Concat(verifications).OrderByDescending(r => r.Timestamp).Take(8))
                RecentOperations.Add(item);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Recent operations could not be read");
        }
    }
}
