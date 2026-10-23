using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.App.ViewModels;
using Wpf.Ui.Controls;

namespace ExchangeRbacStudio.App.Controls;

/// <summary>Exposes status controls to UI Automation (screen readers, automated tests) as text with the status as name.</summary>
internal sealed class StatusTextAutomationPeer(FrameworkElement owner, Func<string?> text) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

    protected override string GetClassNameCore() => Owner.GetType().Name;

    protected override string GetNameCore()
    {
        var explicitName = base.GetNameCore();
        return string.IsNullOrEmpty(explicitName) ? text() ?? string.Empty : explicitName;
    }

    protected override bool IsControlElementCore() => true;

    protected override bool IsContentElementCore() => true;
}

/// <summary>Pill-shaped status label. Colours come from theme resources via template triggers on <see cref="Kind"/>.</summary>
public class StatusBadge : Control
{
    protected override AutomationPeer OnCreateAutomationPeer() => new StatusTextAutomationPeer(this, () => Text);

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(StatusKind), typeof(StatusBadge), new PropertyMetadata(StatusKind.Neutral));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusBadge), new PropertyMetadata(string.Empty));

    static StatusBadge() => DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusBadge), new FrameworkPropertyMetadata(typeof(StatusBadge)));

    public StatusKind Kind
    {
        get => (StatusKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}

/// <summary>Status icon (success, warning, critical, info, progress) coloured by theme resources.</summary>
public class StatusGlyph : Control
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(StatusKind), typeof(StatusGlyph), new PropertyMetadata(StatusKind.Neutral));

    static StatusGlyph() => DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusGlyph), new FrameworkPropertyMetadata(typeof(StatusGlyph)));

    protected override AutomationPeer OnCreateAutomationPeer() => new StatusTextAutomationPeer(this, () => Kind == StatusKind.Neutral ? null : Kind.ToString());

    public StatusKind Kind
    {
        get => (StatusKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
}

/// <summary>A status glyph followed by text that wraps within the available width.</summary>
public class StatusMessage : Control
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(StatusKind), typeof(StatusMessage), new PropertyMetadata(StatusKind.Neutral));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusMessage), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsEmphasizedProperty =
        DependencyProperty.Register(nameof(IsEmphasized), typeof(bool), typeof(StatusMessage), new PropertyMetadata(false));

    static StatusMessage() => DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusMessage), new FrameworkPropertyMetadata(typeof(StatusMessage)));

    protected override AutomationPeer OnCreateAutomationPeer() => new StatusTextAutomationPeer(this, () => $"{Kind}: {Text}");

    public StatusKind Kind
    {
        get => (StatusKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsEmphasized
    {
        get => (bool)GetValue(IsEmphasizedProperty);
        set => SetValue(IsEmphasizedProperty, value);
    }
}

/// <summary>Friendly empty/disconnected state with an icon, title, message and optional action content.</summary>
public class EmptyState : ContentControl
{
    public static readonly DependencyProperty SymbolProperty =
        DependencyProperty.Register(nameof(Symbol), typeof(SymbolRegular), typeof(EmptyState), new PropertyMetadata(SymbolRegular.Info24));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));

    static EmptyState() => DefaultStyleKeyProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(typeof(EmptyState)));

    public SymbolRegular Symbol
    {
        get => (SymbolRegular)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }
}

/// <summary>Indeterminate progress ring with a status message.</summary>
public class LoadingIndicator : Control
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(LoadingIndicator), new PropertyMetadata("Loading…"));

    static LoadingIndicator() => DefaultStyleKeyProperty.OverrideMetadata(typeof(LoadingIndicator), new FrameworkPropertyMetadata(typeof(LoadingIndicator)));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}

/// <summary>Read-only, selectable text with a copy button (identifiers, filters, commands).</summary>
public class CopyableText : Control
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(CopyableText), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsMonospaceProperty =
        DependencyProperty.Register(nameof(IsMonospace), typeof(bool), typeof(CopyableText), new PropertyMetadata(true));

    static CopyableText() => DefaultStyleKeyProperty.OverrideMetadata(typeof(CopyableText), new FrameworkPropertyMetadata(typeof(CopyableText)));

    public CopyableText()
    {
        CopyCommand = new RelayCommand(() => ClipboardHelper.TrySetText(Text));
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsMonospace
    {
        get => (bool)GetValue(IsMonospaceProperty);
        set => SetValue(IsMonospaceProperty, value);
    }

    public ICommand CopyCommand { get; }
}

/// <summary>Error banner with remediation and an expandable, sanitized technical diagnostics panel.</summary>
public class ErrorPanel : Control
{
    public static readonly DependencyProperty ErrorProperty =
        DependencyProperty.Register(nameof(Error), typeof(ErrorViewModel), typeof(ErrorPanel), new PropertyMetadata(null));

    static ErrorPanel() => DefaultStyleKeyProperty.OverrideMetadata(typeof(ErrorPanel), new FrameworkPropertyMetadata(typeof(ErrorPanel)));

    public ErrorViewModel? Error
    {
        get => (ErrorViewModel?)GetValue(ErrorProperty);
        set => SetValue(ErrorProperty, value);
    }
}
