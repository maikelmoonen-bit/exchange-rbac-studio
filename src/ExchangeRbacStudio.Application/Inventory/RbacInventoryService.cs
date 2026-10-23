using System.Collections.Concurrent;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Identity;
using Microsoft.Extensions.Logging;

namespace ExchangeRbacStudio.Application.Inventory;

/// <summary>Loads, caches and analyzes the tenant's application RBAC configuration.</summary>
public sealed class RbacInventoryService(
    ConnectionManager connections,
    IExchangeRbacGateway exchange,
    IEntraDirectory entra,
    TimeProvider time,
    ILogger<RbacInventoryService> logger)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private RbacInventory? _current;

    public event EventHandler<RbacInventory?>? Changed;

    /// <summary>The cached inventory, but only if it belongs to the current session (tenant isolation).</summary>
    public RbacInventory? Current
    {
        get
        {
            var cached = _current;
            return cached is not null && cached.SessionId == connections.Current.SessionId && connections.Current.IsExchangeConnected ? cached : null;
        }
    }

    public async Task<RbacInventory> GetAsync(CancellationToken cancellationToken) =>
        Current ?? await RefreshAsync(connections.Current.IsGraphConnected, null, cancellationToken).ConfigureAwait(false);

    public async Task<RbacInventory> RefreshAsync(bool includeEntra, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = connections.RequireExchange();

            progress?.Report("Reading Exchange service principals…");
            var servicePrincipals = await exchange.GetServicePrincipalsAsync(cancellationToken).ConfigureAwait(false);

            progress?.Report("Reading management scopes…");
            var scopes = await exchange.GetManagementScopesAsync(cancellationToken).ConfigureAwait(false);

            progress?.Report("Discovering application roles…");
            var roles = await exchange.GetApplicationRolesAsync(cancellationToken).ConfigureAwait(false);

            progress?.Report("Reading application role assignments…");
            var assignments = await exchange.GetApplicationRoleAssignmentsAsync(roles.Select(r => r.Name).ToList(), cancellationToken).ConfigureAwait(false);

            IReadOnlyList<ApplicationAccessPolicy>? policies = null;
            string? policyError = null;
            try
            {
                policies = await exchange.GetApplicationAccessPoliciesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (StudioOperationException ex) when (!ex.IsSessionProblem)
            {
                policyError = ex.Message;
                logger.LogWarning(ex, "Could not read application access policies");
            }

            EntraEnrichment? entraData = null;
            if (includeEntra && session.IsGraphConnected && session.TenantConsistent)
            {
                progress?.Report("Analyzing Microsoft Entra ID permissions…");
                entraData = await EnrichAsync(servicePrincipals, cancellationToken).ConfigureAwait(false);
            }

            var findings = InventoryAnalyzer.Analyze(servicePrincipals, scopes, assignments, policies, entraData);
            if (connections.Current.SessionId != session.SessionId)
                throw new StudioOperationException(FailureKind.TenantMismatch, "The session changed while the configuration was loading. Refresh to load the current tenant.");

            var inventory = new RbacInventory(session.SessionId, session.TenantId, time.GetUtcNow(), servicePrincipals, scopes, roles,
                assignments, policies, policyError, findings, entraData);
            _current = inventory;
            logger.LogInformation("Inventory loaded: {ServicePrincipals} service principals, {Scopes} scopes, {Assignments} application role assignments, {Findings} findings",
                servicePrincipals.Count, scopes.Count, assignments.Count, findings.Count);
            Changed?.Invoke(this, inventory);
            return inventory;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Invalidate()
    {
        _current = null;
        Changed?.Invoke(this, null);
    }

    private async Task<EntraEnrichment> EnrichAsync(IReadOnlyList<ExchangeServicePrincipal> servicePrincipals, CancellationToken cancellationToken)
    {
        var principals = new ConcurrentDictionary<Guid, EntraServicePrincipal?>();
        var grants = new ConcurrentDictionary<Guid, IReadOnlyList<EntraAppPermissionGrant>>();
        var failed = new ConcurrentBag<Guid>();
        string? failureMessage = null;

        var ids = servicePrincipals.Where(sp => sp.ObjectId is not null).Select(sp => sp.ObjectId!.Value).Distinct().ToList();
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (id, ct) =>
        {
            try
            {
                var sp = await entra.GetServicePrincipalByObjectIdAsync(id, ct).ConfigureAwait(false);
                principals[id] = sp;
                if (sp is not null)
                    grants[id] = await entra.GetAppPermissionGrantsAsync(id, ct).ConfigureAwait(false);
            }
            catch (StudioOperationException ex)
            {
                failed.Add(id);
                failureMessage = ex.Message;
                logger.LogWarning(ex, "Entra enrichment failed for service principal {ObjectId}", id);
            }
        }).ConfigureAwait(false);

        return new EntraEnrichment(principals, grants, failed.ToList(), failureMessage);
    }
}
