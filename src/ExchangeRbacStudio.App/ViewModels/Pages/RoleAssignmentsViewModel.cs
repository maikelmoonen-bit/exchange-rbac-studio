using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.App.ViewModels.Dialogs;
using ExchangeRbacStudio.Application.Configuration;
using ExchangeRbacStudio.Application.Inventory;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Exchange;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.ViewModels.Pages;

public sealed record AssignmentRow(ManagementRoleAssignment Assignment, string Application, ExchangeServicePrincipal? Pointer)
{
    public string Name => Assignment.Name;
    public string Role => Assignment.Role;
    public string Scope => Assignment.ScopeKind == AssignmentScopeKind.Organization ? "Organization (all mailboxes)" : Assignment.ScopeReference ?? "Unknown";
    public string ScopeKind => Assignment.ScopeKind switch
    {
        AssignmentScopeKind.ManagementScope => "Management scope",
        AssignmentScopeKind.AdministrativeUnit => "Administrative unit",
        AssignmentScopeKind.Group => "Group",
        AssignmentScopeKind.Organization => "Organization",
        _ => "Unknown",
    };
    public StatusKind ScopeKindStatus => Assignment.ScopeKind == AssignmentScopeKind.Organization ? StatusKind.Critical
        : Assignment.ScopeKind == AssignmentScopeKind.Unknown ? StatusKind.Warning : StatusKind.Info;
    public string Status => Assignment.Enabled ? "Enabled" : "Disabled";
    public StatusKind StatusKind => Assignment.Enabled ? StatusKind.Success : StatusKind.Neutral;
    public string Identity => Assignment.Identity;
    public string? Created => Assignment.WhenCreatedUtc?.ToLocalTime().ToString("g");
}

/// <summary>Existing application role assignments with controlled creation, rescoping and removal.</summary>
public sealed partial class RoleAssignmentsViewModel : PageViewModel
{
    private readonly RbacInventoryService _inventory;
    private readonly ManagementPlanService _plans;
    private readonly NavigationService _navigation;
    private readonly IDialogService _dialogs;

    public RoleAssignmentsViewModel(ConnectionManager connections, IUiDispatcher dispatcher, RbacInventoryService inventory, ManagementPlanService plans,
        NavigationService navigation, IDialogService dialogs, ILogger<RoleAssignmentsViewModel> logger) : base(connections, dispatcher, logger)
    {
        _inventory = inventory;
        _plans = plans;
        _navigation = navigation;
        _dialogs = dialogs;
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = Matches;
        RowsView.SortDescriptions.Add(new SortDescription(nameof(AssignmentRow.Application), ListSortDirection.Ascending));
        RowsView.SortDescriptions.Add(new SortDescription(nameof(AssignmentRow.Role), ListSortDirection.Ascending));
        inventory.Changed += (_, current) => dispatcher.Post(() => { if (current is not null) Apply(current); });
    }

    public override string Title => "Role Assignments";

    public override string Subtitle => "Application role assignments to service principals, with their resource scopes.";

    public ObservableCollection<AssignmentRow> Rows { get; } = [];

    public ICollectionView RowsView { get; }

    public ObservableCollection<string> RoleFilters { get; } = ["All roles"];

    public ObservableCollection<string> TargetScopes { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _roleFilter = "All roles";
    [ObservableProperty] private AssignmentRow? _selectedRow;
    [ObservableProperty] private string? _targetScope;
    [ObservableProperty] private string? _summary;

    partial void OnSearchTextChanged(string value) => RowsView.Refresh();

    partial void OnRoleFilterChanged(string value) => RowsView.Refresh();

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is string search) SearchText = search;
        if (_inventory.Current is { } current) Apply(current);
        else if (IsExchangeConnected) await RefreshAsync();
    }

    public override Task RefreshAsync() => RefreshCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task Refresh() => RunAsync(async ct =>
    {
        var progress = new Progress<string>(m => BusyMessage = m);
        Apply(await _inventory.RefreshAsync(IsGraphConnected, progress, ct));
    }, "Loading role assignments…", "Load role assignments");

    [RelayCommand]
    private void NewAssignment() => _navigation.Navigate(PageKey.ConfigureAccess);

    [RelayCommand]
    private async Task ChangeScopeAsync()
    {
        if (SelectedRow is null || string.IsNullOrWhiteSpace(TargetScope)) return;
        var row = SelectedRow;
        PreparedPlan? prepared = null;
        await RunAsync(async ct => prepared = await _plans.ChangeAssignmentScopeAsync(row.Assignment, TargetScope, ct), "Preparing…", "Prepare rescoping");
        if (prepared is null) return;
        var result = _dialogs.ShowChangeReview(new ChangeReviewRequest(prepared.Plan, prepared.Replan, "Change assignment scope",
            $"'{row.Role}' for {row.Application} will apply to the recipients of '{TargetScope}' instead of '{row.Scope}'. Verify the new scope's membership first.", null));
        if (result?.ChangesMade == true) await RefreshAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (SelectedRow is null) return;
        var row = SelectedRow;
        PreparedPlan? prepared = null;
        await RunAsync(async ct => prepared = await _plans.RemoveAssignmentAsync(row.Assignment, ct), "Preparing…", "Prepare removal");
        if (prepared is null) return;
        var result = _dialogs.ShowChangeReview(new ChangeReviewRequest(prepared.Plan, prepared.Replan, "Remove role assignment",
            $"{row.Application} loses '{row.Role}' on '{row.Scope}'. The management scope is not removed.", row.Name));
        if (result?.ChangesMade == true) await RefreshAsync();
    }

    [RelayCommand]
    private void OpenApplication()
    {
        if (SelectedRow?.Pointer is { } pointer)
            _navigation.Navigate(PageKey.ApplicationDetail, new ApplicationDetailRequest(pointer.ObjectId, pointer.AppId));
    }

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
        Rows.Clear();
        foreach (var assignment in inventory.Assignments)
        {
            var pointer = inventory.ServicePrincipalFor(assignment);
            Rows.Add(new AssignmentRow(assignment, pointer?.DisplayName ?? assignment.RoleAssigneeName ?? assignment.RoleAssignee ?? "Unknown", pointer));
        }

        var currentFilter = RoleFilter;
        RoleFilters.Clear();
        RoleFilters.Add("All roles");
        foreach (var role in inventory.Assignments.Select(a => a.Role).Distinct().Order()) RoleFilters.Add(role);
        RoleFilter = RoleFilters.Contains(currentFilter) ? currentFilter : "All roles";

        TargetScopes.Clear();
        foreach (var scope in inventory.Scopes.Where(s => s.IsRecipientScope).Select(s => s.Name).Order()) TargetScopes.Add(scope);

        Summary = $"{Rows.Count} application role assignment(s) · {Rows.Count(r => r.Assignment.ScopeKind == AssignmentScopeKind.Organization)} organization-wide · retrieved {inventory.RetrievedUtc.ToLocalTime():g}";
        RowsView.Refresh();
    }

    private bool Matches(object item)
    {
        if (item is not AssignmentRow row) return false;
        if (RoleFilter != "All roles" && !row.Role.Equals(RoleFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var term = SearchText.Trim();
        return row.Application.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.Role.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.Scope.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.Name.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
