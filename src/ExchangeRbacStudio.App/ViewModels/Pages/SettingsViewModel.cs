using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Diagnostics;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Application.Settings;
using ExchangeRbacStudio.Domain.Clouds;
using ExchangeRbacStudio.Domain.Scoping;
using ExchangeRbacStudio.Infrastructure.Exchange;
using ExchangeRbacStudio.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.ViewModels.Pages;

public sealed record StrategyOption(RecipientFilterStrategy Strategy, string Title, string Description);

/// <summary>Local, non-secret preferences and diagnostics.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsStore _store;
    private readonly ThemeService _themeService;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly IAuditLog _audit;
    private readonly IReportExporter _reports;
    private readonly StudioRuntimeInfo _runtime;

    public SettingsViewModel(ConnectionManager connections, IUiDispatcher dispatcher, ISettingsStore store, ThemeService theme, IDialogService dialogs,
        AppPaths paths, IAuditLog audit, IReportExporter reports, StudioRuntimeInfo runtime, ILogger<SettingsViewModel> logger) : base(connections, dispatcher, logger)
    {
        _store = store;
        _themeService = theme;
        _dialogs = dialogs;
        _paths = paths;
        _audit = audit;
        _reports = reports;
        _runtime = runtime;
        Load(store.Current);
    }

    public override string Title => "Settings";

    public override string Subtitle => "Preferences are stored locally in your user profile. No credentials or tokens are ever stored.";

    public IReadOnlyList<CloudEnvironmentProfile> Environments => CloudEnvironments.All;

    public IReadOnlyList<StrategyOption> Strategies { get; } =
    [
        new(RecipientFilterStrategy.ExternalDirectoryObjectId, "Microsoft Entra object ID (recommended)",
            "ExternalDirectoryObjectId -eq '<guid>'. Immutable, unique and shared with Microsoft Graph user IDs."),
        new(RecipientFilterStrategy.ObjectGuid, "Exchange object GUID",
            "Guid -eq '<guid>'. Immutable directory object identifier of the recipient."),
        new(RecipientFilterStrategy.ExchangeGuid, "Mailbox GUID",
            "ExchangeGuid -eq '<guid>'. Mailbox identifier; in hybrid organizations a mail user can carry the same value, which the overmatch check detects."),
    ];

    public ObservableCollection<string> ValidationErrors { get; } = [];

    public string Version => _runtime.Version;

    public string RuntimeDescription => $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}";

    public string LogLocation => _paths.Logs;

    public string AuditLocation => _audit.StorageLocation;

    public string ReportLocation => _reports.DefaultDirectory;

    public string SettingsLocation => _paths.SettingsFile;

    public string MicrosoftClientId => KnownPublicClients.MicrosoftGraphCommandLineTools;

    [ObservableProperty] private ThemePreference _theme;
    [ObservableProperty] private CloudEnvironmentProfile _environment = CloudEnvironments.Global;
    [ObservableProperty] private string _tenantHint = string.Empty;
    [ObservableProperty] private GraphClientMode _clientMode;
    [ObservableProperty] private string _customClientId = string.Empty;
    [ObservableProperty] private bool _useWam;
    [ObservableProperty] private string _powerShellPath = string.Empty;
    [ObservableProperty] private string? _detectedPowerShell;
    [ObservableProperty] private string _minimumModuleVersion = "3.7.0";
    [ObservableProperty] private string _scopeNameTemplate = ScopeNamingPolicy.DefaultScopeTemplate;
    [ObservableProperty] private string _assignmentNameTemplate = ScopeNamingPolicy.DefaultAssignmentTemplate;
    [ObservableProperty] private StrategyOption? _strategy;
    [ObservableProperty] private double? _maxMailboxes = 50;
    [ObservableProperty] private bool _requireTypedConfirmation = true;
    [ObservableProperty] private string? _namePreview;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private StatusKind _statusKind;

    partial void OnThemeChanged(ThemePreference value) => _themeService.Apply(value);

    partial void OnScopeNameTemplateChanged(string value) => UpdatePreview();

    partial void OnAssignmentNameTemplateChanged(string value) => UpdatePreview();

    protected override Task OnNavigatedToAsync(object? parameter)
    {
        Load(_store.Current);
        try
        {
            DetectedPowerShell = PowerShellLocator.Locate(null);
        }
        catch (StudioOperationException ex)
        {
            DetectedPowerShell = ex.Message;
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task Save() => RunAsync(async ct =>
    {
        var updated = Build();
        ValidationErrors.Clear();
        foreach (var error in updated.Validate()) ValidationErrors.Add(error);
        if (ValidationErrors.Count > 0)
        {
            StatusMessage = "Settings were not saved. Correct the highlighted values.";
            StatusKind = StatusKind.Critical;
            return;
        }

        var reconnectNeeded = (Session.IsGraphConnected || Session.IsExchangeConnected)
            && (updated.Environment != _store.Current.Environment || updated.EffectiveGraphClientId != _store.Current.EffectiveGraphClientId || updated.TenantHint != _store.Current.TenantHint);
        await _store.SaveAsync(updated, ct);
        StatusMessage = reconnectNeeded ? "Settings saved. Disconnect and connect again to apply the new connection settings." : "Settings saved.";
        StatusKind = StatusKind.Success;
    }, "Saving…", "Save settings");

    [RelayCommand]
    private void Reset()
    {
        Load(new StudioSettings { GraphClient = _store.Current.GraphClient, CustomGraphClientId = _store.Current.CustomGraphClientId });
        StatusMessage = "Defaults restored. Save to apply.";
        StatusKind = StatusKind.Info;
    }

    [RelayCommand]
    private void BrowsePowerShell()
    {
        var path = _dialogs.PickPowerShellExecutable(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "PowerShell"));
        if (path is not null) PowerShellPath = path;
    }

    [RelayCommand]
    private void OpenLogs() => ShellLauncher.OpenFolder(_paths.Logs);

    [RelayCommand]
    private void OpenAudit() => ShellLauncher.OpenFolder(_audit.StorageLocation);

    [RelayCommand]
    private void OpenReports() => ShellLauncher.OpenFolder(_reports.DefaultDirectory);

    /// <summary>Exports logs (already redacted) and an environment summary. No tokens or credentials are included.</summary>
    [RelayCommand]
    private Task ExportDiagnostics() => RunAsync(async ct =>
    {
        var folder = _dialogs.PickFolder("Choose a folder for the diagnostics package", System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop));
        if (folder is null) return;
        var target = Path.Combine(folder, $"ExchangeRbacStudio-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var session = Session;
        var summary = string.Join(System.Environment.NewLine,
            $"Exchange RBAC Studio {Version}",
            RuntimeDescription,
            $"Environment: {session.Environment.DisplayName}",
            $"Graph: {session.Graph}; Exchange: {session.Exchange}",
            $"Tenant: {session.TenantId}",
            $"PowerShell: {session.PowerShellVersion}; ExchangeOnlineManagement: {session.ExchangeModuleVersion}",
            $"Missing cmdlets: {string.Join(", ", session.MissingExchangeCommands)}",
            $"Settings: theme={_store.Current.Theme}; strategy={_store.Current.FilterStrategy}; maxMailboxes={_store.Current.MaxMailboxesPerScope}; wam={_store.Current.UseWebAccountManagerForExchange}");

        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(target, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("environment.txt");
            using (var writer = new StreamWriter(entry.Open())) writer.Write(SensitiveDataRedactor.Redact(summary));
            if (Directory.Exists(_paths.Logs))
            {
                foreach (var log in new DirectoryInfo(_paths.Logs).EnumerateFiles("*.log").OrderByDescending(f => f.LastWriteTime).Take(5))
                {
                    using var source = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(source);
                    var logEntry = zip.CreateEntry("logs/" + log.Name);
                    using var logWriter = new StreamWriter(logEntry.Open());
                    logWriter.Write(SensitiveDataRedactor.Redact(reader.ReadToEnd()));
                }
            }
        }, ct);

        ShellLauncher.SelectFile(target);
        StatusMessage = $"Diagnostics exported to {target}.";
        StatusKind = StatusKind.Success;
    }, "Exporting diagnostics…", "Export diagnostics");

    private void Load(StudioSettings s)
    {
        Theme = s.Theme;
        Environment = CloudEnvironments.Get(s.Environment);
        TenantHint = s.TenantHint ?? string.Empty;
        ClientMode = s.GraphClient;
        CustomClientId = s.CustomGraphClientId ?? string.Empty;
        UseWam = s.UseWebAccountManagerForExchange;
        PowerShellPath = s.PowerShellPath ?? string.Empty;
        MinimumModuleVersion = s.MinimumExchangeModuleVersion;
        ScopeNameTemplate = s.ScopeNameTemplate;
        AssignmentNameTemplate = s.AssignmentNameTemplate;
        Strategy = Strategies.First(o => o.Strategy == s.FilterStrategy);
        MaxMailboxes = s.MaxMailboxesPerScope;
        RequireTypedConfirmation = s.RequireTypedConfirmationForRemovals;
        UpdatePreview();
    }

    private StudioSettings Build() => _store.Current with
    {
        Theme = Theme,
        Environment = Environment.Environment,
        TenantHint = string.IsNullOrWhiteSpace(TenantHint) ? null : TenantHint.Trim(),
        GraphClient = ClientMode,
        CustomGraphClientId = string.IsNullOrWhiteSpace(CustomClientId) ? null : CustomClientId.Trim(),
        UseWebAccountManagerForExchange = UseWam,
        PowerShellPath = string.IsNullOrWhiteSpace(PowerShellPath) ? null : PowerShellPath.Trim(),
        MinimumExchangeModuleVersion = MinimumModuleVersion.Trim(),
        ScopeNameTemplate = ScopeNameTemplate.Trim(),
        AssignmentNameTemplate = AssignmentNameTemplate.Trim(),
        FilterStrategy = Strategy?.Strategy ?? RecipientFilterStrategy.ExternalDirectoryObjectId,
        MaxMailboxesPerScope = (int)Math.Round(MaxMailboxes ?? 50),
        RequireTypedConfirmationForRemovals = RequireTypedConfirmation,
    };

    /// <summary>Obviously synthetic identity used only to render the naming preview; never sent to a tenant.</summary>
    private static readonly Guid PreviewSampleAppId = new("11111111-2222-3333-4444-555555555555");

    private void UpdatePreview()
    {
        var context = new NamingContext("Sample App", PreviewSampleAppId, ["Application Mail.Read"], 3, DateTimeOffset.UtcNow);
        NamePreview = $"Scope: {ScopeNamingPolicy.RenderScopeName(ScopeNameTemplate, context)}    Assignment: {ScopeNamingPolicy.RenderAssignmentName(AssignmentNameTemplate, context, "Application Mail.Read")}";
    }

    public static string AssemblyVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
}
