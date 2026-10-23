using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ExchangeRbacStudio.App.Views.Dialogs;

public partial class ConnectDialog : FluentWindow
{
    public ConnectDialog() => InitializeComponent();
}

public partial class ChangeReviewDialog : FluentWindow
{
    public ChangeReviewDialog()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            // Never close while a change is executing.
            if (DataContext is ViewModels.Dialogs.ChangeReviewViewModel { IsExecuting: true }) e.Cancel = true;
        };
    }
}

/// <summary>Simple confirmation/message dialog built in code (no external dialog host required).</summary>
public sealed class ConfirmDialog : FluentWindow
{
    public ConfirmDialog(string title, string message, string confirmLabel, bool destructive, bool showCancel = true)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 720;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        AutomationProperties_SetId(this, "ConfirmDialog");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TitleBar { Title = title, ShowMaximize = false, ShowMinimize = false });

        var body = new StackPanel { Margin = new Thickness(28, 8, 28, 20) };
        body.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var text = new TextBlock { Text = message, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        body.Children.Add(new ScrollViewer { Content = text, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var footer = new Border { Padding = new Thickness(28, 14, 28, 14), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (showCancel)
        {
            var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            cancel.Click += (_, _) => DialogResult = false;
            buttons.Children.Add(cancel);
        }

        var confirm = new Wpf.Ui.Controls.Button
        {
            Content = confirmLabel,
            Appearance = destructive ? ControlAppearance.Danger : ControlAppearance.Primary,
            IsDefault = !destructive,
        };
        confirm.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(confirm);
        footer.Child = buttons;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
    }

    private static void AutomationProperties_SetId(DependencyObject element, string id) =>
        System.Windows.Automation.AutomationProperties.SetAutomationId(element, id);
}
