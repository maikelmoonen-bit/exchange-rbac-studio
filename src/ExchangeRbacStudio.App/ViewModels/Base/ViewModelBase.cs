using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Sessions;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.ViewModels;

/// <summary>An error prepared for display: understandable message, remediation and an expandable sanitized diagnostic.</summary>
public sealed partial class ErrorViewModel : ObservableObject
{
    private readonly Action<ErrorViewModel>? _dismiss;

    public ErrorViewModel(UserFacingError error, Action<ErrorViewModel>? dismiss = null)
    {
        Error = error;
        _dismiss = dismiss;
    }

    public UserFacingError Error { get; }

    public string Title => Error.Title;

    public string Message => Error.Message;

    public string? Remediation => Error.Remediation;

    public string TechnicalDetails => Error.TechnicalDetails;

    public bool IsWarning => Error.Kind is FailureKind.Cancelled or FailureKind.AuthenticationCancelled;

    [ObservableProperty]
    private bool _isDetailsExpanded;

    [ObservableProperty]
    private bool _copied;

    [RelayCommand]
    private void CopyDetails() => Copied = ClipboardHelper.TrySetText($"{Title}\n{Message}\n\n{TechnicalDetails}");

    [RelayCommand]
    private void Dismiss() => _dismiss?.Invoke(this);
}

/// <summary>Base class providing busy state and centralized, non-crashing error handling for async work.</summary>
public abstract partial class ViewModelBase : ObservableObject
{
    protected ViewModelBase(ILogger logger)
    {
        Logger = logger;
    }

    protected ILogger Logger { get; }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _busyMessage;

    [ObservableProperty]
    private ErrorViewModel? _error;

    /// <summary>Runs work with busy indication. Exceptions become a displayable <see cref="Error"/>; cancellation is silent.</summary>
    protected async Task<bool> RunAsync(Func<CancellationToken, Task> work, string busyMessage, string errorContext, CancellationToken cancellationToken = default)
    {
        Error = null;
        IsBusy = true;
        BusyMessage = busyMessage;
        try
        {
            await work(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Context} failed", errorContext);
            Error = new ErrorViewModel(UserFacingError.From(ex, errorContext), e => { if (ReferenceEquals(Error, e)) Error = null; });
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }

    protected void ShowError(Exception exception, string context) =>
        Error = new ErrorViewModel(UserFacingError.From(exception, context), e => { if (ReferenceEquals(Error, e)) Error = null; });
}

/// <summary>A page hosted in the shell's content area.</summary>
public abstract partial class PageViewModel : ViewModelBase, IDisposable
{
    private readonly EventHandler<SessionSnapshot> _sessionHandler;

    protected PageViewModel(ConnectionManager connections, IUiDispatcher dispatcher, ILogger logger) : base(logger)
    {
        Connections = connections;
        Dispatcher = dispatcher;
        Session = connections.Current;
        _sessionHandler = (_, snapshot) => dispatcher.Post(() =>
        {
            var previous = Session;
            Session = snapshot;
            OnTenantSessionChanged(previous, snapshot);
        });
        connections.SessionChanged += _sessionHandler;
    }

    /// <summary>False for per-navigation pages (details, wizard), which are disposed when the user navigates away.</summary>
    public virtual bool KeepAlive => true;

    public void Dispose()
    {
        Connections.SessionChanged -= _sessionHandler;
        OnDisposed();
        GC.SuppressFinalize(this);
    }

    protected virtual void OnDisposed()
    {
    }

    protected ConnectionManager Connections { get; }

    protected IUiDispatcher Dispatcher { get; }

    public abstract string Title { get; }

    public abstract string Subtitle { get; }

    [ObservableProperty]
    private SessionSnapshot _session;

    public bool IsExchangeConnected => Session.IsExchangeConnected;

    public bool IsGraphConnected => Session.IsGraphConnected;

    public bool IsFullyConnected => Session.IsFullyConnected;

    partial void OnSessionChanged(SessionSnapshot value)
    {
        OnPropertyChanged(nameof(IsExchangeConnected));
        OnPropertyChanged(nameof(IsGraphConnected));
        OnPropertyChanged(nameof(IsFullyConnected));
    }

    public Task NavigatedToAsync(object? parameter) => OnNavigatedToAsync(parameter);

    protected virtual Task OnNavigatedToAsync(object? parameter) => Task.CompletedTask;

    /// <summary>Called on the UI thread when the session changes. Pages must drop tenant data when the session ID changes.</summary>
    protected virtual void OnTenantSessionChanged(SessionSnapshot previous, SessionSnapshot current)
    {
    }

    /// <summary>Optional refresh entry point (F5).</summary>
    public virtual Task RefreshAsync() => Task.CompletedTask;
}
