using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.Application.Inventory;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Permissions;
using ExchangeRbacStudio.Domain.Scoping;
using ExchangeRbacStudio.Domain.Security;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.ViewModels.Pages;

public sealed record ApplicationDetailRequest(Guid? ServicePrincipalObjectId, Guid? AppId);

public sealed record ApplicationRow(
    ExchangeServicePrincipal Pointer,
    string DisplayName,
    string AppId,
    string ObjectId,
    int AssignmentCount,
    string Roles,
    string Scopes,
    string RiskLabel,
    StatusKind RiskKind,
    string RiskDetail,
    bool HasWarnings);

/// <summary>Applications known to Exchange RBAC (service principal pointers) with their assignments and risk summary.</summary>
public sealed partial class ApplicationsViewModel : PageViewModel
{
    private readonly RbacInventoryService _inventory;
    private readonly NavigationService _navigation;

    public ApplicationsViewModel(ConnectionManager connections, IUiDispatcher dispatcher, RbacInventoryService inventory, NavigationService navigation,
        ILogger<ApplicationsViewModel> logger) : base(connections, dispatcher, logger)
    {
        _inventory = inventory;
        _navigation = navigation;
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = Matches;
        RowsView.SortDescriptions.Add(new SortDescription(nameof(ApplicationRow.DisplayName), ListSortDirection.Ascending));
        inventory.Changed += (_, current) => dispatcher.Post(() => { if (current is not null) Apply(current); else IsStale = Rows.Count > 0; });
    }

    public override string Title => "Applications";

    public override string Subtitle => "Applications with an Exchange Online service principal pointer and their application role assignments.";

    public ObservableCollection<ApplicationRow> Rows { get; } = [];

    public ICollectionView RowsView { get; }

    public IReadOnlyList<string> FilterModes { get; } = ["All applications", "With warnings", "Without assignments"];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _filterMode = "All applications";
    [ObservableProperty] private ApplicationRow? _selectedRow;
    [ObservableProperty] private bool _isStale;
    [ObservableProperty] private string? _summary;

    partial void OnSearchTextChanged(string value) => RowsView.Refresh();

    partial void OnFilterModeChanged(string value) => RowsView.Refresh();

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        if (_inventory.Current is { } current) Apply(current);
        else if (IsExchangeConnected) await RefreshAsync();
    }

    public override Task RefreshAsync() => RefreshCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task Refresh() => RunAsync(async ct =>
    {
        var progress = new Progress<string>(m => BusyMessage = m);
        Apply(await _inventory.RefreshAsync(IsGraphConnected, progress, ct));
    }, "Loading applications…", "Load applications");

    [RelayCommand]
    private void Open(ApplicationRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return;
        _navigation.Navigate(PageKey.ApplicationDetail, new ApplicationDetailRequest(row.Pointer.ObjectId, row.Pointer.AppId));
    }

    [RelayCommand]
    private void ConfigureAccess() => _navigation.Navigate(PageKey.ConfigureAccess);

    protected override void OnTenantSessionChanged(SessionSnapshot previous, SessionSnapshot current)
    {
        if (previous.SessionId != current.SessionId || !current.IsExchangeConnected)
        {
            Rows.Clear();
            Summary = null;
        }
    }

    private void Apply(RbacInventory inventory)
    {
        IsStale = false;
        Rows.Clear();
        foreach (var pointer in inventory.ServicePrincipals)
        {
            var assignments = inventory.AssignmentsFor(pointer);
            var (label, kind, detail) = Assess(inventory, pointer, assignments);
            Rows.Add(new ApplicationRow(
                pointer,
                pointer.DisplayName,
                pointer.AppId?.ToString() ?? "—",
                pointer.ObjectId?.ToString() ?? "—",
                assignments.Count,
                string.Join(", ", assignments.Select(a => a.Role.Replace("Application ", string.Empty, StringComparison.Ordinal)).Distinct().Order()),
                string.Join(", ", assignments.Select(a => a.ScopeKind == AssignmentScopeKind.Organization ? "Organization" : a.ScopeReference ?? "?").Distinct().Order()),
                label, kind, detail, kind is StatusKind.Critical or StatusKind.Warning));
        }

        Summary = $"{Rows.Count} application(s) · {Rows.Count(r => r.RiskKind == StatusKind.Critical)} with critical findings · retrieved {inventory.RetrievedUtc.ToLocalTime():g}";
        RowsView.Refresh();
    }

    private static (string Label, StatusKind Kind, string Detail) Assess(RbacInventory inventory, ExchangeServicePrincipal pointer, IReadOnlyList<ManagementRoleAssignment> assignments)
    {
        if (assignments.Count == 0) return ("No assignments", StatusKind.Neutral, "The pointer exists but no application roles are assigned.");
        if (assignments.Any(a => a.Enabled && a.ScopeKind == AssignmentScopeKind.Organization))
            return ("Organization-wide", StatusKind.Critical, "At least one application role applies to every mailbox.");

        if (inventory.Entra is { } entra && pointer.ObjectId is { } id && entra.Grants.TryGetValue(id, out var grants))
        {
            var analysis = EntraGrantAnalyzer.Analyze(grants, ApplicationRoleCatalog.CapabilitiesOf(assignments.Select(a => a.Role)));
            if (analysis.UnderminesRequestedScope)
                return ("Bypassed by Entra ID", StatusKind.Critical, "A tenant-wide Entra ID permission overlaps the scoped roles: " + string.Join(", ", analysis.UnderminingGrants.Select(g => g.PermissionValue)));
            if (analysis.Findings.Any(f => f.UnderminesScoping))
                return ("Privileged Entra permission", StatusKind.Critical, string.Join("; ", analysis.Findings.Where(f => f.UnderminesScoping).Select(f => f.Title)));
        }

        var risky = assignments.Select(inventory.ScopeFor).Where(s => s is not null)
            .Select(s => RecipientFilterAnalyzer.Analyze(s!.RecipientFilter)).Where(a => a.OverallRisk is FilterRisk.High or FilterRisk.Moderate or FilterRisk.Unknown).ToList();
        if (risky.Count > 0)
            return ("Broad scope filter", StatusKind.Warning, string.Join("; ", risky.SelectMany(r => r.Flags).Select(f => f.Description).Distinct()));

        if (inventory.Entra is null)
            return ("Scoped (Entra not checked)", StatusKind.Info, "Exchange scopes look restricted; Microsoft Entra ID permissions were not analyzed.");

        return ("Scoped", StatusKind.Success, "Assignments use restricted scopes and no overlapping tenant-wide Entra ID permission was found.");
    }

    private bool Matches(object item)
    {
        if (item is not ApplicationRow row) return false;
        var modeOk = FilterMode switch
        {
            "With warnings" => row.HasWarnings,
            "Without assignments" => row.AssignmentCount == 0,
            _ => true,
        };
        if (!modeOk) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var term = SearchText.Trim();
        return row.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.AppId.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.ObjectId.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.Roles.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.Scopes.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
