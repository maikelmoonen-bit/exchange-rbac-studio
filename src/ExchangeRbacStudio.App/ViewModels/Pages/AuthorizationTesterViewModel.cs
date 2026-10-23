using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.App.ViewModels.Shared;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Authorization;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Inventory;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Authorization;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Permissions;
using ExchangeRbacStudio.Domain.Security;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.ViewModels.Pages;

public sealed record ApplicationChoice(ExchangeServicePrincipal Pointer, IReadOnlyList<string> AssignedRoles)
{
    public string DisplayName => Pointer.DisplayName;
    public string Detail => $"{Pointer.AppId} · {AssignedRoles.Count} role(s)";
}

public sealed partial class RoleChoice(string role, bool isSelected) : ObservableObject
{
    public string Role { get; } = role;

    [ObservableProperty] private bool _isSelected = isSelected;
}

public sealed record TestOutcomeItem(RoleAuthorizationOutcome Outcome)
{
    public string Mailbox => Outcome.Mailbox;
    public string Expectation => Outcome.Expectation == TestExpectation.InScope ? "Should be in scope" : "Should be out of scope";
    public string Role => Outcome.RoleName;
    public string ExchangeResult => Outcome.ExchangeResult switch
    {
        InScopeResult.InScope => "In scope",
        InScopeResult.NotInScope => "Not in scope",
        InScopeResult.NotRun => "Not run",
        _ => Outcome.RoleAssigned ? "Unknown" : "Role not assigned",
    };
    public string EntraOverride => Outcome.EntraTenantWideGrant ? "Tenant-wide grant" : "—";
    public string Verdict => Outcome.Verdict.ToString();
    public StatusKind Kind => StatusMapper.From(Outcome.Verdict);
    public string Explanation => Outcome.Explanation;
}

/// <summary>Runs Test-ServicePrincipalAuthorization for chosen positive and negative mailboxes and interprets each role separately.</summary>
public sealed partial class AuthorizationTesterViewModel : PageViewModel
{
    private readonly RbacInventoryService _inventory;
    private readonly AuthorizationTestService _tests;
    private readonly IEntraDirectory _entra;

    public AuthorizationTesterViewModel(ConnectionManager connections, IUiDispatcher dispatcher, RbacInventoryService inventory, AuthorizationTestService tests,
        IEntraDirectory entra, Func<MailboxPickerViewModel> pickerFactory, ILogger<AuthorizationTesterViewModel> logger) : base(connections, dispatcher, logger)
    {
        _inventory = inventory;
        _tests = tests;
        _entra = entra;
        PositivePicker = pickerFactory();
        NegativePicker = pickerFactory();
        PositivePicker.SetAdditionalRule(r => NegativePicker.SelectedRecipients.Any(n => n.IdentityKey == r.IdentityKey) ? "Already selected as an out-of-scope (negative) target." : null);
        NegativePicker.SetAdditionalRule(r => PositivePicker.SelectedRecipients.Any(p => p.IdentityKey == r.IdentityKey) ? "Already selected as an in-scope (positive) target." : null);
        PositivePicker.SelectionChanged += (_, _) => RunTestsCommand.NotifyCanExecuteChanged();
        NegativePicker.SelectionChanged += (_, _) => RunTestsCommand.NotifyCanExecuteChanged();
    }

    public override string Title => "Authorization Tester";

    public override string Subtitle => "Evaluate which mailboxes an application can access through Exchange RBAC, per role.";

    public ObservableCollection<ApplicationChoice> Applications { get; } = [];

    public ObservableCollection<RoleChoice> Roles { get; } = [];

    public ObservableCollection<TestOutcomeItem> Results { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    public ObservableCollection<string> Errors { get; } = [];

    public MailboxPickerViewModel PositivePicker { get; }

    public MailboxPickerViewModel NegativePicker { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunTestsCommand))]
    private ApplicationChoice? _selectedApplication;

    [ObservableProperty] private string? _overallLabel;
    [ObservableProperty] private StatusKind _overallKind;
    [ObservableProperty] private string? _entraNote;

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!IsExchangeConnected) return;
        await RunAsync(async ct =>
        {
            var inventory = _inventory.Current ?? await _inventory.RefreshAsync(IsGraphConnected, null, ct);
            LoadApplications(inventory);
        }, "Loading applications…", "Load applications");

        if (parameter is Guid objectId)
            SelectedApplication = Applications.FirstOrDefault(a => a.Pointer.ObjectId == objectId) ?? SelectedApplication;
    }

    partial void OnSelectedApplicationChanged(ApplicationChoice? value)
    {
        Roles.Clear();
        Results.Clear();
        OverallLabel = null;
        if (value is null) return;
        foreach (var role in value.AssignedRoles.Distinct().Order()) Roles.Add(new RoleChoice(role, true));
    }

    private bool CanRun() => SelectedApplication is not null && PositivePicker.Selected.Count + NegativePicker.Selected.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task RunTests() => RunAsync(async ct =>
    {
        var app = SelectedApplication!;
        var roles = Roles.Where(r => r.IsSelected).Select(r => r.Role).ToList();
        if (roles.Count == 0) throw new StudioOperationException(FailureKind.InvalidInput, "Select at least one role to evaluate.");

        var tenantWide = MailboxCapability.None;
        var verified = false;
        EntraNote = null;
        if (IsGraphConnected && app.Pointer.ObjectId is { } id)
        {
            try
            {
                var grants = await _entra.GetAppPermissionGrantsAsync(id, ct);
                var analysis = EntraGrantAnalyzer.Analyze(grants, ApplicationRoleCatalog.CapabilitiesOf(roles));
                tenantWide = analysis.TenantWideCapabilities;
                verified = true;
                if (analysis.UnderminesRequestedScope)
                    EntraNote = "Tenant-wide Microsoft Entra ID permissions overlap these roles: " + string.Join(", ", analysis.UnderminingGrants.Select(g => g.PermissionValue)) + ". Negative tests will fail regardless of Exchange RBAC.";
            }
            catch (StudioOperationException ex) when (!ex.IsSessionProblem)
            {
                EntraNote = "Microsoft Entra ID permissions could not be read: " + ex.Message;
            }
        }
        else
        {
            EntraNote = "Microsoft Entra ID is not connected; tenant-wide permissions are not evaluated.";
        }

        Results.Clear();
        Errors.Clear();
        var progress = new Progress<MailboxTestResult>(r =>
        {
            foreach (var outcome in r.Outcomes) Results.Add(new TestOutcomeItem(outcome));
            if (r.Error is not null) Errors.Add($"{r.Mailbox.Label}: {r.Error}");
        });

        var report = await _tests.RunAsync(new AuthorizationTestRequest(app.Pointer.ObjectId ?? Guid.Empty, app.DisplayName,
            PositivePicker.SelectedRecipients, NegativePicker.SelectedRecipients, roles, tenantWide, verified), progress, ct);

        OverallLabel = $"{report.OverallVerdict}: {report.Passed} passed, {report.Failed} failed, {report.Inconclusive} inconclusive";
        OverallKind = StatusMapper.From(report.OverallVerdict);
        Notes.Clear();
        foreach (var note in report.Notes) Notes.Add(note);
    }, "Running Test-ServicePrincipalAuthorization…", "Authorization test");

    public override async Task RefreshAsync()
    {
        if (!IsExchangeConnected) return;
        await RunAsync(async ct => LoadApplications(await _inventory.RefreshAsync(IsGraphConnected, null, ct)), "Loading applications…", "Load applications");
    }

    protected override void OnTenantSessionChanged(SessionSnapshot previous, SessionSnapshot current)
    {
        if (previous.SessionId != current.SessionId || !current.IsExchangeConnected)
        {
            Applications.Clear();
            Roles.Clear();
            Results.Clear();
            PositivePicker.Selected.Clear();
            NegativePicker.Selected.Clear();
            PositivePicker.Results.Clear();
            NegativePicker.Results.Clear();
            OverallLabel = null;
        }
    }

    private void LoadApplications(RbacInventory inventory)
    {
        var selected = SelectedApplication?.Pointer.ObjectId;
        Applications.Clear();
        foreach (var pointer in inventory.ServicePrincipals.OrderBy(p => p.DisplayName))
            Applications.Add(new ApplicationChoice(pointer, inventory.AssignmentsFor(pointer).Where(a => a.Enabled).Select(a => a.Role).ToList()));
        SelectedApplication = Applications.FirstOrDefault(a => a.Pointer.ObjectId == selected);
    }
}
