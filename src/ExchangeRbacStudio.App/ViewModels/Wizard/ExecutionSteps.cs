using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.App.ViewModels.Dialogs;
using ExchangeRbacStudio.App.ViewModels.Pages;
using ExchangeRbacStudio.App.ViewModels.Shared;
using ExchangeRbacStudio.Application.Authorization;
using ExchangeRbacStudio.Application.Configuration;
using ExchangeRbacStudio.Application.Execution;
using ExchangeRbacStudio.Application.Reporting;
using ExchangeRbacStudio.Application.Scoping;
using ExchangeRbacStudio.Domain.Auditing;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Permissions;
using ExchangeRbacStudio.Domain.Planning;
using ExchangeRbacStudio.Domain.Scoping;
using ExchangeRbacStudio.Domain.Validation;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.ViewModels.Wizard;

/// <summary>Step 5: the generated management scope, its exact filter and the verified membership.</summary>
public sealed partial class ScopeStepViewModel(WizardServices services, WizardState state, ILogger logger) : WizardStepViewModel(services, state, logger)
{
    private bool _loading;

    public override string Title => "Generate scope";

    public override string Description => "An exact-identity management scope. Exchange must resolve it to precisely the selected mailboxes.";

    public ObservableCollection<RecipientItem> Matched { get; } = [];

    public ObservableCollection<RecipientItem> Unexpected { get; } = [];

    public ObservableCollection<RecipientItem> Missing { get; } = [];

    [ObservableProperty] private string _scopeName = string.Empty;
    [ObservableProperty] private string? _filter;
    [ObservableProperty] private string? _strategyLabel;
    [ObservableProperty] private string? _nameMessage;
    [ObservableProperty] private StatusKind _nameKind;
    [ObservableProperty] private string? _verificationMessage;
    [ObservableProperty] private StatusKind _verificationKind;
    [ObservableProperty] private string? _equivalentScopeName;
    [ObservableProperty] private bool _isNameUsable;
    [ObservableProperty] private bool _isVerified;

    public bool IsModify => State.Mode == ConfigurationMode.Modify;

    public string StrategyExplanation =>
        "PrimarySmtpAddress is not used: Microsoft documents that filters on it also match proxy addresses, and addresses can be reassigned. "
        + "The filter uses immutable identifiers and is accepted only if Exchange resolves it to exactly the selected mailboxes.";

    public override bool CanAdvance => !IsBusy && IsNameUsable && IsVerified;

    public override Task OnEnterAsync()
    {
        _loading = true;
        ScopeName = State.ScopeName ?? string.Empty;
        _loading = false;
        Filter = State.ScopeFilter;
        StrategyLabel = State.Strategy switch
        {
            RecipientFilterStrategy.ExternalDirectoryObjectId => "Microsoft Entra object ID (ExternalDirectoryObjectId)",
            RecipientFilterStrategy.ObjectGuid => "Exchange object GUID (Guid)",
            _ => "Mailbox GUID (ExchangeGuid)",
        };
        CheckName();
        ShowVerification(State.Preflight?.Context.ScopeVerification);
        return Task.CompletedTask;
    }

    partial void OnScopeNameChanged(string value)
    {
        if (_loading) return;
        State.ScopeName = value.Trim();
        State.ScopeNameEditedByUser = true;
        State.ValidatedPlan = null;
        CheckName();
    }

    [RelayCommand]
    private void UseEquivalentScope()
    {
        if (EquivalentScopeName is not null) ScopeName = EquivalentScopeName;
    }

    [RelayCommand]
    private Task Reverify() => RunAsync(async ct =>
    {
        if (Filter is null) return;
        ShowVerification(await Services.ScopeDesigner.VerifyAsync(Filter, State.Mailboxes, ct));
    }, "Resolving the filter in Exchange Online…", "Verify scope");

    private void CheckName()
    {
        var scopes = State.Preflight?.Context.State?.Scopes ?? [];
        var updatable = State.Mode == ConfigurationMode.Modify ? State.ExistingScopeName : null;
        var check = ScopeDesigner.CheckName(ScopeName.Trim(), Filter ?? string.Empty, scopes, updatable);
        IsNameUsable = check.IsUsable;
        NameMessage = check.Message;
        NameKind = check.IsUsable ? (check.ExistingIsEquivalent ? StatusKind.Info : StatusKind.Success) : StatusKind.Critical;
        var equivalent = Filter is null ? null : ScopeDesigner.FindEquivalentScope(Filter, scopes);
        EquivalentScopeName = equivalent is not null && !equivalent.Name.Equals(ScopeName.Trim(), StringComparison.OrdinalIgnoreCase) ? equivalent.Name : null;
        NotifyNavigationChanged();
    }

    private void ShowVerification(ScopeVerification? verification)
    {
        Matched.Clear();
        Unexpected.Clear();
        Missing.Clear();
        if (verification is null)
        {
            IsVerified = false;
            VerificationMessage = "The scope has not been verified yet.";
            VerificationKind = StatusKind.Warning;
            NotifyNavigationChanged();
            return;
        }

        IsVerified = verification.IsVerified;
        VerificationMessage = verification.Message;
        VerificationKind = StatusMapper.From(verification.Status);
        if (verification.Comparison is { } comparison)
        {
            foreach (var r in comparison.Matched) Matched.Add(new RecipientItem(r.DisplayName, r.PrimarySmtpAddress, r.RecipientTypeDetails));
            foreach (var r in comparison.Unexpected) Unexpected.Add(new RecipientItem(r.DisplayName, r.PrimarySmtpAddress, r.RecipientTypeDetails));
            foreach (var r in comparison.Missing) Missing.Add(new RecipientItem(r.DisplayName, r.PrimarySmtpAddress, r.RecipientTypeDetails));
        }

        NotifyNavigationChanged();
    }
}

/// <summary>Step 6: human-readable review, exact PowerShell preview, and the explicit "Validate configuration" gate.</summary>
public sealed partial class ReviewStepViewModel(WizardServices services, WizardState state, ILogger logger) : WizardStepViewModel(services, state, logger)
{
    public override string Title => "Review change";

    public override string Description => "Review exactly what will change. Validate the configuration to enable execution.";

    public ObservableCollection<FactItem> Identity { get; } = [];

    public ObservableCollection<string> Mailboxes { get; } = [];

    public ObservableCollection<string> Roles { get; } = [];

    public ObservableCollection<string> ExpectedMatches { get; } = [];

    public ObservableCollection<string> ExistingConfiguration { get; } = [];

    public ObservableCollection<OperationItem> Operations { get; } = [];

    public ObservableCollection<PlanConflict> Conflicts { get; } = [];

    public ObservableCollection<CheckItem> Concerns { get; } = [];

    [ObservableProperty] private string? _scopeName;
    [ObservableProperty] private string? _filter;
    [ObservableProperty] private string? _script;
    [ObservableProperty] private bool _isValidated;
    [ObservableProperty] private string? _validationMessage;
    [ObservableProperty] private StatusKind _validationKind;
    [ObservableProperty] private string? _changeSummary;
    [ObservableProperty] private string? _securityVerdict;
    [ObservableProperty] private StatusKind _securityKind;

    public override bool CanAdvance => !IsBusy && IsValidated && State.ValidatedPlan is { HasConflicts: false };

    public override Task OnEnterAsync()
    {
        IsValidated = State.ValidatedPlan is not null;
        if (State.Preflight?.Context.State is { } current && State.Preflight.Context.Session.TenantId is { } tenantId)
        {
            var plan = State.ValidatedPlan ?? ConfigurationPlanner.Plan(State.BuildRequest(), current, tenantId, State.Preflight.Context.Session.TenantDisplayName,
                Services.Settings.Current.AssignmentNameTemplate, Services.Time.GetUtcNow());
            Show(plan, State.Preflight);
        }

        ValidationMessage = IsValidated
            ? "Configuration validated against the live tenant. You can continue to execution."
            : "Not validated yet. Validate the configuration to re-run all checks against the live tenant and lock the plan.";
        ValidationKind = IsValidated ? StatusKind.Success : StatusKind.Info;
        NotifyNavigationChanged();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task Validate() => RunAsync(async ct =>
    {
        IsValidated = false;
        State.ValidatedPlan = null;
        var request = State.BuildRequest();
        var outcome = await Services.Preflight.RunAsync(request, null, ct);
        State.Preflight = outcome;
        State.PreflightInputKey = State.InputKey;

        if (outcome.Context.State is null || outcome.Context.Session.TenantId is not { } tenantId)
        {
            ValidationMessage = "Validation could not read the current configuration. Review the checks and try again.";
            ValidationKind = StatusKind.Critical;
            ShowConcerns(outcome);
            return;
        }

        var plan = ConfigurationPlanner.Plan(request, outcome.Context.State, tenantId, outcome.Context.Session.TenantDisplayName,
            Services.Settings.Current.AssignmentNameTemplate, Services.Time.GetUtcNow());
        Show(plan, outcome);

        if (!outcome.Report.CanProceed(State.Acknowledged))
        {
            ValidationMessage = outcome.Report.HasHardBlock
                ? "Validation found blocking issues. Return to Security validation to resolve them."
                : "Validation found conflicts that require acknowledgement in the Security validation step.";
            ValidationKind = StatusKind.Critical;
            return;
        }

        if (plan.HasConflicts)
        {
            ValidationMessage = "The plan contains conflicts that require a decision (see below).";
            ValidationKind = StatusKind.Critical;
            return;
        }

        State.ValidatedPlan = plan;
        IsValidated = true;
        ValidationMessage = plan.HasChanges
            ? $"Validated. {plan.WriteOperations.Count} change(s) will be made; plan fingerprint {plan.Fingerprint[..12]}…"
            : "Validated. The desired configuration already exists; executing will only verify it.";
        ValidationKind = StatusKind.Success;
        await Services.Audit.RecordAsync(Services.Connections.Current, AuditCategories.Validation, "Validate configuration",
            outcome.Report.IsSecurelyScoped ? AuditOutcome.Succeeded : AuditOutcome.Warning,
            $"{plan.Title}: {plan.WriteOperations.Count} change(s); securely scoped: {outcome.Report.IsSecurelyScoped}.", plan.Application?.DisplayName, details: $"Fingerprint {plan.Fingerprint}");
    }, "Validating against the live tenant…", "Validate configuration");

    [RelayCommand]
    private void CopyScript() => ClipboardHelper.TrySetText(Script);

    private void Show(ChangePlan plan, PreflightOutcome outcome)
    {
        var session = outcome.Context.Session;
        var sp = State.Application!.ServicePrincipal;
        Identity.Clear();
        Identity.Add(new FactItem("Target tenant", $"{session.TenantDisplayName}"));
        Identity.Add(new FactItem("Tenant ID", session.TenantId?.ToString(), true));
        Identity.Add(new FactItem("Environment", session.Environment.DisplayName));
        Identity.Add(new FactItem("Application", sp.DisplayName));
        Identity.Add(new FactItem("Application (client) ID", sp.AppId.ToString(), true));
        Identity.Add(new FactItem("Service principal object ID", sp.ObjectId.ToString(), true));
        Identity.Add(new FactItem("Mode", State.Mode == ConfigurationMode.Modify ? $"Modify existing scope '{State.ExistingScopeName}'" : "Add scoped access"));

        Mailboxes.Clear();
        foreach (var m in State.Mailboxes) Mailboxes.Add($"{m.Label} · {m.RecipientTypeDetails} · {m.Guid}");
        Roles.Clear();
        foreach (var r in State.Roles) Roles.Add($"{r} ({string.Join(", ", ApplicationRoleCatalog.Resolve(r).ApiPermissions)})");

        ScopeName = State.ScopeName;
        Filter = State.ScopeFilter;
        ExpectedMatches.Clear();
        foreach (var m in outcome.Context.ScopeVerification?.Comparison?.Matched ?? []) ExpectedMatches.Add(m.Label);

        ExistingConfiguration.Clear();
        if (outcome.Context.State is { } current)
        {
            ExistingConfiguration.Add(current.Pointer is null ? "No Exchange service principal pointer exists yet." : $"Pointer '{current.Pointer.DisplayName}' exists (AppId {current.Pointer.AppId}).");
            foreach (var a in current.ServicePrincipalAssignments)
                ExistingConfiguration.Add($"Assignment '{a.Name}': {a.Role} → {a.ScopeReference ?? "organization"}");
            if (current.ServicePrincipalAssignments.Count == 0) ExistingConfiguration.Add("The application has no Exchange application role assignments.");
        }

        Operations.Clear();
        foreach (var op in plan.Operations) Operations.Add(new OperationItem(op));
        Conflicts.Clear();
        foreach (var c in plan.Conflicts) Conflicts.Add(c);
        ChangeSummary = $"{plan.WriteOperations.Count} change(s), {plan.Operations.Count(o => o.Disposition == OperationDisposition.Reuse)} reused object(s), {plan.Conflicts.Count} conflict(s)";
        Script = plan.ToPowerShellScript();
        ShowConcerns(outcome);
    }

    private void ShowConcerns(PreflightOutcome outcome)
    {
        Concerns.Clear();
        foreach (var check in outcome.Report.Checks.Where(c => c.Status is CheckStatus.Blocked or CheckStatus.UnableToVerify or CheckStatus.Warning))
            Concerns.Add(new CheckItem(check));
        SecurityVerdict = outcome.Report.IsSecurelyScoped
            ? "Securely scoped once executed and verified: no conflicting organization-wide permission was found."
            : "NOT securely scoped: existing permissions bypass the restriction or could not be verified. The report will state this.";
        SecurityKind = outcome.Report.IsSecurelyScoped ? StatusKind.Success : StatusKind.Critical;
    }
}

/// <summary>Step 7: explicit confirmation and live, step-by-step execution of the validated plan.</summary>
public sealed partial class ExecuteStepViewModel(WizardServices services, WizardState state, Func<int, Task> goTo, ILogger logger) : WizardStepViewModel(services, state, logger)
{
    private CancellationTokenSource? _cts;

    public override string Title => "Execute configuration";

    public override string Description => "Changes are made only after you confirm. Each step is verified against Exchange Online.";

    public ObservableCollection<ExecutionStepItem> Steps { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand), nameof(DryRunCommand))]
    private bool _isConfirmed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand), nameof(DryRunCommand))]
    private bool _isExecuting;

    [ObservableProperty] private string? _tenantName;
    [ObservableProperty] private string? _tenantId;
    [ObservableProperty] private string? _planSummary;
    [ObservableProperty] private string? _outcomeLabel;
    [ObservableProperty] private StatusKind _outcomeKind;
    [ObservableProperty] private bool _canCompensate;
    [ObservableProperty] private string? _reportPath;

    public override bool CanAdvance => !IsBusy && !IsExecuting && State.Execution is { DryRun: false, Outcome: ExecutionOutcome.Completed or ExecutionOutcome.PartiallyCompleted };

    public override bool CanGoBack => !IsBusy && !IsExecuting && !State.HasExecuted;

    public override string NextLabel => "Verify";

    public override Task OnEnterAsync()
    {
        var plan = State.ValidatedPlan;
        TenantName = plan?.TenantDisplayName;
        TenantId = plan?.TenantId.ToString();
        PlanSummary = plan is null ? "No validated plan." : $"{plan.Title}: {plan.WriteOperations.Count} change(s) to execute.";
        if (State.Execution is null)
        {
            Steps.Clear();
            if (plan is not null) foreach (var step in ExecutionStepItem.CreateFor(plan)) Steps.Add(step);
        }

        NotifyNavigationChanged();
        return Task.CompletedTask;
    }

    private bool CanStart() => IsConfirmed && !IsExecuting && State.ValidatedPlan is not null && !State.HasExecuted;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task Execute() => RunPlanAsync(dryRun: false);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task DryRun() => RunPlanAsync(dryRun: true);

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private Task Revalidate()
    {
        State.ValidatedPlan = null;
        return goTo(5);
    }

    [RelayCommand]
    private async Task CompensateAsync()
    {
        if (State.Execution is not { } result) return;
        PreparedPlan? prepared = null;
        await RunAsync(async ct => prepared = await Services.Plans.CompensateAsync(result, ct), "Checking dependencies of created objects…", "Prepare compensation");
        if (prepared is null) return;
        var outcome = Services.Dialogs.ShowChangeReview(new ChangeReviewRequest(prepared.Plan, prepared.Replan, "Remove objects created by this run",
            "Only objects created by the failed run are removed, and only when nothing else depends on them. Pre-existing configuration is never touched.", null));
        if (outcome?.ChangesMade == true) CanCompensate = false;
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (ReportPath is not null) ShellLauncher.OpenFile(ReportPath);
    }

    private async Task RunPlanAsync(bool dryRun)
    {
        var plan = State.ValidatedPlan!;
        var request = State.BuildRequest();
        _cts = new CancellationTokenSource();
        Steps.Clear();
        foreach (var step in ExecutionStepItem.CreateFor(plan)) Steps.Add(step);
        IsExecuting = true;
        NotifyNavigationChanged();
        try
        {
            var progress = new Progress<StepResult>(step => Steps.FirstOrDefault(s => s.Index == step.Index)?.Update(step));
            async Task<ChangePlan> Replan(CancellationToken ct)
            {
                var current = await Services.StateReader.ReadAsync(request, ct);
                return ConfigurationPlanner.Plan(request, current, plan.TenantId, plan.TenantDisplayName, Services.Settings.Current.AssignmentNameTemplate, plan.CreatedUtc);
            }

            await RunAsync(async ct =>
            {
                var result = await Services.Executor.ExecuteAsync(plan, new ExecutionOptions(dryRun, plan.Fingerprint, Replan), progress, ct);
                OutcomeLabel = result.Summary;
                OutcomeKind = StatusMapper.From(result.Outcome);
                if (!dryRun) State.Execution = result;
                CanCompensate = !dryRun && result.Outcome == ExecutionOutcome.PartiallyCompleted && result.CreatedObjects.Count > 0;
                ReportPath = await WizardReporting.SaveAsync(Services, State, dryRun ? result : null, ct);
            }, dryRun ? "Dry run…" : "Executing…", dryRun ? "Dry run" : "Execute configuration", _cts.Token);
        }
        finally
        {
            IsExecuting = false;
            if (State.HasExecuted) IsConfirmed = false;
            NotifyNavigationChanged();
            ExecuteCommand.NotifyCanExecuteChanged();
            DryRunCommand.NotifyCanExecuteChanged();
        }
    }
}

public sealed record VerifyOutcomeItem(TestOutcomeItem Outcome);

/// <summary>Step 8: Exchange authorization tests (positive and negative, per role) and the change report.</summary>
public sealed partial class VerifyStepViewModel : WizardStepViewModel
{
    private bool _autoRan;

    public VerifyStepViewModel(WizardServices services, WizardState state, ILogger logger) : base(services, state, logger)
    {
        NegativePicker = services.PickerFactory();
        NegativePicker.SetAdditionalRule(r => State.Mailboxes.Any(m => m.IdentityKey == r.IdentityKey)
            ? "This mailbox is in the intended scope; choose a mailbox that should NOT be accessible."
            : null);
    }

    public override string Title => "Verify & report";

    public override string Description => "Test-ServicePrincipalAuthorization for every intended mailbox, optional negative tests, and the change report.";

    public override bool IsFinalStep => true;

    public override bool CanAdvance => false;

    public override bool CanGoBack => !IsBusy;

    public MailboxPickerViewModel NegativePicker { get; }

    public ObservableCollection<string> Positive { get; } = [];

    public ObservableCollection<TestOutcomeItem> Results { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    public ObservableCollection<string> Errors { get; } = [];

    [ObservableProperty] private string? _overallLabel;
    [ObservableProperty] private StatusKind _overallKind;
    [ObservableProperty] private string? _reportPath;
    [ObservableProperty] private string? _reportJsonPath;
    [ObservableProperty] private string? _securityVerdict;
    [ObservableProperty] private StatusKind _securityKind;

    public override async Task OnEnterAsync()
    {
        Positive.Clear();
        foreach (var m in State.Mailboxes) Positive.Add(m.Label);
        if (!_autoRan && State.HasExecuted)
        {
            _autoRan = true;
            await RunTests();
        }
    }

    [RelayCommand]
    private Task RunTests() => RunAsync(async ct =>
    {
        var sp = State.Application!.ServicePrincipal;
        var grants = State.Preflight?.Context.GrantAnalysis;
        var report = await Services.Tests.RunAsync(new AuthorizationTestRequest(sp.ObjectId, sp.DisplayName, State.Mailboxes, NegativePicker.SelectedRecipients,
            State.Roles, grants?.TenantWideCapabilities ?? MailboxCapability.None, grants is not null), null, ct);
        State.Tests = report;

        Results.Clear();
        foreach (var outcome in report.Results.SelectMany(r => r.Outcomes)) Results.Add(new TestOutcomeItem(outcome));
        Errors.Clear();
        foreach (var error in report.Results.Where(r => r.Error is not null)) Errors.Add($"{error.Mailbox.Label}: {error.Error}");
        Notes.Clear();
        foreach (var note in report.Notes) Notes.Add(note);
        OverallLabel = $"{report.OverallVerdict}: {report.Passed} mailbox(es) passed, {report.Failed} failed, {report.Inconclusive} inconclusive";
        OverallKind = StatusMapper.From(report.OverallVerdict);
        ReportPath = await WizardReporting.SaveAsync(Services, State, null, ct);
        ReportJsonPath = ReportPath is null ? null : System.IO.Path.ChangeExtension(ReportPath, ".json");
        UpdateVerdict();
    }, "Running Test-ServicePrincipalAuthorization…", "Authorization tests");

    [RelayCommand]
    private Task ExportReport() => RunAsync(async ct =>
    {
        var folder = Services.Dialogs.PickFolder("Export the change report (JSON and HTML)", Services.Reports.DefaultDirectory);
        if (folder is null) return;
        var path = await WizardReporting.SaveAsync(Services, State, null, ct, folder);
        if (path is not null) ShellLauncher.SelectFile(path);
    }, "Exporting report…", "Export report");

    [RelayCommand]
    private void OpenReport()
    {
        if (ReportPath is not null) ShellLauncher.OpenFile(ReportPath);
    }

    [RelayCommand]
    private void Finish()
    {
        var sp = State.Application?.ServicePrincipal;
        if (sp is null) Services.Navigation.Navigate(PageKey.Applications);
        else Services.Navigation.Navigate(PageKey.ApplicationDetail, new ApplicationDetailRequest(sp.ObjectId, sp.AppId));
    }

    [RelayCommand]
    private void StartNew() => Services.Navigation.Navigate(PageKey.ConfigureAccess);

    private void UpdateVerdict()
    {
        var report = State.Execution is null ? null : ReportBuilder.Build(WizardReporting.Input(Services, State, null), Services.Time.GetUtcNow());
        if (report is null) return;
        SecurityVerdict = report.SecurityVerdict.Statement + (report.SecurityVerdict.Reasons.Count > 0 ? " " + string.Join(" ", report.SecurityVerdict.Reasons) : string.Empty);
        SecurityKind = report.SecurityVerdict.SecurelyScoped ? StatusKind.Success : StatusKind.Warning;
    }
}

/// <summary>Builds and saves the wizard's change report.</summary>
internal static class WizardReporting
{
    public static ReportInput Input(WizardServices services, WizardState state, ExecutionResult? dryRun)
    {
        var request = state.Application is not null && state.ScopeName is not null && state.ScopeFilter is not null ? state.BuildRequest() : null;
        var context = state.Preflight?.Context;
        return new ReportInput(
            services.Connections.Current,
            services.Runtime,
            dryRun is not null ? "Exchange RBAC Studio – dry run report" : "Exchange RBAC Studio – scoped application access change report",
            state.ValidatedPlan ?? dryRun?.Plan,
            dryRun ?? state.Execution,
            state.Preflight?.Report,
            request,
            context?.ScopeVerification,
            context?.GrantAnalysis,
            dryRun is null ? state.Tests : null,
            context?.State?.Pointer is null ? "Created by this change" : "Existing pointer reused");
    }

    public static async Task<string?> SaveAsync(WizardServices services, WizardState state, ExecutionResult? dryRun, CancellationToken ct, string? directory = null)
    {
        if (state.Application is null) return null;
        var report = ReportBuilder.Build(Input(services, state, dryRun), services.Time.GetUtcNow());
        state.ReportBaseName ??= string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{ScopeNamingPolicy.SanitizeSegment(state.Application.ServicePrincipal.DisplayName)}");
        var baseName = dryRun is not null ? state.ReportBaseName + "-dryrun" : state.ReportBaseName;
        var result = await services.Reports.ExportAsync(report, directory ?? services.Reports.DefaultDirectory, baseName, ct);
        await services.Audit.RecordAsync(services.Connections.Current, AuditCategories.Report, "Report exported", AuditOutcome.Information,
            $"Change report saved ({(report.SecurityVerdict.SecurelyScoped ? "securely scoped" : "not confirmed as securely scoped")}).",
            state.Application.ServicePrincipal.DisplayName, state.Execution?.RunId, details: result.HtmlPath);
        return result.HtmlPath;
    }
}
