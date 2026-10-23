using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Mailboxes;
using ExchangeRbacStudio.Domain.Exchange;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.App.ViewModels.Shared;

public sealed partial class MailboxResultItem(MailboxCandidate candidate, MailboxPickerViewModel owner) : ObservableObject
{
    public MailboxCandidate Candidate { get; } = candidate;

    public string DisplayName => Candidate.Recipient.DisplayName;

    public string? Address => Candidate.Recipient.PrimarySmtpAddress;

    public string? Alias => Candidate.Recipient.Alias;

    public string TypeLabel => Candidate.Recipient.RecipientTypeDetails ?? "Unknown";

    public bool IsSupported => Candidate.Validation.IsSupported && owner.Accepts(Candidate.Recipient) is null;

    public string? Reason => Candidate.Validation.IsSupported ? owner.Accepts(Candidate.Recipient) : Candidate.Validation.Reason;

    public StatusKind TypeKind => Candidate.Validation.IsSupported ? StatusKind.Info : StatusKind.Neutral;

    [ObservableProperty] private bool _isSelected;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add() => owner.Add(Candidate);

    private bool CanAdd() => IsSupported && !IsSelected;

    partial void OnIsSelectedChanged(bool value) => AddCommand.NotifyCanExecuteChanged();
}

public sealed partial class SelectedMailboxItem(ExchangeRecipient recipient, MailboxPickerViewModel owner) : ObservableObject
{
    public ExchangeRecipient Recipient { get; } = recipient;

    public string DisplayName => Recipient.DisplayName;

    public string? Address => Recipient.PrimarySmtpAddress;

    public string TypeLabel => Recipient.RecipientTypeDetails ?? "Unknown";

    public string ExchangeGuid => Recipient.ExchangeGuid?.ToString() ?? "—";

    public string ObjectGuid => Recipient.Guid?.ToString() ?? "—";

    public string ValidationLabel => "Validated";

    public StatusKind ValidationKind => StatusKind.Success;

    [RelayCommand]
    private void Remove() => owner.Remove(this);
}

public sealed record ResolutionMessage(string Input, string Message, StatusKind Kind);

/// <summary>
/// Searchable multi-select mailbox picker. Every accepted mailbox has been resolved in Exchange Online and its actual
/// RecipientTypeDetails validated; unsupported recipients are shown with an explanation and cannot be added.
/// </summary>
public sealed partial class MailboxPickerViewModel : ViewModelBase
{
    private readonly MailboxDirectoryService _directory;
    private Func<ExchangeRecipient, string?> _additionalRule = _ => null;
    private CancellationTokenSource? _searchCts;

    public MailboxPickerViewModel(MailboxDirectoryService directory, ILogger<MailboxPickerViewModel> logger) : base(logger)
    {
        _directory = directory;
        Selected.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedCount));
            foreach (var result in Results) result.IsSelected = IsSelectedRecipient(result.Candidate.Recipient);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? SelectionChanged;

    public ObservableCollection<MailboxResultItem> Results { get; } = [];

    public ObservableCollection<SelectedMailboxItem> Selected { get; } = [];

    public ObservableCollection<ResolutionMessage> ResolutionMessages { get; } = [];

    public int MaxItems { get; set; } = 50;

    /// <summary>Upper bound for one pasted list, independent of the selection limit so partial adds can be reported.</summary>
    private const int PasteLimit = 500;

    public int SelectedCount => Selected.Count;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _pasteText = string.Empty;
    [ObservableProperty] private bool _hasSearched;

    public IReadOnlyList<ExchangeRecipient> SelectedRecipients => Selected.Select(s => s.Recipient).ToList();

    /// <summary>An additional acceptance rule (for example: negative test targets must not be in the intended scope).</summary>
    public void SetAdditionalRule(Func<ExchangeRecipient, string?> rule) => _additionalRule = rule;

    internal string? Accepts(ExchangeRecipient recipient) => _additionalRule(recipient);

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < 2) return;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = DebouncedSearchAsync(cts.Token);
    }

    private async Task DebouncedSearchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(400, token);
            await SearchCoreAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search.
        }
    }

    [RelayCommand]
    private Task SearchAsync()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        return SearchCoreAsync(cts.Token);
    }

    private Task SearchCoreAsync(CancellationToken token) => RunAsync(async ct =>
    {
        var results = await _directory.SearchAsync(SearchText, ct);
        ct.ThrowIfCancellationRequested();
        Results.Clear();
        foreach (var candidate in results)
            Results.Add(new MailboxResultItem(candidate, this) { IsSelected = IsSelectedRecipient(candidate.Recipient) });
        HasSearched = true;
    }, "Searching Exchange Online…", "Mailbox search", token);

    [RelayCommand]
    private Task AddPastedAsync() => RunAsync(async ct =>
    {
        ResolutionMessages.Clear();
        var resolutions = await _directory.ResolveManyAsync(PasteText, PasteLimit, ct);
        var limitReached = false;
        foreach (var resolution in resolutions)
        {
            if (resolution.Candidate is null)
            {
                ResolutionMessages.Add(new(resolution.Input, resolution.Error ?? "Not found.", StatusKind.Critical));
                continue;
            }

            if (!resolution.Candidate.Validation.IsSupported)
            {
                ResolutionMessages.Add(new(resolution.Input, $"{resolution.Candidate.Recipient.Label}: {resolution.Candidate.Validation.Reason}", StatusKind.Critical));
                continue;
            }

            var rule = Accepts(resolution.Candidate.Recipient);
            if (rule is not null)
            {
                ResolutionMessages.Add(new(resolution.Input, rule, StatusKind.Warning));
                continue;
            }

            if (Selected.Count >= MaxItems)
            {
                limitReached = true;
                ResolutionMessages.Add(new(resolution.Input, $"Not added: the maximum of {MaxItems} mailboxes per scope is reached.", StatusKind.Warning));
                continue;
            }

            if (Add(resolution.Candidate))
                ResolutionMessages.Add(new(resolution.Input, $"Added {resolution.Candidate.Recipient.Label}.", StatusKind.Success));
            else
                ResolutionMessages.Add(new(resolution.Input, "Already selected.", StatusKind.Info));
        }

        PasteText = string.Empty;
        if (limitReached)
            ShowError(new StudioOperationException(FailureKind.InvalidInput, $"At most {MaxItems} mailboxes can be selected for one scope (configurable in Settings). Some entries were not added."), "Add mailboxes");
    }, "Resolving mailboxes…", "Add mailboxes");

    public bool Add(MailboxCandidate candidate)
    {
        if (!candidate.Validation.IsSupported || Accepts(candidate.Recipient) is not null) return false;
        if (IsSelectedRecipient(candidate.Recipient)) return false;
        if (Selected.Count >= MaxItems)
        {
            ShowError(new StudioOperationException(FailureKind.InvalidInput, $"At most {MaxItems} mailboxes can be selected for one scope (configurable in Settings)."), "Add mailbox");
            return false;
        }

        Selected.Add(new SelectedMailboxItem(candidate.Recipient, this));
        return true;
    }

    public void Remove(SelectedMailboxItem item) => Selected.Remove(item);

    public void SetSelection(IEnumerable<ExchangeRecipient> recipients)
    {
        Selected.Clear();
        foreach (var recipient in recipients)
            Selected.Add(new SelectedMailboxItem(recipient, this));
    }

    [RelayCommand]
    private void ClearSelection() => Selected.Clear();

    private bool IsSelectedRecipient(ExchangeRecipient recipient) => Selected.Any(s => s.Recipient.IdentityKey == recipient.IdentityKey);
}
