using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.App.ViewModels.Dialogs;
using ExchangeRbacStudio.App.ViewModels.Wizard;
using ExchangeRbacStudio.Application.Applications;
using ExchangeRbacStudio.Application.Authorization;
using ExchangeRbacStudio.Application.Configuration;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Authorization;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Identity;
using ExchangeRbacStudio.Domain.Permissions;
using ExchangeRbacStudio.Domain.Scoping;
using ExchangeRbacStudio.Domain.Security;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.ViewModels.Pages;

public sealed record GrantItem(string Permission, string Resource, string Reach, StatusKind Kind, string? Explanation);

public sealed partial class AssignmentItem(ManagementRoleAssignment assignment, ApplicationDetailViewModel owner)
{
    public ManagementRoleAssignment Assignment { get; } = assignment;
    public string Name => Assignment.Name;
    public string Role => Assignment.Role;
    public string Scope => Assignment.ScopeKind == AssignmentScopeKind.Organization ? "Organization (all mailboxes)" : Assignment.ScopeReference ?? "Unknown";
    public string ScopeKindLabel => Assignment.ScopeKind.ToString();
    public StatusKind Kind => Assignment.ScopeKind == AssignmentScopeKind.Organization ? StatusKind.Critical : Assignment.Enabled ? StatusKind.Success : StatusKind.Neutral;
    public string Status => Assignment.Enabled ? "Enabled" : "Disabled";

    [RelayCommand]
    private Task Remove() => owner.RemoveAssignmentAsync(this);
}

public sealed record ScopeItem(ScopeMembership Membership)
{
    public string Name => Membership.Scope.Name;
    public string Filter => Membership.Scope.RecipientFilter ?? "(no filter)";
    public string MemberSummary => Membership.Error is not null ? "Not retrievable: " + Membership.Error
        : Membership.Members is null ? "Not retrievable"
        : Membership.Truncated ? $"{Membership.Members.Count}+ recipients (preview limit reached)"
        : $"{Membership.Members.Count} recipient(s)" + (Membership.IsReliable ? string.Empty : " (recipient root not applied to preview)");
    public string RiskLabel => Membership.Analysis.OverallRisk switch
    {
        FilterRisk.ExactIdentity => "Exact identity",
        FilterRisk.Low => "Low risk",
        FilterRisk.Moderate => "Review",
        FilterRisk.High => "Broad",
        _ => "Unknown",
    };
    public StatusKind RiskKind => Membership.Analysis.OverallRisk switch
    {
        FilterRisk.ExactIdentity or FilterRisk.Low => StatusKind.Success,
        FilterRisk.Moderate => StatusKind.Warning,
        _ => StatusKind.Critical,
    };
}

public sealed record MatrixCellItem(string Label, StatusKind Kind, string ToolTip);

public sealed record MatrixRowItem(string Mailbox, bool IsAllOthers, IReadOnlyList<MatrixCellItem> Cells);

public sealed record SecurityFindingItem(SecurityFinding Finding)
{
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public string? Remediation => Finding.Remediation;
    public StatusKind Kind => StatusMapper.From(Finding.Severity);
    public string SeverityLabel => Finding.Severity.ToString();
}

/// <summary>Everything about one application's mailbox access, distinguishing Exchange RBAC from Microsoft Entra ID grants.</summary>
public sealed partial class ApplicationDetailViewModel : PageViewModel
{
    private readonly ApplicationProfileService _profiles;
    private readonly AuthorizationTestService _tests;
    private readonly ManagementPlanService _plans;
    private readonly NavigationService _navigation;
    private readonly IDialogService _dialogs;
    private ApplicationDetailRequest? _request;

    public ApplicationDetailViewModel(ConnectionManager connections, IUiDispatcher dispatcher, ApplicationProfileService profiles, AuthorizationTestService tests,
        ManagementPlanService plans, NavigationService navigation, IDialogService dialogs, ILogger<ApplicationDetailViewModel> logger)
        : base(connections, dispatcher, logger)
    {
        _profiles = profiles;
        _tests = tests;
        _plans = plans;
        _navigation = navigation;
        _dialogs = dialogs;
    }

    public override bool KeepAlive => false;

    public override string Title => Profile?.DisplayName ?? "Application";

    public override string Subtitle => "Effective mailbox access through Exchange RBAC and Microsoft Entra ID.";

    public ObservableCollection<GrantItem> Grants { get; } = [];

    public ObservableCollection<AssignmentItem> Assignments { get; } = [];

    public ObservableCollection<ScopeItem> Scopes { get; } = [];

    public ObservableCollection<string> AuthorizedMailboxes { get; } = [];

    public ObservableCollection<SecurityFindingItem> Findings { get; } = [];

    public ObservableCollection<string> MatrixHeaders { get; } = [];

    public ObservableCollection<MatrixRowItem> MatrixRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private ApplicationProfile? _profile;

    [ObservableProperty] private string? _appId;
    [ObservableProperty] private string? _objectId;
    [ObservableProperty] private string _pointerLabel = string.Empty;
    [ObservableProperty] private StatusKind _pointerKind;
    [ObservableProperty] private string? _entraStatus;
    [ObservableProperty] private string? _grantsError;
    [ObservableProperty] private int _matrixColumns = 1;
    [ObservableProperty] private string? _matrixNote;
    [ObservableProperty] private string? _testSummary;
    [ObservableProperty] private StatusKind _testSummaryKind;

    protected override Task OnNavigatedToAsync(object? parameter)
    {
        _request = parameter as ApplicationDetailRequest;
        return RefreshAsync();
    }

    public override Task RefreshAsync() => RefreshCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task Refresh() => RunAsync(async ct =>
    {
        if (_request is null) return;
        Apply(await _profiles.LoadAsync(_request.ServicePrincipalObjectId, _request.AppId, ct));
    }, "Loading application…", "Load application");

    /// <summary>Runs Test-ServicePrincipalAuthorization for every mailbox in the application's scopes to show observed access.</summary>
    [RelayCommand]
    private Task RunTests() => RunAsync(async ct =>
    {
        if (Profile?.ObjectId is not { } objectId) return;
        var members = Profile.Scopes.SelectMany(s => s.Members ?? []).DistinctBy(m => m.IdentityKey)
            .Where(m => RecipientSupport.Validate(m).IsSupported).Take(25).ToList();
        var roles = Profile.Assignments.Where(a => a.Enabled).Select(a => a.Role).Distinct().ToList();
        if (members.Count == 0 || roles.Count == 0)
        {
            TestSummary = "No scoped mailboxes or roles to test.";
            TestSummaryKind = StatusKind.Neutral;
            return;
        }

        var tenantWide = Profile.GrantAnalysis?.TenantWideCapabilities ?? MailboxCapability.None;
        var report = await _tests.RunAsync(new AuthorizationTestRequest(objectId, Profile.DisplayName, members, [], roles, tenantWide, Profile.GrantAnalysis is not null), null, ct);
        Apply(ApplicationProfileService.WithTestResults(Profile, report));
        TestSummary = $"Tested {report.Results.Count} mailbox(es): {report.Passed} passed, {report.Failed} failed, {report.Inconclusive} inconclusive. Matrix cells now show observed results.";
        TestSummaryKind = StatusMapper.From(report.OverallVerdict);
    }, "Testing authorization…", "Authorization test");

    [RelayCommand]
    private void ModifyAccess()
    {
        if (Profile?.Entra is not { } sp) return;
        var exact = Profile.Scopes.FirstOrDefault(s => s.Analysis.IsExactIdentityFilter && s.Members is not null && !s.Truncated);
        if (exact is null)
        {
            _navigation.Navigate(PageKey.ConfigureAccess, new WizardStartOptions(sp));
            return;
        }

        var roles = Profile.Assignments.Where(a => exact.Scope.IsReferencedBy(a.ScopeReference)).Select(a => a.Role).Distinct().ToList();
        _navigation.Navigate(PageKey.ConfigureAccess, new WizardStartOptions(sp, exact.Members, roles, exact.Scope.Name));
    }

    [RelayCommand]
    private void AddAccess()
    {
        if (Profile?.Entra is { } sp) _navigation.Navigate(PageKey.ConfigureAccess, new WizardStartOptions(sp));
    }

    [RelayCommand]
    private void TestAuthorization()
    {
        if (Profile?.ObjectId is { } id) _navigation.Navigate(PageKey.AuthorizationTester, id);
    }

    [RelayCommand]
    private void Back() => _navigation.Navigate(PageKey.Applications);

    [RelayCommand]
    private async Task RemoveConfigurationAsync()
    {
        if (Profile?.ObjectId is not { } objectId || Profile.Pointer is null) return;
        if (!_dialogs.Confirm("Remove application configuration",
                $"Plan the removal of all Exchange application role assignments and the service principal pointer of '{Profile.DisplayName}'? Scopes are removed only if no other assignment uses them. You will review the plan before anything is changed.",
                "Prepare removal plan", destructive: true))
            return;

        PreparedPlan? prepared = null;
        await RunAsync(async ct => prepared = await _plans.RemoveApplicationAsync(objectId, removePointer: true, removeUnusedScopes: true, ct), "Checking dependencies…", "Prepare removal");
        if (prepared is null) return;
        var result = _dialogs.ShowChangeReview(new ChangeReviewRequest(prepared.Plan, prepared.Replan, "Remove application configuration",
            "Removes the application's Exchange RBAC configuration. The Enterprise Application in Microsoft Entra ID and its Entra permissions are not changed.",
            Profile.DisplayName));
        if (result?.ChangesMade == true) await RefreshAsync();
    }

    internal async Task RemoveAssignmentAsync(AssignmentItem item)
    {
        PreparedPlan? prepared = null;
        await RunAsync(async ct => prepared = await _plans.RemoveAssignmentAsync(item.Assignment, ct), "Preparing…", "Prepare removal");
        if (prepared is null) return;
        var result = _dialogs.ShowChangeReview(new ChangeReviewRequest(prepared.Plan, prepared.Replan, "Remove role assignment",
            $"Removes '{item.Role}' from the application on '{item.Scope}'. The management scope itself is not removed.", item.Name));
        if (result?.ChangesMade == true) await RefreshAsync();
    }

    protected override void OnTenantSessionChanged(SessionSnapshot previous, SessionSnapshot current)
    {
        if (previous.SessionId != current.SessionId) _navigation.Navigate(PageKey.Applications);
    }

    private void Apply(ApplicationProfile profile)
    {
        Profile = profile;
        AppId = profile.AppId?.ToString();
        ObjectId = profile.ObjectId?.ToString();
        (PointerLabel, PointerKind) = profile.PointerStatus switch
        {
            ExchangePointerStatus.Present => ("Exchange service principal: present", StatusKind.Success),
            ExchangePointerStatus.Missing => ("Exchange service principal: not created", StatusKind.Neutral),
            ExchangePointerStatus.Mismatch => ("Exchange service principal: identifier mismatch", StatusKind.Critical),
            _ => ("Exchange service principal: unknown", StatusKind.Warning),
        };
        EntraStatus = profile.Entra is null ? profile.EntraError : $"{profile.Entra.ServicePrincipalType ?? "Service principal"}" + (profile.Entra.AccountEnabled == false ? " · disabled" : " · enabled");
        GrantsError = profile.GrantsError;

        Grants.Clear();
        foreach (var grant in profile.Grants.OrderBy(g => g.PermissionValue))
        {
            var definition = EntraPermissionCatalog.TryGet(grant.ResourceAppId, grant.PermissionValue);
            var (reach, kind) = definition?.Reach switch
            {
                EntraGrantReach.TenantWideMailboxAccess => ("Tenant-wide mailbox access", StatusKind.Critical),
                EntraGrantReach.Administrative => ("Privileged", StatusKind.Critical),
                EntraGrantReach.RequiresExchangeMailboxPermission => ("Needs mailbox permission", StatusKind.Info),
                _ => ("Not mailbox-related", StatusKind.Neutral),
            };
            Grants.Add(new GrantItem(grant.PermissionValue ?? grant.AppRoleId.ToString(), grant.ResourceDisplayName, reach, kind, definition?.Explanation));
        }

        Assignments.Clear();
        foreach (var assignment in profile.Assignments.OrderBy(a => a.Role)) Assignments.Add(new AssignmentItem(assignment, this));

        Scopes.Clear();
        foreach (var scope in profile.Scopes) Scopes.Add(new ScopeItem(scope));

        AuthorizedMailboxes.Clear();
        foreach (var label in profile.Scopes.SelectMany(s => s.Members ?? []).Select(m => m.Label).Distinct().Order()) AuthorizedMailboxes.Add(label);

        Findings.Clear();
        foreach (var finding in profile.Findings) Findings.Add(new SecurityFindingItem(finding));

        MatrixHeaders.Clear();
        foreach (var permission in profile.Matrix.Permissions) MatrixHeaders.Add(permission);
        MatrixColumns = Math.Max(1, MatrixHeaders.Count);
        MatrixRows.Clear();
        foreach (var row in profile.Matrix.Rows)
        {
            MatrixRows.Add(new MatrixRowItem(row.Mailbox, row.IsAllOtherMailboxes, row.Cells.Select(c => new MatrixCellItem(
                c.Label,
                c.Sources.Contains(AccessSource.EntraTenantWide) || c.Sources.Contains(AccessSource.RbacOrganizationWide) ? StatusKind.Critical
                    : c.Sources.Contains(AccessSource.RbacObserved) ? StatusKind.Success
                    : c.Sources.Contains(AccessSource.RbacConfigured) ? StatusKind.Info
                    : StatusKind.Neutral,
                string.IsNullOrWhiteSpace(c.Detail) ? "No access through Exchange RBAC or a tenant-wide Entra ID permission." : c.Detail)).ToList()));
        }

        MatrixNote = profile.GrantsError is not null
            ? "Microsoft Entra ID permissions could not be read, so tenant-wide access is not reflected in the matrix."
            : null;
        OnPropertyChanged(nameof(Title));
    }
}
