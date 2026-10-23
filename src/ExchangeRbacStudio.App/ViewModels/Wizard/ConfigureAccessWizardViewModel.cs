using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.Application.Configuration;
using ExchangeRbacStudio.Application.Sessions;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.ViewModels.Wizard;

public enum WizardStepState
{
    Upcoming,
    Current,
    Completed,
}

public sealed partial class WizardStepItem(int index, string title, string description) : ObservableObject
{
    public int Index { get; } = index;

    public int Number => Index + 1;

    public string Title { get; } = title;

    public string Description { get; } = description;

    [ObservableProperty] private WizardStepState _state;
}

/// <summary>Base class for a wizard step.</summary>
public abstract partial class WizardStepViewModel : ViewModelBase
{
    protected WizardStepViewModel(WizardServices services, WizardState state, ILogger logger) : base(logger)
    {
        Services = services;
        State = state;
    }

    protected WizardServices Services { get; }

    protected WizardState State { get; }

    public abstract string Title { get; }

    public abstract string Description { get; }

    public virtual bool CanAdvance => !IsBusy;

    public virtual bool CanGoBack => !IsBusy;

    public virtual string NextLabel => "Next";

    public virtual bool IsFinalStep => false;

    /// <summary>Called whenever the step becomes the current step.</summary>
    public virtual Task OnEnterAsync() => Task.CompletedTask;

    protected void NotifyNavigationChanged()
    {
        OnPropertyChanged(nameof(CanAdvance));
        OnPropertyChanged(nameof(CanGoBack));
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsBusy)) NotifyNavigationChanged();
    }
}

/// <summary>
/// Guided "Configure Scoped Application Access" workflow: application → mailboxes → permissions → security validation →
/// scope → review → execute → verify and report. Nothing is changed in the tenant before the administrator explicitly
/// confirms execution of a validated plan.
/// </summary>
public sealed partial class ConfigureAccessWizardViewModel : PageViewModel
{
    private readonly WizardServices _services;
    private WizardState _state;

    public ConfigureAccessWizardViewModel(WizardServices services, IUiDispatcher dispatcher, ILogger<ConfigureAccessWizardViewModel> logger)
        : base(services.Connections, dispatcher, logger)
    {
        _services = services;
        _state = NewState();
        StepItems =
        [
            new(0, "Select application", "Enterprise application and identifiers"),
            new(1, "Select mailboxes", "User and shared mailboxes"),
            new(2, "Select permissions", "Exchange application roles"),
            new(3, "Security validation", "Preflight and conflict checks"),
            new(4, "Generate scope", "Exact management scope"),
            new(5, "Review change", "Plan and PowerShell preview"),
            new(6, "Execute", "Apply the validated plan"),
            new(7, "Verify & report", "Authorization tests and report"),
        ];
        Steps = CreateSteps();
    }

    public override bool KeepAlive => false;

    public override string Title => "Configure Scoped Application Access";

    public override string Subtitle => _state.Mode == ConfigurationMode.Modify
        ? $"Modify the mailbox scope or permissions of an existing configuration (scope '{_state.ExistingScopeName}')."
        : "Grant an application access to selected mailboxes only, using Exchange Online RBAC for Applications.";

    public ObservableCollection<WizardStepItem> StepItems { get; }

    public IReadOnlyList<WizardStepViewModel> Steps { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))]
    private WizardStepViewModel? _currentStep;

    [ObservableProperty] private int _currentIndex;
    [ObservableProperty] private string? _resetMessage;

    protected override async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is WizardStartOptions options)
        {
            _state.Mode = options.IsModify ? ConfigurationMode.Modify : ConfigurationMode.New;
            _state.ExistingScopeName = options.ExistingScopeName;
            if (options.IsModify)
            {
                _state.ScopeName = options.ExistingScopeName;
                _state.ScopeNameEditedByUser = true;
            }

            OnPropertyChanged(nameof(Subtitle));
            ((ApplicationStepViewModel)Steps[0]).Prefill(options.ServicePrincipal);
            if (options.Mailboxes is { Count: > 0 }) ((MailboxesStepViewModel)Steps[1]).Prefill(options.Mailboxes);
            if (options.Roles is { Count: > 0 }) ((PermissionsStepViewModel)Steps[2]).Prefill(options.Roles);
        }

        await GoToAsync(0);
    }

    private bool CanGoNext() => CurrentStep is { CanAdvance: true, IsFinalStep: false } && CurrentIndex < Steps.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private Task Next() => GoToAsync(CurrentIndex + 1);

    private bool CanGoBackStep() => CurrentIndex > 0 && CurrentStep is { CanGoBack: true };

    [RelayCommand(CanExecute = nameof(CanGoBackStep))]
    private Task Back() => GoToAsync(CurrentIndex - 1);

    [RelayCommand]
    private void Cancel()
    {
        if (CurrentStep?.IsBusy == true) return;
        if (!_state.HasExecuted && _state.Application is not null
            && !_services.Dialogs.Confirm("Leave the wizard", "Leave the wizard? Nothing has been changed in Exchange Online; your selections will be discarded.", "Leave", destructive: false))
            return;
        _services.Navigation.Navigate(PageKey.Applications);
    }

    internal async Task GoToAsync(int index)
    {
        if (index < 0 || index >= Steps.Count) return;
        if (CurrentStep is not null) CurrentStep.PropertyChanged -= OnStepPropertyChanged;

        CurrentIndex = index;
        CurrentStep = Steps[index];
        CurrentStep.PropertyChanged += OnStepPropertyChanged;
        foreach (var item in StepItems)
            item.State = item.Index < index ? WizardStepState.Completed : item.Index == index ? WizardStepState.Current : WizardStepState.Upcoming;
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();

        await CurrentStep.OnEnterAsync();
        NextCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WizardStepViewModel.CanAdvance) or nameof(WizardStepViewModel.CanGoBack) or nameof(WizardStepViewModel.IsBusy))
        {
            NextCommand.NotifyCanExecuteChanged();
            BackCommand.NotifyCanExecuteChanged();
        }
    }

    protected override void OnTenantSessionChanged(SessionSnapshot previous, SessionSnapshot current)
    {
        if (_state.SessionId == current.SessionId || CurrentStep?.IsBusy == true) return;

        // Tenant isolation: never carry selections or validation results across sessions or tenants.
        _state = NewState();
        Steps = CreateSteps();
        OnPropertyChanged(nameof(Steps));
        ResetMessage = previous.TenantId is not null && previous.TenantId != current.TenantId
            ? "The tenant changed, so the configuration was reset. Selections and validation results are never reused across tenants."
            : "The session changed, so the configuration was reset.";
        _ = GoToAsync(0);
    }

    protected override void OnDisposed()
    {
        if (CurrentStep is not null) CurrentStep.PropertyChanged -= OnStepPropertyChanged;
    }

    private WizardState NewState() => new()
    {
        SessionId = _services.Connections.Current.SessionId,
        Strategy = _services.Settings.Current.FilterStrategy,
    };

    private IReadOnlyList<WizardStepViewModel> CreateSteps()
    {
        var f = _services.LoggerFactory;
        return
        [
            new ApplicationStepViewModel(_services, _state, f.CreateLogger<ApplicationStepViewModel>()),
            new MailboxesStepViewModel(_services, _state, f.CreateLogger<MailboxesStepViewModel>()),
            new PermissionsStepViewModel(_services, _state, f.CreateLogger<PermissionsStepViewModel>()),
            new ValidationStepViewModel(_services, _state, f.CreateLogger<ValidationStepViewModel>()),
            new ScopeStepViewModel(_services, _state, f.CreateLogger<ScopeStepViewModel>()),
            new ReviewStepViewModel(_services, _state, f.CreateLogger<ReviewStepViewModel>()),
            new ExecuteStepViewModel(_services, _state, i => GoToAsync(i), f.CreateLogger<ExecuteStepViewModel>()),
            new VerifyStepViewModel(_services, _state, f.CreateLogger<VerifyStepViewModel>()),
        ];
    }
}
