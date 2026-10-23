using System.Windows;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.App.ViewModels;
using Wpf.Ui.Controls;

namespace ExchangeRbacStudio.App.Views;

public partial class MainWindow : FluentWindow
{
    internal const double CompactPaneThreshold = 1180;
    private readonly ShellViewModel _shell;

    public MainWindow(ShellViewModel shell, ThemeService theme)
    {
        _shell = shell;
        DataContext = shell;
        InitializeComponent();
        theme.Attach(this);
        SizeChanged += (_, e) => _shell.IsPaneCompact = e.NewSize.Width < CompactPaneThreshold;
        Loaded += (_, _) =>
        {
            _shell.IsPaneCompact = ActualWidth < CompactPaneThreshold;
            _shell.Start();
        };
    }
}
