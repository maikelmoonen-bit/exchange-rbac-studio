using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Mailboxes;
using ExchangeRbacStudio.Application.Scoping;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Identity;
using ExchangeRbacStudio.Domain.Permissions;
using ExchangeRbacStudio.Domain.Scoping;
using ExchangeRbacStudio.Domain.Security;
using ExchangeRbacStudio.Domain.Validation;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.Application.Configuration;

/// <summary>
/// Read-only preflight validation of a configuration request. Never changes anything. Checks that cannot be completed
/// are reported as "Unable to verify" and, when security-critical, block execution rather than silently passing.
/// </summary>
public sealed class PreflightService(
    ConnectionManager connections,
    IEntraDirectory entra,
    IExchangeRbacGateway exchange,
    ConfigurationStateReader stateReader,
    MailboxDirectoryService mailboxes,
    ScopeDesigner scopeDesigner,
    ISettingsStore settings,
    TimeProvider time,
    ILogger<PreflightService> logger)
{
    public static class CheckIds
    {
        public const string Session = "session.tenant";
        public const string ExchangeConnection = "exchange.connection";
        public const string ExchangePrivileges = "exchange.privileges";
        public const string ServicePrincipal = "entra.servicePrincipal";
        public const string Identifiers = "entra.identifiers";
        public const string ExchangeState = "exchange.state";
        public const string Pointer = "exchange.pointer";
        public const string Roles = "exchange.roles";
        public const string Mailboxes = "exchange.mailboxes";
        public const string ExistingAssignments = "exchange.assignments";
        public const string Overlap = "exchange.overlap";
        public const string EntraGrants = "entra.grants";
        public const string AccessPolicies = "exchange.accessPolicies";
        public const string ScopeName = "scope.name";
        public const string ScopePrecision = "scope.precision";
        public const string Plan = "plan.conflicts";
    }

    public async Task<PreflightOutcome> RunAsync(ConfigurationRequest request, IProgress<PreflightCheck>? progress, CancellationToken cancellationToken)
    {
        var checks = new List<PreflightCheck>();
        void Add(PreflightCheck check)
        {
            checks.Add(check);
            progress?.Report(check);
        }

        var session = connections.Current;
        EntraServicePrincipal? entraSp = null;
        CurrentConfiguration? state = null;
        EntraGrantAnalysis? grantAnalysis = null;
        RbacOverlapAnalysis? overlap = null;
        ScopeVerification? scopeVerification = null;
        IReadOnlyList<ExchangeRecipient> revalidated = [];
        IReadOnlyList<ApplicationAccessPolicy> matchingPolicies = [];

        // 1. Session and tenant binding.
        if (!session.IsGraphConnected || !session.IsExchangeConnected)
        {
            Add(new(CheckIds.Session, "Session and tenant", CheckStatus.Blocked,
                "Both Microsoft Entra ID and Exchange Online must be connected.", Remediation: "Connect from the session bar.", IsSecurityCritical: true));
            return Finish(checks, session, entraSp, state, revalidated, grantAnalysis, overlap, scopeVerification, matchingPolicies, request);
        }

        if (!session.TenantConsistent)
        {
            Add(new(CheckIds.Session, "Session and tenant", CheckStatus.Blocked,
                "Microsoft Graph and Exchange Online are connected to different tenants.", IsSecurityCritical: true));
            return Finish(checks, session, entraSp, state, revalidated, grantAnalysis, overlap, scopeVerification, matchingPolicies, request);
        }

        Add(new(CheckIds.Session, "Session and tenant", CheckStatus.Passed,
            $"Microsoft Graph and Exchange Online are both bound to {session.TenantDisplayName} ({session.TenantId})."));

        // 2. Live Exchange connection.
        try
        {
            var info = await exchange.GetConnectionInfoAsync(cancellationToken).ConfigureAwait(false);
            if (info is null || !info.IsConnected)
                Add(new(CheckIds.ExchangeConnection, "Exchange Online connection", CheckStatus.Blocked, "The Exchange Online session is not active.", Remediation: "Reconnect Exchange Online.", IsSecurityCritical: true));
            else if (info.TenantId != session.TenantId)
                Add(new(CheckIds.ExchangeConnection, "Exchange Online connection", CheckStatus.Blocked, $"The Exchange session is bound to tenant {info.TenantId}, not {session.TenantId}.", IsSecurityCritical: true));
            else
                Add(new(CheckIds.ExchangeConnection, "Exchange Online connection", CheckStatus.Passed,
                    $"Connected as {info.UserPrincipalName}" + (info.TokenExpiryUtc is { } exp ? $"; token valid until {exp.LocalDateTime:g}." : ".")));
        }
        catch (StudioOperationException ex)
        {
            Add(new(CheckIds.ExchangeConnection, "Exchange Online connection", CheckStatus.Blocked, ex.Message, ex.TechnicalDetail, "Reconnect Exchange Online.", IsSecurityCritical: true));
        }

        // 3. Administrative privileges (cmdlets are only exposed to the session when the admin's RBAC allows them).
        try
        {
            var availability = await exchange.GetCommandAvailabilityAsync(ExchangeCommandSets.ConfigurationCommands, cancellationToken).ConfigureAwait(false);
            var missing = ExchangeCommandSets.ConfigurationCommands.Where(c => !availability.TryGetValue(c, out var ok) || !ok).ToList();
            Add(missing.Count == 0
                ? new(CheckIds.ExchangePrivileges, "Administrative privileges", CheckStatus.Passed, "All cmdlets required to configure application RBAC are available to your session.")
                : new(CheckIds.ExchangePrivileges, "Administrative privileges", CheckStatus.Blocked,
                    $"Your Exchange session cannot run: {string.Join(", ", missing)}.",
                    "Exchange Online only exposes cmdlets permitted by your role assignments.",
                    "Membership of the Organization Management role group (or the Exchange Administrator Entra role) is required. If the role is eligible in Privileged Identity Management, activate it and choose 'Reconnect & revalidate'.",
                    IsSecurityCritical: true));
        }
        catch (StudioOperationException ex)
        {
            Add(new(CheckIds.ExchangePrivileges, "Administrative privileges", CheckStatus.UnableToVerify, ex.Message, ex.TechnicalDetail, IsSecurityCritical: true));
        }

        // 4/5. Entra service principal and identifiers.
        try
        {
            entraSp = await entra.GetServicePrincipalByObjectIdAsync(request.ServicePrincipal.ObjectId, cancellationToken).ConfigureAwait(false);
            if (entraSp is null)
            {
                var app = await entra.GetApplicationByObjectIdAsync(request.ServicePrincipal.ObjectId, cancellationToken).ConfigureAwait(false);
                Add(new(CheckIds.ServicePrincipal, "Microsoft Entra service principal", CheckStatus.Blocked,
                    app is null
                        ? $"No service principal with object ID {request.ServicePrincipal.ObjectId} exists in Microsoft Entra ID."
                        : $"{request.ServicePrincipal.ObjectId} is the App Registration object ID of '{app.DisplayName}', not a service principal object ID.",
                    Remediation: "Select the application again from the Enterprise Applications list.", IsSecurityCritical: true));
            }
            else
            {
                var status = entraSp.AccountEnabled == false ? CheckStatus.Warning : CheckStatus.Passed;
                Add(new(CheckIds.ServicePrincipal, "Microsoft Entra service principal", status,
                    entraSp.AccountEnabled == false
                        ? $"'{entraSp.DisplayName}' exists but is disabled; it cannot authenticate until enabled."
                        : $"'{entraSp.DisplayName}' exists and is enabled.",
                    $"Type: {entraSp.ServicePrincipalType ?? "unknown"}; owner tenant: {entraSp.AppOwnerOrganizationId?.ToString() ?? "unknown"}."));

                Add(entraSp.AppId == request.ServicePrincipal.AppId
                    ? new(CheckIds.Identifiers, "Identifiers", CheckStatus.Passed,
                        $"AppId {entraSp.AppId} and service principal object ID {entraSp.ObjectId} belong to the same Enterprise Application.")
                    : new(CheckIds.Identifiers, "Identifiers", CheckStatus.Blocked,
                        $"The service principal's AppId is {entraSp.AppId}, but {request.ServicePrincipal.AppId} was selected.", IsSecurityCritical: true));
            }
        }
        catch (StudioOperationException ex)
        {
            Add(new(CheckIds.ServicePrincipal, "Microsoft Entra service principal", CheckStatus.UnableToVerify, ex.Message, ex.TechnicalDetail, IsSecurityCritical: true));
        }

        // 6. Current Exchange configuration.
        try
        {
            state = await stateReader.ReadAsync(request, cancellationToken).ConfigureAwait(false);
            Add(new(CheckIds.ExchangeState, "Existing Exchange configuration", CheckStatus.Passed,
                $"Read {state.AllPointers.Count} service principal pointer(s), {state.Scopes.Count} management scope(s) and {state.AllApplicationAssignments.Count} application role assignment(s)."));
        }
        catch (StudioOperationException ex)
        {
            Add(new(CheckIds.ExchangeState, "Existing Exchange configuration", CheckStatus.UnableToVerify,
                "The existing configuration could not be read, so duplicates and conflicts cannot be ruled out: " + ex.Message, ex.TechnicalDetail, IsSecurityCritical: true));
        }

        if (state is not null)
        {
            // 7. Pointer.
            Add(state.PointerMismatch
                ? new(CheckIds.Pointer, "Exchange service principal pointer", CheckStatus.Blocked,
                    $"A pointer exists with mismatching identifiers (AppId {state.Pointer!.AppId}, ObjectId {state.Pointer.ObjectId}).",
                    Remediation: "Remove the stale pointer after reviewing its assignments, then validate again.", IsSecurityCritical: true)
                : state.Pointer is not null
                    ? new(CheckIds.Pointer, "Exchange service principal pointer", CheckStatus.Passed, $"Pointer '{state.Pointer.DisplayName}' exists and will be reused.")
                    : new(CheckIds.Pointer, "Exchange service principal pointer", CheckStatus.Info, "No pointer exists yet; New-ServicePrincipal will create it."));

            // 8. Roles available in the tenant.
            var unavailable = request.Roles.Where(r => !state.TenantRoles.Any(t => t.Name.Equals(r, StringComparison.OrdinalIgnoreCase))).ToList();
            Add(request.Roles.Count == 0
                ? new(CheckIds.Roles, "Application roles", CheckStatus.Blocked, "No application roles are selected.")
                : unavailable.Count > 0
                    ? new(CheckIds.Roles, "Application roles", CheckStatus.Blocked, $"Not available in this tenant: {string.Join(", ", unavailable)}.")
                    : new(CheckIds.Roles, "Application roles", CheckStatus.Passed, $"{request.Roles.Count} role(s) are available in the tenant."));

            // 10/11. Existing assignments, duplicates and overlaps.
            Add(new(CheckIds.ExistingAssignments, "Existing role assignments", CheckStatus.Passed,
                state.ServicePrincipalAssignments.Count == 0
                    ? "The application has no Exchange application role assignments yet."
                    : $"The application has {state.ServicePrincipalAssignments.Count} existing assignment(s): " + string.Join("; ", state.ServicePrincipalAssignments.Select(a => $"{a.Role} → {a.ScopeReference ?? a.ScopeKind.ToString()}"))));

            overlap = RbacOverlapAnalyzer.Analyze(state.ServicePrincipalAssignments, request.Roles, state.FindScope(request.ScopeName), request.ScopeName);
            if (overlap.OrganizationWide.Count > 0)
            {
                Add(new(CheckIds.Overlap, "Duplicate and overlapping assignments", CheckStatus.Blocked,
                    $"{overlap.OrganizationWide.Count} organization-wide assignment(s) overlap with the requested roles, so the new scope would not restrict access.",
                    string.Join(Environment.NewLine, overlap.Findings.Where(f => f.UnderminesScoping).Select(f => f.Detail)),
                    "Remove or rescope the organization-wide assignments. You may proceed only after acknowledging that the result is not securely scoped.",
                    IsSecurityCritical: true, CanAcknowledge: true));
            }
            else if (overlap.OtherScopes.Count > 0)
            {
                Add(new(CheckIds.Overlap, "Duplicate and overlapping assignments", CheckStatus.Warning,
                    $"The application already holds overlapping roles on {overlap.OtherScopes.Select(a => a.ScopeReference).Distinct().Count()} other scope(s); its effective access will be the union.",
                    string.Join(Environment.NewLine, overlap.Findings.Select(f => f.Detail))));
            }
            else
            {
                Add(new(CheckIds.Overlap, "Duplicate and overlapping assignments", CheckStatus.Passed,
                    overlap.ExistingOnIntendedScope.Count > 0
                        ? $"{overlap.ExistingOnIntendedScope.Count} requested assignment(s) already exist on the target scope and will be reused."
                        : "No duplicate or overlapping assignments."));
            }

            // Application access policies (legacy; constrain only Entra grants).
            try
            {
                var policies = await exchange.GetApplicationAccessPoliciesAsync(cancellationToken).ConfigureAwait(false);
                matchingPolicies = policies.Where(p => Guid.TryParse(p.AppId, out var id) && id == request.ServicePrincipal.AppId).ToList();
                Add(matchingPolicies.Count == 0
                    ? new(CheckIds.AccessPolicies, "Application access policies", CheckStatus.Passed, "No legacy Application Access Policy targets this application.")
                    : new(CheckIds.AccessPolicies, "Application access policies", CheckStatus.Warning,
                        $"{matchingPolicies.Count} Application Access Policy(ies) target this application. They constrain only Microsoft Entra ID permissions and have no effect on Exchange RBAC assignments.",
                        string.Join(Environment.NewLine, matchingPolicies.Select(p => $"{p.Identity}: {p.AccessRight} {p.ScopeName}")),
                        "After migrating to RBAC for Applications, remove the consent in Entra ID and then the Application Access Policy, as documented by Microsoft."));
            }
            catch (StudioOperationException ex) when (!ex.IsSessionProblem)
            {
                Add(new(CheckIds.AccessPolicies, "Application access policies", CheckStatus.Info, "Application Access Policies could not be read: " + ex.Message));
            }
        }

        // 9. Mailbox identities.
        var maxMailboxes = settings.Current.MaxMailboxesPerScope;
        if (request.Mailboxes.Count == 0)
        {
            Add(new(CheckIds.Mailboxes, "Mailbox identities", CheckStatus.Blocked, "No mailboxes are selected."));
        }
        else if (request.Mailboxes.Count > maxMailboxes)
        {
            Add(new(CheckIds.Mailboxes, "Mailbox identities", CheckStatus.Blocked,
                $"{request.Mailboxes.Count} mailboxes are selected; the configured maximum per scope is {maxMailboxes}.",
                Remediation: "Reduce the selection, or raise the limit in Settings if your tenant's filter length allows it."));
        }
        else
        {
            try
            {
                var results = await mailboxes.RevalidateAsync(request.Mailboxes, cancellationToken).ConfigureAwait(false);
                var problems = results.Where(r => r.Problem is not null).ToList();
                revalidated = results.Where(r => r.Problem is null && r.Current is not null).Select(r => r.Current!.Recipient).ToList();
                Add(problems.Count == 0
                    ? new(CheckIds.Mailboxes, "Mailbox identities", CheckStatus.Passed, $"All {results.Count} mailbox(es) exist and are supported user or shared mailboxes.")
                    : new(CheckIds.Mailboxes, "Mailbox identities", CheckStatus.Blocked,
                        $"{problems.Count} mailbox(es) failed validation.",
                        string.Join(Environment.NewLine, problems.Select(p => $"{p.Selected.Label}: {p.Problem}")), IsSecurityCritical: true));
            }
            catch (StudioOperationException ex)
            {
                Add(new(CheckIds.Mailboxes, "Mailbox identities", CheckStatus.UnableToVerify, ex.Message, ex.TechnicalDetail, IsSecurityCritical: true));
            }
        }

        // 12. Tenant-wide Entra ID application permissions (additive with Exchange RBAC).
        try
        {
            var grants = await entra.GetAppPermissionGrantsAsync(request.ServicePrincipal.ObjectId, cancellationToken).ConfigureAwait(false);
            grantAnalysis = EntraGrantAnalyzer.Analyze(grants, ApplicationRoleCatalog.CapabilitiesOf(request.Roles));
            var critical = grantAnalysis.Findings.Where(f => f.UnderminesScoping).ToList();
            if (critical.Count > 0)
            {
                Add(new(CheckIds.EntraGrants, "Organization-wide Entra ID permissions", CheckStatus.Blocked,
                    $"{critical.Count} admin-consented Entra ID permission(s) give this application access beyond the selected mailboxes. The configuration will NOT be securely scoped.",
                    string.Join(Environment.NewLine + Environment.NewLine, critical.Select(f => f.Title + ": " + f.Detail)),
                    string.Join(Environment.NewLine, critical.Select(f => f.Remediation).Where(r => r is not null).Distinct()),
                    IsSecurityCritical: true, CanAcknowledge: true));
            }
            else if (grantAnalysis.Findings.Any(f => f.Severity >= FindingSeverity.Warning))
            {
                Add(new(CheckIds.EntraGrants, "Organization-wide Entra ID permissions", CheckStatus.Warning,
                    "The application holds other tenant-wide Entra ID permissions unrelated to the requested roles.",
                    string.Join(Environment.NewLine, grantAnalysis.Findings.Where(f => f.Severity >= FindingSeverity.Warning).Select(f => f.Title + ": " + f.Detail))));
            }
            else
            {
                Add(new(CheckIds.EntraGrants, "Organization-wide Entra ID permissions", CheckStatus.Passed,
                    grants.Count == 0
                        ? "The application has no admin-consented application permissions in Entra ID."
                        : $"None of the application's {grants.Count} granted application permission(s) provide tenant-wide mailbox access."));
            }
        }
        catch (StudioOperationException ex)
        {
            Add(new(CheckIds.EntraGrants, "Organization-wide Entra ID permissions", CheckStatus.UnableToVerify,
                "The application's granted Entra ID permissions could not be read. Tenant-wide grants would make the mailbox scope ineffective, so this must be verified: " + ex.Message,
                ex.TechnicalDetail,
                "Reading appRoleAssignments requires a role such as Directory Readers, Global Reader or Cloud Application Administrator. Activate it (PIM) and validate again.",
                IsSecurityCritical: true));
        }

        // 13/14. Scope name and precision.
        if (state is not null)
        {
            var updatable = request.Mode == ConfigurationMode.Modify ? request.ExistingScopeName ?? request.ScopeName : null;
            var nameCheck = ScopeDesigner.CheckName(request.ScopeName, request.ScopeFilter, state.Scopes, updatable);
            Add(new(CheckIds.ScopeName, "Management scope name", nameCheck.IsUsable ? (nameCheck.ExistingIsEquivalent ? CheckStatus.Info : CheckStatus.Passed) : CheckStatus.Blocked,
                $"'{request.ScopeName}': {nameCheck.Message}"));
        }

        var intended = revalidated.Count == request.Mailboxes.Count ? revalidated : request.Mailboxes;
        scopeVerification = await scopeDesigner.VerifyAsync(request.ScopeFilter, intended, cancellationToken).ConfigureAwait(false);
        Add(scopeVerification.Status switch
        {
            ScopeVerificationStatus.Verified => new(CheckIds.ScopePrecision, "Scope precision", CheckStatus.Passed, scopeVerification.Message,
                $"Filter: {request.ScopeFilter}", IsSecurityCritical: true),
            ScopeVerificationStatus.Unverifiable => new(CheckIds.ScopePrecision, "Scope precision", CheckStatus.UnableToVerify, scopeVerification.Message,
                scopeVerification.TechnicalDetail, "Retry validation. A scope whose membership cannot be verified is never accepted.", IsSecurityCritical: true),
            _ => new(CheckIds.ScopePrecision, "Scope precision", CheckStatus.Blocked, scopeVerification.Message,
                DescribeComparison(scopeVerification), "Review the selected mailboxes; unexpected matches always block execution.", IsSecurityCritical: true),
        });

        // Plan conflicts (requires a readable state).
        if (state is not null && session.TenantId is { } tenantId)
        {
            var plan = ConfigurationPlanner.Plan(request, state, tenantId, session.TenantDisplayName, settings.Current.AssignmentNameTemplate, time.GetUtcNow());
            Add(plan.HasConflicts
                ? new(CheckIds.Plan, "Configuration conflicts", CheckStatus.Blocked, $"{plan.Conflicts.Count} conflict(s) require a decision.",
                    string.Join(Environment.NewLine, plan.Conflicts.Select(c => $"{c.Title}: {c.Detail}")),
                    string.Join(Environment.NewLine, plan.Conflicts.Select(c => c.Resolution).Distinct()))
                : new(CheckIds.Plan, "Configuration conflicts", plan.HasChanges ? CheckStatus.Passed : CheckStatus.Info,
                    plan.HasChanges ? $"{plan.WriteOperations.Count} change(s) planned; {plan.Operations.Count - plan.WriteOperations.Count} existing object(s) reused."
                                    : "The desired configuration already exists. No changes are required."));
        }

        logger.LogInformation("Preflight for {App}: {Passed} passed, {Warnings} warnings, {Blocked} blocked, {Unverified} unverifiable",
            request.ServicePrincipal.DisplayName, checks.Count(c => c.Status == CheckStatus.Passed), checks.Count(c => c.Status == CheckStatus.Warning),
            checks.Count(c => c.Status == CheckStatus.Blocked), checks.Count(c => c.Status == CheckStatus.UnableToVerify));

        return Finish(checks, session, entraSp, state, revalidated, grantAnalysis, overlap, scopeVerification, matchingPolicies, request);
    }

    private PreflightOutcome Finish(List<PreflightCheck> checks, SessionSnapshot session, EntraServicePrincipal? entraSp, CurrentConfiguration? state,
        IReadOnlyList<ExchangeRecipient> revalidated, EntraGrantAnalysis? grants, RbacOverlapAnalysis? overlap, ScopeVerification? scope,
        IReadOnlyList<ApplicationAccessPolicy> policies, ConfigurationRequest request) =>
        new(new PreflightReport(checks, time.GetUtcNow()),
            new PreflightContext(session, entraSp, state, revalidated, grants, overlap, scope, policies),
            request);

    private static string? DescribeComparison(ScopeVerification verification)
    {
        if (verification.Comparison is not { } comparison) return verification.TechnicalDetail;
        var lines = new List<string>();
        lines.AddRange(comparison.Unexpected.Select(r => $"Unexpected match: {r.Label} ({r.RecipientTypeDetails})"));
        lines.AddRange(comparison.Missing.Select(r => $"Not matched: {r.Label}"));
        return string.Join(Environment.NewLine, lines);
    }
}
