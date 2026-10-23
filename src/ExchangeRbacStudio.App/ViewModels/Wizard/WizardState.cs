using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.App.ViewModels.Shared;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Applications;
using ExchangeRbacStudio.Application.Auditing;
using ExchangeRbacStudio.Application.Authorization;
using ExchangeRbacStudio.Application.Configuration;
using ExchangeRbacStudio.Application.Execution;
using ExchangeRbacStudio.Application.Inventory;
using ExchangeRbacStudio.Application.Mailboxes;
using ExchangeRbacStudio.Application.Permissions;
using ExchangeRbacStudio.Application.Scoping;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Identity;
using ExchangeRbacStudio.Domain.Planning;
using ExchangeRbacStudio.Domain.Scoping;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.ViewModels.Wizard;

/// <summary>How the wizard was started: blank, or pre-filled from an existing application configuration.</summary>
public sealed record WizardStartOptions(
    EntraServicePrincipal ServicePrincipal,
    IReadOnlyList<ExchangeRecipient>? Mailboxes = null,
    IReadOnlyList<string>? Roles = null,
    string? ExistingScopeName = null)
{
    public bool IsModify => ExistingScopeName is not null;
}

/// <summary>Services used by the wizard steps (aggregated to keep step constructors small).</summary>
public sealed record WizardServices(
    ConnectionManager Connections,
    ApplicationIdentityService Identity,
    RbacInventoryService Inventory,
    MailboxDirectoryService Mailboxes,
    Func<MailboxPickerViewModel> PickerFactory,
    RoleCatalogService Roles,
    PreflightService Preflight,
    ScopeDesigner ScopeDesigner,
    ConfigurationStateReader StateReader,
    ChangeExecutor Executor,
    ManagementPlanService Plans,
    AuthorizationTestService Tests,
    IReportExporter Reports,
    AuditRecorder Audit,
    ISettingsStore Settings,
    IDialogService Dialogs,
    NavigationService Navigation,
    StudioRuntimeInfo Runtime,
    TimeProvider Time,
    ILoggerFactory LoggerFactory);

/// <summary>State shared across wizard steps. Changing an earlier input invalidates every later result.</summary>
public sealed class WizardState
{
    public Guid SessionId { get; set; }

    public ConfigurationMode Mode { get; set; } = ConfigurationMode.New;

    public string? ExistingScopeName { get; set; }

    public ApplicationIdentityDetails? Application { get; set; }

    public IReadOnlyList<ExchangeRecipient> Mailboxes { get; set; } = [];

    public IReadOnlyList<string> Roles { get; set; } = [];

    public RecipientFilterStrategy Strategy { get; set; } = RecipientFilterStrategy.ExternalDirectoryObjectId;

    public string? ScopeFilter { get; set; }

    public string? ScopeName { get; set; }

    public bool ScopeNameEditedByUser { get; set; }

    public PreflightOutcome? Preflight { get; set; }

    public string? PreflightInputKey { get; set; }

    public bool Acknowledged { get; set; }

    public ChangePlan? ValidatedPlan { get; set; }

    public ExecutionResult? Execution { get; set; }

    public AuthorizationTestReport? Tests { get; set; }

    public string? ReportBaseName { get; set; }

    public bool HasExecuted => Execution is { DryRun: false };

    /// <summary>Key over all inputs that influence preflight; a change means preflight must run again.</summary>
    public string InputKey =>
        string.Join("|",
            Application?.ServicePrincipal.ObjectId.ToString() ?? "-",
            string.Join(",", Mailboxes.Select(m => m.IdentityKey).Order()),
            string.Join(",", Roles.Order(StringComparer.OrdinalIgnoreCase)),
            Strategy,
            ScopeName ?? "-",
            Mode,
            ExistingScopeName ?? "-");

    public ConfigurationRequest BuildRequest() => new(
        Application?.ServicePrincipal ?? throw new InvalidOperationException("No application selected."),
        Mailboxes,
        Roles,
        ScopeName ?? throw new InvalidOperationException("No scope name."),
        ScopeFilter ?? throw new InvalidOperationException("No scope filter."),
        Strategy,
        Mode,
        ExistingScopeName);

    /// <summary>Clears results that depend on the inputs of the given step index and later.</summary>
    public void InvalidateAfter(int stepIndex)
    {
        if (stepIndex <= 2)
        {
            ScopeFilter = null;
            if (!ScopeNameEditedByUser && Mode == ConfigurationMode.New) ScopeName = null;
        }

        if (stepIndex <= 4)
        {
            Preflight = null;
            PreflightInputKey = null;
            Acknowledged = false;
        }

        ValidatedPlan = null;
    }
}
