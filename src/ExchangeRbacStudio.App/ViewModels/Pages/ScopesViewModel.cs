using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.App.ViewModels.Dialogs;
using ExchangeRbacStudio.Application.Applications;
using ExchangeRbacStudio.Application.Configuration;
using ExchangeRbacStudio.Application.Inventory;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Scoping;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.ViewModels.Pages;

public sealed record ScopeRow(
    ManagementScope Scope,
    FilterAnalysis Analysis,
    IReadOnlyList<ManagementRoleAssignment> Assignments,
    string Applications)
{
    public string Name => Scope.Name;
    public string Filter => Scope.RecipientFilter ?? "(no filter)";
    public int AssignmentCount => Assignments.Count;
    public bool IsApplicationScope => Assignments.Count > 0;
    public string RiskLabel => Analysis.OverallRisk switch
    {
        FilterRisk.ExactIdentity => "Exact identity",
        FilterRisk.Low => "Low risk",
        FilterRisk.Moderate => "Review",
        FilterRisk.High => "Broad",
        _ => "Unknown",
    };
    public StatusKind RiskKind => Analysis.OverallRisk switch
    {
        FilterRisk.ExactIdentity or FilterRisk.Low => StatusKind.Success,
        FilterRisk.Moderate => StatusKind.Warning,
        _ => StatusKind.Critical,
    };
}

public sealed record RecipientItem(string DisplayName, string? Address, string? Type);

/// <summary>Management scopes, their filters, matching recipients, linked assignments and potentially unsafe conditions.</summary>
public sealed partial class ScopesViewModel : PageViewModel
{
    private readonly RbacInventoryService _inventory;
    private readonly ApplicationProfileService _profiles;
    private readonly ManagementPlanService _plans;
    private readonly IDialogService _dialogs;

    public ScopesViewModel(ConnectionManager connections, IUiDispatcher dispatcher, RbacInventoryService inventory, ApplicationProfileService profiles,
        ManagementPlanService plans, IDialogService dialogs, ILogger<ScopesViewModel> logger) : base(connections, dispatcher, logger)
    {
        _inventory = inventory;
        _profiles = profiles;
        _plans = plans;
        _dialogs = dialogs;
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = Matches;
        RowsView.SortDescriptions.Add(new SortDescription(nameof(ScopeRow.Name), ListSortDirection.Ascending));
        inventory.Changed += (_, current) => dispatcher.Post(() => { if (current is not null) Apply(current); });
    }

    public override string Title => "Management Scopes";

    public override string Subtitle => "Recipient scopes, their filters and the application role assignments that use them.";

    public ObservableCollection<ScopeRow> Rows { get; } = [];

    public ICollectionView RowsView { get; }

    public ObservableCollection<RecipientItem> Members { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _onlyApplicationScopes = true;
    [ObservableProperty] private ScopeRow? _selectedRow;
    [ObservableProperty] private string? _memberSummary;
    [ObservableProperty] private string? _summary;

    partial void OnSearchTextChanged(string value) => RowsView.Refresh();

    partial void OnOnlyApplicationScopesChanged(bool value) => RowsView.Refresh();

    partial void OnSelectedRowChanged(ScopeRow? value)
    {
        Members.Clear();
        MemberSummary = null;
    }

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
    }, "Loading scopes…", "Load scopes");

    [RelayCommand]
    private Task ResolveMembers() => RunAsync(async ct =>
    {
        if (SelectedRow is null) return;
        var membership = await _profiles.ResolveMembershipAsync(SelectedRow.Scope, ct);
        Members.Clear();
        foreach (var member in membership.Members ?? []) Members.Add(new RecipientItem(member.DisplayName, member.PrimarySmtpAddress, member.RecipientTypeDetails));
        MemberSummary = membership.Error is not null ? "Membership could not be resolved: " + membership.Error
            : membership.Truncated ? $"At least {Members.Count} recipients match (preview limit reached)."
            : $"{Members.Count} recipient(s) match the filter (Get-Recipient -RecipientPreviewFilter)."
              + (string.IsNullOrWhiteSpace(SelectedRow.Scope.RecipientRoot) ? string.Empty : " The recipient root is not applied to this preview, so the effective scope may be smaller.");
    }, "Resolving matching recipients…", "Resolve scope members");

    [RelayCommand]
    private async Task RemoveScopeAsync()
    {
        if (SelectedRow is null) return;
        var scope = SelectedRow.Scope;
        PreparedPlan? prepared = null;
        await RunAsync(async ct => prepared = await _plans.RemoveScopeAsync(scope.Name, ct), "Checking which role assignments use this scope…", "Prepare scope removal");
        if (prepared is null) return;
        var result = _dialogs.ShowChangeReview(new ChangeReviewRequest(prepared.Plan, prepared.Replan, "Remove management scope",
            "A scope can only be removed when no role assignment (application or administrator) references it.", scope.Name));
        if (result?.ChangesMade == true) await RefreshAsync();
    }

    protected override void OnTenantSessionChanged(SessionSnapshot previous, SessionSnapshot current)
    {
        if (previous.SessionId != current.SessionId || !current.IsExchangeConnected)
        {
            Rows.Clear();
            Members.Clear();
            Summary = null;
        }
    }

    private void Apply(RbacInventory inventory)
    {
        var selected = SelectedRow?.Name;
        Rows.Clear();
        foreach (var scope in inventory.Scopes)
        {
            var assignments = inventory.AssignmentsReferencing(scope);
            var apps = string.Join(", ", assignments.Select(a => inventory.ServicePrincipalFor(a)?.DisplayName ?? a.RoleAssigneeName).Distinct().Order());
            Rows.Add(new ScopeRow(scope, RecipientFilterAnalyzer.Analyze(scope.RecipientFilter), assignments, apps));
        }

        Summary = $"{inventory.Scopes.Count} scope(s), {Rows.Count(r => r.IsApplicationScope)} used by applications · retrieved {inventory.RetrievedUtc.ToLocalTime():g}";
        RowsView.Refresh();
        SelectedRow = Rows.FirstOrDefault(r => r.Name == selected);
    }

    private bool Matches(object item)
    {
        if (item is not ScopeRow row) return false;
        if (OnlyApplicationScopes && !row.IsApplicationScope) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var term = SearchText.Trim();
        return row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.Filter.Contains(term, StringComparison.OrdinalIgnoreCase)
            || row.Applications.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
