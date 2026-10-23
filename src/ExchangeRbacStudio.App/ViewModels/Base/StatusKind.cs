using ExchangeRbacStudio.Application.Execution;
using ExchangeRbacStudio.Application.Scoping;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Authorization;
using ExchangeRbacStudio.Domain.Permissions;
using ExchangeRbacStudio.Domain.Security;
using ExchangeRbacStudio.Domain.Validation;

namespace ExchangeRbacStudio.App.ViewModels;

/// <summary>Visual status vocabulary shared by badges, glyphs and banners.</summary>
public enum StatusKind
{
    Neutral,
    Info,
    Success,
    Warning,
    Critical,
    Progress,
}

public static class StatusMapper
{
    public static StatusKind From(CheckStatus status) => status switch
    {
        CheckStatus.Passed => StatusKind.Success,
        CheckStatus.Info => StatusKind.Info,
        CheckStatus.Warning => StatusKind.Warning,
        CheckStatus.UnableToVerify => StatusKind.Warning,
        CheckStatus.Blocked => StatusKind.Critical,
        _ => StatusKind.Neutral,
    };

    public static string Label(CheckStatus status) => status switch
    {
        CheckStatus.Passed => "Passed",
        CheckStatus.Info => "Info",
        CheckStatus.Warning => "Warning",
        CheckStatus.UnableToVerify => "Unable to verify",
        CheckStatus.Blocked => "Blocked",
        _ => status.ToString(),
    };

    public static StatusKind From(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical or FindingSeverity.High => StatusKind.Critical,
        FindingSeverity.Warning => StatusKind.Warning,
        _ => StatusKind.Info,
    };

    public static StatusKind From(ServiceConnectionState state) => state switch
    {
        ServiceConnectionState.Connected => StatusKind.Success,
        ServiceConnectionState.Connecting => StatusKind.Progress,
        ServiceConnectionState.Expired => StatusKind.Warning,
        ServiceConnectionState.Failed => StatusKind.Critical,
        _ => StatusKind.Neutral,
    };

    public static string Label(ServiceConnectionState state) => state switch
    {
        ServiceConnectionState.Connected => "Connected",
        ServiceConnectionState.Connecting => "Connecting…",
        ServiceConnectionState.Expired => "Session expired",
        ServiceConnectionState.Failed => "Failed",
        _ => "Not connected",
    };

    public static StatusKind From(StepStatus status) => status switch
    {
        StepStatus.Succeeded => StatusKind.Success,
        StepStatus.Reused => StatusKind.Info,
        StepStatus.DryRun => StatusKind.Info,
        StepStatus.Running => StatusKind.Progress,
        StepStatus.Failed => StatusKind.Critical,
        StepStatus.NotRun => StatusKind.Warning,
        _ => StatusKind.Neutral,
    };

    public static StatusKind From(ExecutionOutcome outcome) => outcome switch
    {
        ExecutionOutcome.Completed => StatusKind.Success,
        ExecutionOutcome.DryRunCompleted => StatusKind.Info,
        ExecutionOutcome.PartiallyCompleted => StatusKind.Warning,
        _ => StatusKind.Critical,
    };

    public static string Label(ExecutionOutcome outcome) => outcome switch
    {
        ExecutionOutcome.Completed => "Completed",
        ExecutionOutcome.DryRunCompleted => "Dry run completed",
        ExecutionOutcome.PartiallyCompleted => "Partially completed",
        _ => "Failed",
    };

    public static StatusKind From(AuthorizationVerdict verdict) => verdict switch
    {
        AuthorizationVerdict.Pass => StatusKind.Success,
        AuthorizationVerdict.Fail => StatusKind.Critical,
        _ => StatusKind.Warning,
    };

    public static StatusKind From(ScopeVerificationStatus status) => status switch
    {
        ScopeVerificationStatus.Verified => StatusKind.Success,
        ScopeVerificationStatus.Unverifiable => StatusKind.Warning,
        _ => StatusKind.Critical,
    };

    public static StatusKind From(PrivilegeLevel level) => level switch
    {
        PrivilegeLevel.Critical => StatusKind.Critical,
        PrivilegeLevel.High => StatusKind.Warning,
        PrivilegeLevel.Moderate => StatusKind.Info,
        _ => StatusKind.Neutral,
    };
}
