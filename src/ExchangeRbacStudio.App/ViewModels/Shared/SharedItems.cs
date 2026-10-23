using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ExchangeRbacStudio.Application.Execution;
using ExchangeRbacStudio.Domain.Planning;
using ExchangeRbacStudio.Domain.Validation;

namespace ExchangeRbacStudio.App.ViewModels.Shared;

/// <summary>Live view of one execution step.</summary>
public sealed partial class ExecutionStepItem : ObservableObject
{
    public ExecutionStepItem(StepResult step) => Update(step);

    public int Index { get; private set; }

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string? _command;
    [ObservableProperty] private string? _errorDetail;
    [ObservableProperty] private StepStatus _status;
    [ObservableProperty] private StatusKind _kind;
    [ObservableProperty] private string _statusLabel = string.Empty;
    [ObservableProperty] private string? _duration;

    public void Update(StepResult step)
    {
        Index = step.Index;
        Title = step.Title;
        Message = step.Message;
        Command = step.Command;
        ErrorDetail = step.ErrorDetail;
        Status = step.Status;
        Kind = StatusMapper.From(step.Status);
        StatusLabel = step.Status switch
        {
            StepStatus.Pending => "Pending",
            StepStatus.Running => "Running",
            StepStatus.Succeeded => "Succeeded",
            StepStatus.Reused => "Reused",
            StepStatus.DryRun => "Dry run",
            StepStatus.NotRun => "Not run",
            StepStatus.Failed => "Failed",
            _ => step.Status.ToString(),
        };
        Duration = step.Duration is { } d ? $"{d.TotalSeconds:0.0}s" : null;
    }

    public static ObservableCollection<ExecutionStepItem> CreateFor(ChangePlan plan) =>
        new(ChangeExecutor.CreateInitialSteps(plan).Select(s => new ExecutionStepItem(s)));
}

/// <summary>One planned operation for review screens.</summary>
public sealed class OperationItem(PlannedOperation operation)
{
    public PlannedOperation Operation { get; } = operation;

    public int Sequence => Operation.Sequence;

    public string Summary => Operation.Summary;

    public string? Command => Operation.Command?.ToPreview();

    public string? BeforeState => Operation.BeforeState;

    public string? AfterState => Operation.AfterState;

    public bool HasDiff => BeforeState is not null || AfterState is not null;

    public string DispositionLabel => Operation.Disposition switch
    {
        OperationDisposition.Create => "Create",
        OperationDisposition.Update => "Change",
        OperationDisposition.Remove => "Remove",
        _ => "Reuse",
    };

    public StatusKind DispositionKind => Operation.Disposition switch
    {
        OperationDisposition.Create => StatusKind.Success,
        OperationDisposition.Update => StatusKind.Warning,
        OperationDisposition.Remove => StatusKind.Critical,
        _ => StatusKind.Neutral,
    };
}

/// <summary>A preflight check for display.</summary>
public sealed partial class CheckItem(PreflightCheck check) : ObservableObject
{
    public PreflightCheck Check { get; } = check;

    public string Title => Check.Title + (Check.IsSecurityCritical ? " (security-critical)" : string.Empty);

    public string Summary => Check.Summary;

    public string? Detail => Check.Detail;

    public string? Remediation => Check.Remediation;

    public StatusKind Kind => StatusMapper.From(Check.Status);

    public string StatusLabel => StatusMapper.Label(Check.Status) + (Check.CanAcknowledge && Check.IsBlocking ? " (acknowledgeable)" : string.Empty);

    public bool HasMore => !string.IsNullOrWhiteSpace(Check.Detail) || !string.IsNullOrWhiteSpace(Check.Remediation);

    [ObservableProperty] private bool _isExpanded = check.Status is CheckStatus.Blocked or CheckStatus.UnableToVerify;
}

/// <summary>A titled list of name/value facts.</summary>
public sealed record FactItem(string Label, string? Value, bool IsIdentifier = false);
