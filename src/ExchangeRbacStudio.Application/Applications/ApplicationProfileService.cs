using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Authorization;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Inventory;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Authorization;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Identity;
using ExchangeRbacStudio.Domain.Permissions;
using ExchangeRbacStudio.Domain.Scoping;
using ExchangeRbacStudio.Domain.Security;

namespace ExchangeRbacStudio.Application.Applications;

public sealed record ScopeMembership(
    ManagementScope Scope,
    IReadOnlyList<ExchangeRecipient>? Members,
    bool Truncated,
    string? Error,
    FilterAnalysis Analysis)
{
    public bool IsReliable => Members is not null && !Truncated && string.IsNullOrWhiteSpace(Scope.RecipientRoot);
}

/// <summary>Everything known about one application's mailbox access, from both authorization systems.</summary>
public sealed record ApplicationProfile(
    ExchangeServicePrincipal? Pointer,
    EntraServicePrincipal? Entra,
    string? EntraError,
    ExchangePointerStatus PointerStatus,
    IReadOnlyList<EntraAppPermissionGrant> Grants,
    string? GrantsError,
    EntraGrantAnalysis? GrantAnalysis,
    IReadOnlyList<ManagementRoleAssignment> Assignments,
    IReadOnlyList<ScopeMembership> Scopes,
    IReadOnlyList<SecurityFinding> Findings,
    EffectiveAccessMatrix Matrix)
{
    public string DisplayName => Entra?.DisplayName ?? Pointer?.DisplayName ?? "Unknown application";

    public Guid? AppId => Entra?.AppId ?? Pointer?.AppId;

    public Guid? ObjectId => Entra?.ObjectId ?? Pointer?.ObjectId;

    public IReadOnlyDictionary<string, MailboxCapability> TenantWidePermissions =>
        BuildTenantWidePermissions(GrantAnalysis);

    internal static IReadOnlyDictionary<string, MailboxCapability> BuildTenantWidePermissions(EntraGrantAnalysis? analysis)
    {
        var result = new Dictionary<string, MailboxCapability>(StringComparer.OrdinalIgnoreCase);
        if (analysis is null) return result;
        foreach (var grant in analysis.MailboxRelevantGrants)
        {
            var definition = EntraPermissionCatalog.TryGet(grant.ResourceAppId, grant.PermissionValue);
            if (definition is { Reach: EntraGrantReach.TenantWideMailboxAccess })
                result[grant.PermissionValue!] = definition.Capabilities;
        }

        return result;
    }
}

/// <summary>Builds application detail views, including scope membership and the effective-access matrix.</summary>
public sealed class ApplicationProfileService(
    ConnectionManager connections,
    RbacInventoryService inventory,
    IExchangeRbacGateway exchange,
    IEntraDirectory entra)
{
    public const int MembershipPreviewLimit = 200;

    public async Task<ApplicationProfile> LoadAsync(Guid? servicePrincipalObjectId, Guid? appId, CancellationToken cancellationToken)
    {
        var session = connections.Current;
        var current = session.IsExchangeConnected ? await inventory.GetAsync(cancellationToken).ConfigureAwait(false) : null;
        var pointer = current?.ServicePrincipals.FirstOrDefault(sp => (servicePrincipalObjectId is not null && sp.ObjectId == servicePrincipalObjectId) || (appId is not null && sp.AppId == appId));

        EntraServicePrincipal? entraSp = null;
        string? entraError = null;
        IReadOnlyList<EntraAppPermissionGrant> grants = [];
        string? grantsError = null;
        var objectId = servicePrincipalObjectId ?? pointer?.ObjectId;

        if (session.IsGraphConnected)
        {
            try
            {
                entraSp = objectId is { } id
                    ? await entra.GetServicePrincipalByObjectIdAsync(id, cancellationToken).ConfigureAwait(false)
                    : appId is { } a ? await entra.GetServicePrincipalByAppIdAsync(a, cancellationToken).ConfigureAwait(false) : null;
                if (entraSp is null) entraError = "No matching service principal was found in Microsoft Entra ID.";
            }
            catch (StudioOperationException ex) when (!ex.IsSessionProblem)
            {
                entraError = ex.Message;
            }

            if (entraSp is not null)
            {
                try
                {
                    grants = await entra.GetAppPermissionGrantsAsync(entraSp.ObjectId, cancellationToken).ConfigureAwait(false);
                }
                catch (StudioOperationException ex) when (!ex.IsSessionProblem)
                {
                    grantsError = ex.Message;
                }
            }
        }
        else
        {
            entraError = "Microsoft Entra ID is not connected.";
            grantsError = "Microsoft Entra ID is not connected; tenant-wide permissions are unknown.";
        }

        var pointerStatus = current is null ? ExchangePointerStatus.Unknown
            : pointer is null ? ExchangePointerStatus.Missing
            : entraSp is null || pointer.Matches(entraSp.AppId, entraSp.ObjectId) ? ExchangePointerStatus.Present
            : ExchangePointerStatus.Mismatch;

        var assignments = pointer is null || current is null ? [] : current.AssignmentsFor(pointer);
        var assignedRoles = assignments.Where(a => a.Enabled).Select(a => a.Role).ToList();
        var analysis = grantsError is null ? EntraGrantAnalyzer.Analyze(grants, ApplicationRoleCatalog.CapabilitiesOf(assignedRoles)) : null;

        var scopes = new List<ScopeMembership>();
        if (current is not null)
        {
            foreach (var scope in assignments.Select(current.ScopeFor).Where(s => s is not null).Cast<ManagementScope>().DistinctBy(s => s.Name))
                scopes.Add(await ResolveMembershipAsync(scope, cancellationToken).ConfigureAwait(false));
        }

        var findings = BuildFindings(assignments, scopes, analysis, grantsError, pointerStatus);
        var matrix = BuildMatrix(assignments, scopes, analysis, new Dictionary<string, IReadOnlyList<AuthorizationTestRow>>());

        return new ApplicationProfile(pointer, entraSp, entraError, pointerStatus, grants, grantsError, analysis, assignments, scopes, findings, matrix);
    }

    /// <summary>Rebuilds the matrix with observed Test-ServicePrincipalAuthorization results.</summary>
    public static ApplicationProfile WithTestResults(ApplicationProfile profile, AuthorizationTestReport report)
    {
        var tested = report.Results.Where(r => r.Error is null).ToDictionary(r => r.Mailbox.Label, r => r.Rows, StringComparer.OrdinalIgnoreCase);
        return profile with { Matrix = BuildMatrix(profile.Assignments, profile.Scopes, profile.GrantAnalysis, tested) };
    }

    public async Task<ScopeMembership> ResolveMembershipAsync(ManagementScope scope, CancellationToken cancellationToken)
    {
        var analysis = RecipientFilterAnalyzer.Analyze(scope.RecipientFilter);
        if (string.IsNullOrWhiteSpace(scope.RecipientFilter))
            return new ScopeMembership(scope, null, false, "The scope has no recipient filter; membership cannot be previewed.", analysis);

        try
        {
            var preview = await exchange.PreviewRecipientFilterAsync(scope.RecipientFilter!, MembershipPreviewLimit, cancellationToken).ConfigureAwait(false);
            return new ScopeMembership(scope, preview.Recipients, preview.Truncated, null, analysis);
        }
        catch (StudioOperationException ex) when (!ex.IsSessionProblem)
        {
            return new ScopeMembership(scope, null, false, ex.Message, analysis);
        }
    }

    private static EffectiveAccessMatrix BuildMatrix(IReadOnlyList<ManagementRoleAssignment> assignments, IReadOnlyList<ScopeMembership> scopes,
        EntraGrantAnalysis? analysis, IReadOnlyDictionary<string, IReadOnlyList<AuthorizationTestRow>> tested)
    {
        var access = assignments.Select(a =>
        {
            var membership = scopes.FirstOrDefault(s => s.Scope.IsReferencedBy(a.ScopeReference));
            return new AssignmentAccess(a, membership?.Members);
        }).ToList();
        return EffectiveAccessMatrixBuilder.Build(access, tested, ApplicationProfile.BuildTenantWidePermissions(analysis));
    }

    private static IReadOnlyList<SecurityFinding> BuildFindings(IReadOnlyList<ManagementRoleAssignment> assignments, IReadOnlyList<ScopeMembership> scopes,
        EntraGrantAnalysis? analysis, string? grantsError, ExchangePointerStatus pointerStatus)
    {
        var findings = new List<SecurityFinding>();

        if (pointerStatus == ExchangePointerStatus.Mismatch)
            findings.Add(new("POINTER_MISMATCH", FindingSeverity.High, "Exchange pointer identifiers do not match Entra ID",
                "The Exchange service principal pointer's AppId or ObjectId differs from the Enterprise Application.", "Remove and recreate the pointer."));

        foreach (var assignment in assignments.Where(a => a.Enabled && a.ScopeKind == AssignmentScopeKind.Organization))
            findings.Add(new("RBAC_ORG_WIDE", FindingSeverity.Critical, $"{assignment.Role} applies to every mailbox",
                $"Role assignment '{assignment.Name}' has no resource scope.", "Rescope or remove the assignment.", UnderminesScoping: true, Subject: assignment.Name));

        foreach (var scope in scopes)
            foreach (var flag in scope.Analysis.Flags.Where(f => f.Risk is FilterRisk.High or FilterRisk.Moderate or FilterRisk.Unknown))
                findings.Add(new("SCOPE_" + flag.Code, flag.Risk == FilterRisk.High ? FindingSeverity.High : FindingSeverity.Warning,
                    $"Scope '{scope.Scope.Name}': potentially broad filter", flag.Description, Subject: scope.Scope.Name));

        if (analysis is not null)
            findings.AddRange(analysis.Findings);
        else if (grantsError is not null)
            findings.Add(new("ENTRA_UNVERIFIED", FindingSeverity.Warning, "Microsoft Entra ID permissions not verified", grantsError,
                "Connect to Microsoft Entra ID with a role that can read application role assignments."));

        return findings.OrderByDescending(f => f.Severity).ToList();
    }
}
