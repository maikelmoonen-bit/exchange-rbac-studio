using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.App.ViewModels.Shared;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Execution;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Application.Settings;
using ExchangeRbacStudio.Domain.Clouds;
using ExchangeRbacStudio.Domain.Planning;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.ViewModels.Dialogs;

/// <summary>Sign-in options and progress for connecting to Microsoft Entra ID and Exchange Online.</summary>
public sealed partial class ConnectDialogViewModel : ViewModelBase, IDisposable
{
    private readonly ConnectionManager _connections;
    private readonly ISettingsStore _settings;
    private readonly EventHandler<SessionSnapshot> _sessionHandler;
    private CancellationTokenSource? _cts;

    public ConnectDialogViewModel(ConnectionManager connections, ISettingsStore settings, IUiDispatcher dispatcher, ILogger<ConnectDialogViewModel> logger) : base(logger)
    {
        _connections = connections;
        _settings = settings;
        _sessionHandler = (_, s) => dispatcher.Post(() => UpdateStates(s));
        var current = settings.Current;
        SelectedEnvironment = CloudEnvironments.Get(current.Environment);
        TenantHint = current.TenantHint ?? string.Empty;
        ClientMode = current.GraphClient == GraphClientMode.NotConfigured ? GraphClientMode.MicrosoftGraphCommandLineTools : current.GraphClient;
        CustomClientId = current.CustomGraphClientId ?? string.Empty;
        UseWam = current.UseWebAccountManagerForExchange;
        IsFirstRun = current.GraphClient == GraphClientMode.NotConfigured;
        connections.SessionChanged += _sessionHandler;
        UpdateStates(connections.Current);
    }

    public void Dispose()
    {
        _connections.SessionChanged -= _sessionHandler;
        _cts?.Cancel();
    }

    public event EventHandler<bool>? RequestClose;

    public IReadOnlyList<CloudEnvironmentProfile> Environments => CloudEnvironments.All;

    public string MicrosoftClientId => KnownPublicClients.MicrosoftGraphCommandLineTools;

    public bool IsFirstRun { get; }

    [ObservableProperty] private CloudEnvironmentProfile _selectedEnvironment;
    [ObservableProperty] private string _tenantHint;
    [ObservableProperty] private GraphClientMode _clientMode;
    [ObservableProperty] private string _customClientId;
    [ObservableProperty] private bool _useWam;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _graphStatus = "Not connected";
    [ObservableProperty] private StatusKind _graphKind = StatusKind.Neutral;
    [ObservableProperty] private string? _graphDetail;
    [ObservableProperty] private string _exchangeStatus = "Not connected";
    [ObservableProperty] private StatusKind _exchangeKind = StatusKind.Neutral;
    [ObservableProperty] private string? _exchangeDetail;

    public bool CanRetryExchange => _connections.Current.IsGraphConnected && !_connections.Current.IsExchangeConnected && !IsConnecting;

    [RelayCommand]
    private async Task ConnectAsync()
    {
        var settings = _settings.Current with
        {
            Environment = SelectedEnvironment.Environment,
            TenantHint = string.IsNullOrWhiteSpace(TenantHint) ? null : TenantHint.Trim(),
            GraphClient = ClientMode,
            CustomGraphClientId = string.IsNullOrWhiteSpace(CustomClientId) ? null : CustomClientId.Trim(),
            UseWebAccountManagerForExchange = UseWam,
        };

        _cts = new CancellationTokenSource();
        IsConnecting = true;
        OnPropertyChanged(nameof(CanRetryExchange));
        try
        {
            var ok = await RunAsync(async ct =>
            {
                await _settings.SaveAsync(settings, ct);
                if (!_connections.Current.IsGraphConnected) await _connections.ConnectGraphAsync(ct);
                if (!_connections.Current.IsExchangeConnected) await _connections.ConnectExchangeAsync(ct);
            }, "Connecting…", "Connect", _cts.Token);

            if (ok && _connections.Current.IsFullyConnected) RequestClose?.Invoke(this, true);
        }
        finally
        {
            IsConnecting = false;
            OnPropertyChanged(nameof(CanRetryExchange));
        }
    }

    [RelayCommand]
    private void CancelConnect() => _cts?.Cancel();

    [RelayCommand]
    private void Close()
    {
        _cts?.Cancel();
        RequestClose?.Invoke(this, _connections.Current.IsGraphConnected || _connections.Current.IsExchangeConnected);
    }

    private void UpdateStates(SessionSnapshot s)
    {
        GraphStatus = StatusMapper.Label(s.Graph);
        GraphKind = StatusMapper.From(s.Graph);
        GraphDetail = s.GraphStatusMessage;
        ExchangeStatus = StatusMapper.Label(s.Exchange);
        ExchangeKind = StatusMapper.From(s.Exchange);
        ExchangeDetail = s.ExchangeStatusMessage;
        OnPropertyChanged(nameof(CanRetryExchange));
    }
}

/// <summary>Request to review and execute a management change plan.</summary>
public sealed record ChangeReviewRequest(
    ChangePlan Plan,
    Func<CancellationToken, Task<ChangePlan>>? Replan,
    string Heading,
    string Description,
    string? TypedConfirmationText);

/// <summary>
/// Reviews a change plan (operations, before/after, exact PowerShell), requires explicit confirmation and executes it
/// with live progress. Used for removals, rescoping and compensation outside the wizard.
/// </summary>
public sealed partial class ChangeReviewViewModel : ViewModelBase
{
    private readonly ChangeExecutor _executor;
    private ChangeReviewRequest? _request;
    private CancellationTokenSource? _cts;

    public ChangeReviewViewModel(ChangeExecutor executor, ConnectionManager connections, ILogger<ChangeReviewViewModel> logger) : base(logger)
    {
        _executor = executor;
        var session = connections.Current;
        TenantName = session.TenantDisplayName;
        TenantId = session.TenantId?.ToString() ?? "—";
    }

    public event EventHandler? RequestClose;

    public string TenantName { get; }

    public string TenantId { get; }

    public string Heading => _request?.Heading ?? string.Empty;

    public string Description => _request?.Description ?? string.Empty;

    public ObservableCollection<OperationItem> Operations { get; } = [];

    public ObservableCollection<PlanConflict> Conflicts { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    public ObservableCollection<ExecutionStepItem> Steps { get; } = [];

    public string Script => _request?.Plan.ToPowerShellScript() ?? string.Empty;

    public string? TypedConfirmationText => _request?.TypedConfirmationText;

    public bool RequiresTypedConfirmation => !string.IsNullOrEmpty(TypedConfirmationText);

    public bool HasChanges => _request?.Plan.HasChanges == true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private bool _isConfirmed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private string _confirmationInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private bool _isExecuting;

    [ObservableProperty] private ExecutionResult? _result;
    [ObservableProperty] private string? _outcomeLabel;
    [ObservableProperty] private StatusKind _outcomeKind;

    public bool IsReviewing => Result is null && !IsExecuting;

    public void Initialize(ChangeReviewRequest request)
    {
        _request = request;
        Operations.Clear();
        foreach (var op in request.Plan.Operations) Operations.Add(new OperationItem(op));
        Conflicts.Clear();
        foreach (var conflict in request.Plan.Conflicts) Conflicts.Add(conflict);
        Notes.Clear();
        foreach (var note in request.Plan.Notes ?? []) Notes.Add(note);
        Steps.Clear();
        foreach (var step in ExecutionStepItem.CreateFor(request.Plan)) Steps.Add(step);
        OnPropertyChanged(string.Empty);
    }

    private bool CanExecute() =>
        _request is not null && !IsExecuting && Result is null && !_request.Plan.HasConflicts && _request.Plan.HasChanges && IsConfirmed
        && (!RequiresTypedConfirmation || string.Equals(ConfirmationInput.Trim(), TypedConfirmationText, StringComparison.Ordinal));

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        if (_request is null) return;
        _cts = new CancellationTokenSource();
        IsExecuting = true;
        OnPropertyChanged(nameof(IsReviewing));
        try
        {
            var progress = new Progress<StepResult>(step =>
            {
                var item = Steps.FirstOrDefault(s => s.Index == step.Index);
                item?.Update(step);
            });

            await RunAsync(async ct =>
            {
                Result = await _executor.ExecuteAsync(_request.Plan, new ExecutionOptions(false, _request.Plan.Fingerprint, _request.Replan), progress, ct);
                OutcomeLabel = Result.Summary;
                OutcomeKind = StatusMapper.From(Result.Outcome);
            }, "Executing…", "Execute change", _cts.Token);
        }
        finally
        {
            IsExecuting = false;
            OnPropertyChanged(nameof(IsReviewing));
        }
    }

    [RelayCommand]
    private void StopAfterCurrentStep() => _cts?.Cancel();

    [RelayCommand]
    private void CopyScript() => ClipboardHelper.TrySetText(Script);

    [RelayCommand]
    private void Close()
    {
        if (IsExecuting) return;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
