using System.Windows;
using ExchangeRbacStudio.App.ViewModels.Dialogs;
using ExchangeRbacStudio.App.Views.Dialogs;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace ExchangeRbacStudio.App.Services;

public interface IDialogService
{
    /// <summary>Shows sign-in options and connects. Returns true when both services are connected.</summary>
    bool ShowConnectDialog();

    /// <summary>Reviews and (after explicit confirmation) executes a change plan. Returns the result, or null if nothing ran.</summary>
    ExecutionResult? ShowChangeReview(ChangeReviewRequest request);

    bool Confirm(string title, string message, string confirmLabel, bool destructive);

    void ShowError(UserFacingError error);

    string? PickFolder(string title, string? initialDirectory);

    string? PickPowerShellExecutable(string? initialDirectory);
}

public sealed class DialogService(IServiceProvider services, ExchangeRbacStudio.Application.Abstractions.ISettingsStore settings) : IDialogService
{
    public bool ShowConnectDialog()
    {
        using var vm = services.GetRequiredService<ConnectDialogViewModel>();
        var window = new ConnectDialog { DataContext = vm, Owner = Owner() };
        vm.RequestClose += (_, _) => window.Close();
        window.ShowDialog();
        return services.GetRequiredService<ExchangeRbacStudio.Application.Sessions.ConnectionManager>().Current.IsFullyConnected;
    }

    public ExecutionResult? ShowChangeReview(ChangeReviewRequest request)
    {
        var vm = services.GetRequiredService<ChangeReviewViewModel>();
        // Typed confirmation of the object name can be relaxed in Settings; the confirmation checkbox always remains.
        vm.Initialize(settings.Current.RequireTypedConfirmationForRemovals ? request : request with { TypedConfirmationText = null });
        var window = new ChangeReviewDialog { DataContext = vm, Owner = Owner() };
        vm.RequestClose += (_, _) => window.Close();
        window.ShowDialog();
        return vm.Result;
    }

    public bool Confirm(string title, string message, string confirmLabel, bool destructive)
    {
        var window = new ConfirmDialog(title, message, confirmLabel, destructive) { Owner = Owner() };
        return window.ShowDialog() == true;
    }

    public void ShowError(UserFacingError error)
    {
        var window = new ConfirmDialog(error.Title, error.Message + (error.Remediation is null ? string.Empty : "\n\n" + error.Remediation), "OK", false, showCancel: false)
        {
            Owner = Owner(),
        };
        window.ShowDialog();
    }

    public string? PickFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = initialDirectory ?? string.Empty };
        return dialog.ShowDialog(Owner()) == true ? dialog.FolderName : null;
    }

    public string? PickPowerShellExecutable(string? initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select pwsh.exe (PowerShell 7)",
            Filter = "PowerShell 7|pwsh.exe",
            InitialDirectory = initialDirectory ?? string.Empty,
            CheckFileExists = true,
        };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    private static Window? Owner() => System.Windows.Application.Current?.MainWindow is { IsLoaded: true } main ? main : null;
}
