using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.App.Services;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Auditing;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.ViewModels.Pages;

public sealed record AuditEntryItem(AuditEntry Entry)
{
    public string Time => Entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Action => Entry.Action;
    public string? Target => Entry.Target;
    public string Outcome => Entry.Outcome.ToString();
    public StatusKind Kind => OutcomeKind(Entry.Outcome);
    public string Message => Entry.Message;
    public string? Command => Entry.Command;

    internal static StatusKind OutcomeKind(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Succeeded => StatusKind.Success,
        AuditOutcome.Reused or AuditOutcome.DryRun or AuditOutcome.Information => StatusKind.Info,
        AuditOutcome.Started or AuditOutcome.Skipped => StatusKind.Neutral,
        AuditOutcome.Warning => StatusKind.Warning,
        _ => StatusKind.Critical,
    };
}

public sealed record HistoryItem(
    DateTimeOffset Timestamp,
    string Title,
    string Category,
    string? Target,
    string OutcomeLabel,
    StatusKind Kind,
    string? Administrator,
    string? Tenant,
    IReadOnlyList<AuditEntryItem> Entries)
{
    public string Time => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed record ReportFileItem(string Path, DateTime Modified)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string When => Modified.ToString("g");
}

/// <summary>Local audit log (hash-chained) and generated reports.</summary>
public sealed partial class ChangeHistoryViewModel : PageViewModel
{
    private readonly IAuditLog _audit;
    private readonly IReportExporter _reports;

    public ChangeHistoryViewModel(ConnectionManager connections, IUiDispatcher dispatcher, IAuditLog audit, IReportExporter reports, ILogger<ChangeHistoryViewModel> logger)
        : base(connections, dispatcher, logger)
    {
        _audit = audit;
        _reports = reports;
    }

    public override string Title => "Change History";

    public override string Subtitle => "Audit trail of sessions, validations, changes and verifications recorded on this computer.";

    public ObservableCollection<HistoryItem> Items { get; } = [];

    public ObservableCollection<ReportFileItem> Reports { get; } = [];

    public string AuditLocation => _audit.StorageLocation;

    public string ReportLocation => _reports.DefaultDirectory;

    [ObservableProperty] private HistoryItem? _selectedItem;
    [ObservableProperty] private bool _allTenants;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string? _integrityMessage;
    [ObservableProperty] private StatusKind _integrityKind;

    partial void OnAllTenantsChanged(bool value) => _ = RefreshAsync();

    partial void OnSearchTextChanged(string value) => _ = RefreshAsync();

    protected override Task OnNavigatedToAsync(object? parameter) => RefreshAsync();

    public override Task RefreshAsync() => RefreshCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task Refresh() => RunAsync(async ct =>
    {
        var entries = await _audit.ReadAsync(new AuditQuery(AllTenants ? null : Session.TenantId, MaxEntries: 5000), ct);
        var items = new List<HistoryItem>();
        foreach (var group in entries.Where(e => e.RunId is not null).GroupBy(e => e.RunId))
        {
            var ordered = group.OrderBy(e => e.TimestampUtc).ToList();
            var first = ordered[0];
            var end = ordered.LastOrDefault(e => e.Action is "Execution completed" or "Dry run completed");
            items.Add(new HistoryItem(first.TimestampUtc, first.Message, first.Category, first.Target,
                end is null ? (first.Category == AuditCategories.Change ? "Incomplete" : first.Outcome.ToString()) : end.Outcome.ToString(),
                end is null ? (first.Category == AuditCategories.Change ? StatusKind.Warning : AuditEntryItem.OutcomeKind(first.Outcome)) : AuditEntryItem.OutcomeKind(end.Outcome),
                first.Administrator, first.TenantName, ordered.Select(e => new AuditEntryItem(e)).ToList()));
        }

        foreach (var entry in entries.Where(e => e.RunId is null))
        {
            items.Add(new HistoryItem(entry.TimestampUtc, $"{entry.Action}: {entry.Message}", entry.Category, entry.Target, entry.Outcome.ToString(),
                AuditEntryItem.OutcomeKind(entry.Outcome), entry.Administrator, entry.TenantName, [new AuditEntryItem(entry)]));
        }

        var term = SearchText.Trim();
        Items.Clear();
        foreach (var item in items
                     .Where(i => term.Length == 0 || i.Title.Contains(term, StringComparison.OrdinalIgnoreCase) || (i.Target?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                                 || (i.Administrator?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                     .OrderByDescending(i => i.Timestamp))
            Items.Add(item);

        Reports.Clear();
        if (Directory.Exists(_reports.DefaultDirectory))
        {
            foreach (var file in new DirectoryInfo(_reports.DefaultDirectory).EnumerateFiles("*.html").OrderByDescending(f => f.LastWriteTime).Take(50))
                Reports.Add(new ReportFileItem(file.FullName, file.LastWriteTime));
        }
    }, "Reading audit log…", "Read audit log");

    [RelayCommand]
    private Task VerifyIntegrity() => RunAsync(async ct =>
    {
        var result = await _audit.VerifyIntegrityAsync(ct);
        IntegrityKind = result.IsIntact ? StatusKind.Success : StatusKind.Critical;
        IntegrityMessage = result.IsIntact
            ? $"Audit log intact: {result.EntriesVerified} entries verified, hash chain unbroken."
            : $"Integrity problems found ({result.Problems.Count}): " + string.Join(" ", result.Problems.Take(5));
    }, "Verifying the audit hash chain…", "Verify audit log");

    [RelayCommand]
    private void OpenAuditFolder() => ShellLauncher.OpenFolder(_audit.StorageLocation);

    [RelayCommand]
    private void OpenReportsFolder() => ShellLauncher.OpenFolder(_reports.DefaultDirectory);

    [RelayCommand]
    private void OpenReport(ReportFileItem? report)
    {
        if (report is not null) ShellLauncher.OpenFile(report.Path);
    }

    protected override void OnTenantSessionChanged(SessionSnapshot previous, SessionSnapshot current)
    {
        if (previous.TenantId != current.TenantId && !AllTenants) _ = RefreshAsync();
    }
}
