using System.Windows.Controls;

namespace ExchangeRbacStudio.App.Views.Pages;

// Page views are pure XAML bound to their view models; no logic lives in code-behind.

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();
}

public partial class ApplicationsView : UserControl
{
    public ApplicationsView() => InitializeComponent();
}

public partial class ApplicationDetailView : UserControl
{
    public ApplicationDetailView() => InitializeComponent();
}

public partial class ScopesView : UserControl
{
    public ScopesView() => InitializeComponent();
}

public partial class RoleAssignmentsView : UserControl
{
    public RoleAssignmentsView() => InitializeComponent();
}

public partial class AuthorizationTesterView : UserControl
{
    public AuthorizationTesterView() => InitializeComponent();
}

public partial class ChangeHistoryView : UserControl
{
    public ChangeHistoryView() => InitializeComponent();
}

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
