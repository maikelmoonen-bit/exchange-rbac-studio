using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Diagnostics;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Auditing;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.Application.Auditing;

/// <summary>Creates audit entries enriched with session, machine and version context.</summary>
public sealed class AuditRecorder(IAuditLog log, StudioRuntimeInfo runtime, TimeProvider time, ILogger<AuditRecorder> logger)
{
    /// <summary>Appends an entry and lets failures propagate (used where the audit trail is a precondition).</summary>
    public async Task RecordRequiredAsync(SessionSnapshot? session, string category, string action, AuditOutcome outcome, string message,
        string? target = null, Guid? runId = null, string? command = null, string? details = null, CancellationToken cancellationToken = default)
    {
        await log.AppendAsync(Create(session, category, action, outcome, message, target, runId, command, details), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Appends an entry; failures are logged but never interrupt the calling workflow.</summary>
    public async Task<bool> RecordAsync(SessionSnapshot? session, string category, string action, AuditOutcome outcome, string message,
        string? target = null, Guid? runId = null, string? command = null, string? details = null)
    {
        try
        {
            await log.AppendAsync(Create(session, category, action, outcome, message, target, runId, command, details)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write audit entry {Category}/{Action}", category, action);
            return false;
        }
    }

    private AuditEntry Create(SessionSnapshot? session, string category, string action, AuditOutcome outcome, string message,
        string? target, Guid? runId, string? command, string? details) => new()
    {
        EntryId = Guid.NewGuid(),
        TimestampUtc = time.GetUtcNow(),
        RunId = runId,
        TenantId = session?.TenantId,
        TenantName = session?.TenantId is null ? null : session.TenantDisplayName,
        Administrator = session?.AdministratorUpn,
        Category = category,
        Action = action,
        Target = target,
        Outcome = outcome,
        Message = SensitiveDataRedactor.Redact(message),
        Command = command is null ? null : SensitiveDataRedactor.Redact(command),
        Details = details is null ? null : SensitiveDataRedactor.Redact(details),
        MachineName = runtime.MachineName,
        AppVersion = runtime.Version,
    };
}
