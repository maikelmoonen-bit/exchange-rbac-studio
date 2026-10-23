using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.App.ViewModels.Pages;
using ExchangeRbacStudio.App.ViewModels.Shared;
using ExchangeRbacStudio.Application.Applications;
using ExchangeRbacStudio.Application.Configuration;
using ExchangeRbacStudio.Application.Permissions;
using ExchangeRbacStudio.Application.Scoping;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Identity;
using ExchangeRbacStudio.Domain.Permissions;
using ExchangeRbacStudio.Domain.Scoping;
using ExchangeRbacStudio.Domain.Validation;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.ViewModels.Wizard;

public sealed record ApplicationResultItem(ApplicationSearchResult Result)
{
    public EntraServicePrincipal ServicePrincipal => Result.ServicePrincipal;
    public string DisplayName => ServicePrincipal.DisplayName;
    public string AppId => ServicePrincipal.AppId.ToString();
    public string ObjectId => ServicePrincipal.ObjectId.ToString();
    public string? MatchNote => Result.MatchNote;
    public string TypeLabel => ServicePrincipal.IsManagedIdentity ? "Managed identity"
        : ServicePrincipal.IsMicrosoftFirstParty ? "Microsoft application"
        : ServicePrincipal.IsEnterpriseApplication ? "Enterprise application"
        : ServicePrincipal.ServicePrincipalType ?? "Service principal";
    public string PointerLabel => Result.PointerStatus switch
    {
        ExchangePointerStatus.Present => "Exchange pointer exists",
        ExchangePointerStatus.Missing => "No Exchange pointer",
        ExchangePointerStatus.Mismatch => "Pointer mismatch",
        _ => "Pointer not checked",
    };
    public StatusKind PointerKind => Result.PointerStatus switch
    {
        ExchangePointerStatus.Present => StatusKind.Success,
        ExchangePointerStatus.Mismatch => StatusKind.Critical,
        _ => StatusKind.Neutral,
    };
}

/// <summary>Step 1: find an Enterprise Application and validate its identifiers against Microsoft Graph and Exchange.</summary>
public sealed partial class ApplicationStepViewModel(WizardServices services, WizardState state, ILogger logger) : WizardStepViewModel(services, state, logger)
{
    private CancellationTokenSource? _searchCts;
    private EntraServicePrincipal? _prefill;

    public override string Title => "Select application";

    public override string Description => "Search Enterprise Applications by display name, application (client) ID or service principal object ID.";

    public ObservableCollection<ApplicationResultItem> Results { get; } = [];

    public ObservableCollection<GrantItem> Grants { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _hasSearched;
    [ObservableProperty] private ApplicationResultItem? _selectedResult;
    [ObservableProperty] private ApplicationIdentityDetails? _details;
    [ObservableProperty] private string? _pointerLabel;
    [ObservableProperty] private StatusKind _pointerKind;
    [ObservableProperty] private string? _tenantLabel;

    public bool IsConnected => Services.Connections.Current.IsFullyConnected;

    public override bool CanAdvance => !IsBusy && IsConnected && Details?.IsValid == true;

    public void Prefill(EntraServicePrincipal servicePrincipal) => _prefill = servicePrincipal;

    public override async Task OnEnterAsync()
    {
        OnPropertyChanged(nameof(IsConnected));
        if (_prefill is not null && IsConnected)
        {
            var sp = _prefill;
            _prefill = null;
            var item = new ApplicationResultItem(new ApplicationSearchResult(sp, ExchangePointerStatus.Unknown, "Pre-selected from the application's existing configuration."));
            Results.Clear();
            Results.Add(item);
            SelectedResult = item;
            await Task.CompletedTask;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        if (value.Trim().Length < 2) return;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = DebouncedAsync(cts.Token);
    }

    private async Task DebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(400, token);
            await SearchCoreAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
    }

    [RelayCommand]
    private Task Search()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        return SearchCoreAsync(_searchCts.Token);
    }

    private Task SearchCoreAsync(CancellationToken token) => RunAsync(async ct =>
    {
        var results = await Services.Identity.SearchAsync(SearchText, ct);
        ct.ThrowIfCancellationRequested();
        Results.Clear();
        foreach (var result in results) Results.Add(new ApplicationResultItem(result));
        HasSearched = true;
    }, "Searching Microsoft Entra ID…", "Application search", token);

    partial void OnSelectedResultChanged(ApplicationResultItem? value)
    {
        Details = null;
        State.Application = null;
        State.InvalidateAfter(0);
        Grants.Clear();
        Notes.Clear();
        NotifyNavigationChanged();
        if (value is not null) _ = ValidateAsync(value.ServicePrincipal);
    }

    private Task ValidateAsync(EntraServicePrincipal sp) => RunAsync(async ct =>
    {
        var details = await Services.Identity.ValidateAsync(sp, ct);
        Details = details;
        State.Application = details.IsValid ? details : null;
        PointerLabel = details.PointerMessage;
        PointerKind = details.PointerStatus switch
        {
            ExchangePointerStatus.Present => StatusKind.Success,
            ExchangePointerStatus.Missing => StatusKind.Info,
            ExchangePointerStatus.Mismatch => StatusKind.Critical,
            _ => StatusKind.Warning,
        };
        TenantLabel = details.Tenant is { } t ? $"{t.DisplayName} ({t.TenantId})" : null;
        foreach (var note in details.Notes) Notes.Add(note);
        foreach (var grant in details.Grants.OrderBy(g => g.PermissionValue))
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

        NotifyNavigationChanged();
    }, "Validating identifiers with Microsoft Graph and Exchange Online…", "Validate application");
}

/// <summary>Step 2: select and validate target mailboxes.</summary>
public sealed partial class MailboxesStepViewModel : WizardStepViewModel
{
    private IReadOnlyList<ExchangeRecipient>? _prefill;

    public MailboxesStepViewModel(WizardServices services, WizardState state, ILogger logger) : base(services, state, logger)
    {
        Picker = services.PickerFactory();
        Picker.MaxItems = services.Settings.Current.MaxMailboxesPerScope;
        Picker.SelectionChanged += (_, _) =>
        {
            State.Mailboxes = Picker.SelectedRecipients;
            State.InvalidateAfter(1);
            NotifyNavigationChanged();
        };
    }

    public override string Title => "Select mailboxes";

    public override string Description => "Search by primary SMTP address, display name or alias, or paste a list. Only user and shared mailboxes are accepted.";

    public MailboxPickerViewModel Picker { get; }

    public int MaxItems => Picker.MaxItems;

    public override bool CanAdvance => !IsBusy && !Picker.IsBusy && Picker.Selected.Count > 0 && Picker.Selected.Count <= Picker.MaxItems;

    public void Prefill(IReadOnlyList<ExchangeRecipient> mailboxes) => _prefill = mailboxes;

    public override Task OnEnterAsync()
    {
        if (_prefill is not null)
        {
            Picker.SetSelection(_prefill.Where(m => RecipientSupport.Validate(m).IsSupported));
            _prefill = null;
        }

        NotifyNavigationChanged();
        return Task.CompletedTask;
    }
}

public sealed partial class RoleOptionItem : ObservableObject
{
    private readonly Action _changed;

    public RoleOptionItem(ApplicationRoleDefinition definition, bool selected, Action changed)
    {
        Definition = definition;
        _isSelected = selected;
        _changed = changed;
    }

    public ApplicationRoleDefinition Definition { get; }
    public string Title => Definition.Title;
    public string RoleName => Definition.RoleName;
    public string ApiPermissions => string.Join(", ", Definition.ApiPermissions) + $" ({Definition.Protocol})";
    public string Explanation => Definition.Explanation;
    public string SecurityImplications => Definition.SecurityImplications;
    public IReadOnlyList<string> Capabilities => Definition.EffectiveCapabilities;
    public string CapabilitiesText => Capabilities.Count == 0 ? "Not described" : string.Join(" · ", Capabilities);
    public string PrivilegeLabel => Definition.Privilege + " privilege" + (Definition.IsCurated ? string.Empty : " (assumed)");
    public StatusKind PrivilegeKind => StatusMapper.From(Definition.Privilege);
    public bool IsAvailable => Definition.IsAvailableInTenant;

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _changed();
}

public sealed record RoleGroupItem(string Title, IReadOnlyList<RoleOptionItem> Roles);

public sealed record AdviceItem(StatusKind Kind, string Title, string Detail);

/// <summary>Step 3: choose application roles, with least-privilege advice.</summary>
public sealed partial class PermissionsStepViewModel(WizardServices services, WizardState state, ILogger logger) : WizardStepViewModel(services, state, logger)
{
    private readonly List<RoleOptionItem> _all = [];
    private IReadOnlyList<string>? _prefill;
    private Guid _loadedForSession;

    public override string Title => "Select permissions";

    public override string Description => "Roles are discovered from the connected tenant. Select only what the application needs.";

    public ObservableCollection<RoleGroupItem> Groups { get; } = [];

    public ObservableCollection<AdviceItem> Advice { get; } = [];

    [ObservableProperty] private bool _showUnavailable;
    [ObservableProperty] private string _selectionSummary = "No roles selected.";

    partial void OnShowUnavailableChanged(bool value) => BuildGroups();

    public override bool CanAdvance => !IsBusy && _all.Any(r => r.IsSelected);

    public void Prefill(IReadOnlyList<string> roles) => _prefill = roles;

    public override Task OnEnterAsync()
    {
        if (_loadedForSession == Services.Connections.Current.SessionId && _all.Count > 0) return Task.CompletedTask;
        return RunAsync(async ct =>
        {
            var roles = await Services.Roles.GetRolesAsync(ct);
            var selected = (_prefill ?? State.Roles).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _prefill = null;
            _all.Clear();
            foreach (var role in roles) _all.Add(new RoleOptionItem(role, selected.Contains(role.RoleName) && role.IsAvailableInTenant, OnSelectionChanged));
            _loadedForSession = Services.Connections.Current.SessionId;
            BuildGroups();
            OnSelectionChanged();
        }, "Discovering application roles in the tenant…", "Load application roles");
    }

    private void BuildGroups()
    {
        Groups.Clear();
        foreach (var group in _all.Where(r => r.IsAvailable || ShowUnavailable).GroupBy(r => r.Definition.Category).OrderBy(g => g.Key))
        {
            var title = group.Key switch
            {
                RoleCategory.Mail => "Mail",
                RoleCategory.Calendars => "Calendars",
                RoleCategory.Contacts => "Contacts",
                RoleCategory.MailboxSettings => "Mailbox settings",
                RoleCategory.MailboxFoldersAndItems => "Mailbox folders, items and configuration",
                RoleCategory.Protocols => "Protocols (EWS, SMTP)",
                RoleCategory.FullAccessBundles => "Full access bundles",
                _ => "Other roles discovered in the tenant",
            };
            Groups.Add(new RoleGroupItem(title, group.OrderBy(r => r.Definition.Privilege).ThenBy(r => r.RoleName).ToList()));
        }
    }

    private void OnSelectionChanged()
    {
        var selected = _all.Where(r => r.IsSelected).Select(r => r.RoleName).ToList();
        State.Roles = selected;
        State.InvalidateAfter(2);
        Advice.Clear();
        foreach (var advice in RoleCatalogService.Evaluate(selected))
            Advice.Add(new AdviceItem(StatusMapper.From(advice.Severity), advice.Title, advice.Detail));
        SelectionSummary = selected.Count == 0 ? "No roles selected." : $"{selected.Count} role(s) selected: {string.Join(", ", selected)}";
        NotifyNavigationChanged();
    }
}

/// <summary>Step 4: read-only preflight security and configuration validation.</summary>
public sealed partial class ValidationStepViewModel(WizardServices services, WizardState state, ILogger logger) : WizardStepViewModel(services, state, logger)
{
    public override string Title => "Security & configuration validation";

    public override string Description => "Read-only checks. Nothing is changed. Unverifiable security-critical checks block execution.";

    public ObservableCollection<CheckItem> Checks { get; } = [];

    public ObservableCollection<string> FilterErrors { get; } = [];

    [ObservableProperty] private string? _summary;
    [ObservableProperty] private string? _verdict;
    [ObservableProperty] private StatusKind _verdictKind;
    [ObservableProperty] private bool _requiresAcknowledgement;
    [ObservableProperty] private string? _acknowledgementText;
    [ObservableProperty] private bool _isAcknowledged;

    partial void OnIsAcknowledgedChanged(bool value)
    {
        State.Acknowledged = value;
        NotifyNavigationChanged();
    }

    public override bool CanAdvance => !IsBusy && State.Preflight is { } outcome && outcome.Report.CanProceed(IsAcknowledged);

    public override Task OnEnterAsync()
    {
        if (State.Preflight is not null && State.PreflightInputKey == State.InputKey)
        {
            Show(State.Preflight);
            return Task.CompletedTask;
        }

        return Run();
    }

    [RelayCommand]
    private Task Run() => RunAsync(async ct =>
    {
        Checks.Clear();
        FilterErrors.Clear();
        State.Preflight = null;
        NotifyNavigationChanged();

        var build = ScopeDesigner.BuildFilter(State.Mailboxes, State.Strategy);
        if (!build.Succeeded)
        {
            foreach (var error in build.Errors) FilterErrors.Add(error);
            Verdict = "A scope filter could not be generated for the selection.";
            VerdictKind = StatusKind.Critical;
            return;
        }

        State.ScopeFilter = build.Filter;
        if (string.IsNullOrWhiteSpace(State.ScopeName) || (!State.ScopeNameEditedByUser && State.Mode == ConfigurationMode.New))
        {
            var inventory = await Services.Inventory.GetAsync(ct);
            var sp = State.Application!.ServicePrincipal;
            var naming = new NamingContext(sp.DisplayName, sp.AppId, State.Roles, State.Mailboxes.Count, Services.Time.GetUtcNow());
            State.ScopeName = ScopeDesigner.SuggestName(Services.Settings.Current.ScopeNameTemplate, naming, build.Filter!, inventory.Scopes);
        }

        var progress = new Progress<PreflightCheck>(check => Checks.Add(new CheckItem(check)));
        var outcome = await Services.Preflight.RunAsync(State.BuildRequest(), progress, ct);
        State.Preflight = outcome;
        State.PreflightInputKey = State.InputKey;
        State.ValidatedPlan = null;
        Show(outcome);
    }, "Running preflight checks…", "Preflight validation");

    /// <summary>Re-establishes the Exchange session (for example after a PIM role activation) and re-runs all checks.</summary>
    [RelayCommand]
    private async Task ReconnectAndRun()
    {
        var reconnected = await RunAsync(ct => Services.Connections.ReconnectExchangeAsync(ct), "Reconnecting to Exchange Online to apply newly activated roles…", "Reconnect");
        if (reconnected) await Run();
    }

    private void Show(PreflightOutcome outcome)
    {
        if (Checks.Count != outcome.Report.Checks.Count)
        {
            Checks.Clear();
            foreach (var check in outcome.Report.Checks) Checks.Add(new CheckItem(check));
        }

        var report = outcome.Report;
        Summary = $"{report.Count(CheckStatus.Passed)} passed · {report.Count(CheckStatus.Warning)} warning · {report.Count(CheckStatus.Blocked)} blocked · {report.Count(CheckStatus.UnableToVerify)} unable to verify";
        RequiresAcknowledgement = report.AcknowledgeableBlocks.Count > 0 && !report.HasHardBlock;
        AcknowledgementText = RequiresAcknowledgement
            ? "I understand that this configuration will NOT restrict the application to the selected mailboxes until the conflicting permissions listed above are removed, and that it will be reported as not securely scoped."
            : null;
        IsAcknowledged = RequiresAcknowledgement && State.Acknowledged;
        if (report.HasHardBlock)
        {
            Verdict = "Blocked: resolve the blocked or unverifiable checks before continuing.";
            VerdictKind = StatusKind.Critical;
        }
        else if (!report.IsSecurelyScoped)
        {
            Verdict = "Not securely scoped: existing permissions would bypass the mailbox restriction.";
            VerdictKind = StatusKind.Warning;
        }
        else
        {
            Verdict = "All security-critical checks passed. The configuration can be securely scoped.";
            VerdictKind = StatusKind.Success;
        }

        NotifyNavigationChanged();
    }
}
