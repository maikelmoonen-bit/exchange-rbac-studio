using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Domain.Scoping;

namespace ExchangeRbacStudio.Application.Configuration;

/// <summary>Reads the live Exchange state relevant to a configuration request. Used by preflight and by drift detection.</summary>
public sealed class ConfigurationStateReader(IExchangeRbacGateway exchange)
{
    public async Task<CurrentConfiguration> ReadAsync(ConfigurationRequest request, CancellationToken cancellationToken)
    {
        var sp = request.ServicePrincipal;
        var pointers = await exchange.GetServicePrincipalsAsync(cancellationToken).ConfigureAwait(false);
        var pointer = pointers.FirstOrDefault(p => p.Matches(sp.AppId, sp.ObjectId))
            ?? pointers.FirstOrDefault(p => p.AppId == sp.AppId || p.ObjectId == sp.ObjectId);
        var mismatch = pointer is not null && !pointer.Matches(sp.AppId, sp.ObjectId);

        var scopes = await exchange.GetManagementScopesAsync(cancellationToken).ConfigureAwait(false);
        var roles = await exchange.GetApplicationRolesAsync(cancellationToken).ConfigureAwait(false);
        var roleNames = roles.Select(r => r.Name).Union(request.Roles, StringComparer.OrdinalIgnoreCase).ToList();
        var assignments = await exchange.GetApplicationRoleAssignmentsAsync(roleNames, cancellationToken).ConfigureAwait(false);
        var spAssignments = pointer is null ? [] : assignments.Where(a => a.IsAssignedTo(pointer)).ToList();

        var target = scopes.FirstOrDefault(s => s.Name.Equals(request.ScopeName, StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<ScopeReference>? targetReferences = null;
        if (target is not null && (request.Mode == ConfigurationMode.Modify || !RecipientFilterAnalyzer.AreEquivalentExactFilters(target.RecipientFilter, request.ScopeFilter)))
            targetReferences = await exchange.GetScopeReferencesAsync(target.Name, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ScopeReference>? existingReferences = null;
        if (request.Mode == ConfigurationMode.Modify && !string.IsNullOrWhiteSpace(request.ExistingScopeName)
            && !request.ExistingScopeName.Equals(request.ScopeName, StringComparison.OrdinalIgnoreCase)
            && scopes.Any(s => s.Name.Equals(request.ExistingScopeName, StringComparison.OrdinalIgnoreCase)))
            existingReferences = await exchange.GetScopeReferencesAsync(request.ExistingScopeName, cancellationToken).ConfigureAwait(false);

        return new CurrentConfiguration(pointer, mismatch, pointers, scopes, roles, assignments, spAssignments, targetReferences, existingReferences);
    }
}
