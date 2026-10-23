using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Diagnostics;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Domain.Clouds;
using ExchangeRbacStudio.Domain.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Authentication.Azure;

namespace ExchangeRbacStudio.Infrastructure.Graph;

/// <summary>
/// Microsoft Entra ID access through the Microsoft Graph SDK with delegated permissions only
/// (User.Read, Application.Read.All). Tokens are held in memory for the session and never persisted or logged.
/// The SDK's retry handler honours Retry-After for throttled (429/503) responses.
/// </summary>
public sealed class GraphEntraDirectory(ILogger<GraphEntraDirectory> logger) : IEntraDirectory
{
    /// <summary>The delegated permissions requested. Nothing else is needed for the supported operations.</summary>
    public static readonly IReadOnlyList<string> DelegatedPermissions = ["User.Read", "Application.Read.All"];

    private static readonly string[] ServicePrincipalSelect =
        ["id", "appId", "displayName", "servicePrincipalType", "accountEnabled", "appOwnerOrganizationId", "tags", "verifiedPublisher"];

    private readonly ConcurrentDictionary<Guid, ServicePrincipal> _resourceCache = new();
    private GraphServiceClient? _client;

    public bool IsSignedIn => _client is not null;

    public async Task<EntraSignInResult> SignInAsync(EntraSignInOptions options, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(options.ClientId, out _))
            throw new StudioOperationException(FailureKind.InvalidInput, "The sign-in client ID is not a valid GUID.");

        SignOut();
        var environment = options.Environment;
        var scopes = DelegatedPermissions.Select(environment.GraphScope).ToArray();
        var credential = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            ClientId = options.ClientId,
            TenantId = string.IsNullOrWhiteSpace(options.Tenant) ? "organizations" : options.Tenant.Trim(),
            AuthorityHost = new Uri(environment.AuthorityHost),
            RedirectUri = new Uri("http://localhost"),
            LoginHint = options.LoginHint,
            // Never pop up a browser unexpectedly in the middle of an operation; an expired session surfaces as an error instead.
            DisableAutomaticAuthentication = true,
            BrowserCustomization = new BrowserCustomizationOptions
            {
                SuccessMessage = "<html><body style=\"font-family:Segoe UI,sans-serif;padding:40px\"><h2>Exchange RBAC Studio</h2><p>Sign-in completed. You can close this window and return to the application.</p></body></html>",
            },
        });

        GraphServiceClient client;
        AuthenticationRecord record;
        try
        {
            record = await credential.AuthenticateAsync(SignInTokenRequest(scopes), cancellationToken).ConfigureAwait(false);
            client = CreateClient(credential, scopes, environment);
        }
        catch (Exception ex)
        {
            throw GraphErrorTranslator.Translate(ex, "sign-in");
        }

        try
        {
            var me = await client.Me.GetAsync(r => r.QueryParameters.Select = ["id", "displayName", "userPrincipalName"], cancellationToken).ConfigureAwait(false);
            var organizations = await client.Organization.GetAsync(r => r.QueryParameters.Select = ["id", "displayName", "verifiedDomains"], cancellationToken).ConfigureAwait(false);
            var organization = organizations?.Value?.FirstOrDefault()
                ?? throw new StudioOperationException(FailureKind.AuthenticationFailed, "The signed-in account's organization could not be read.");

            if (!Guid.TryParse(organization.Id, out var tenantId))
                throw new StudioOperationException(FailureKind.AuthenticationFailed, "Microsoft Graph returned an invalid tenant ID.");
            if (Guid.TryParse(record.TenantId, out var tokenTenant) && tokenTenant != tenantId)
                throw new StudioOperationException(FailureKind.TenantMismatch, $"The access token was issued by tenant {tokenTenant}, but Graph reports organization {tenantId}.");

            var defaultDomain = organization.VerifiedDomains?.FirstOrDefault(d => d.IsDefault == true)?.Name
                ?? organization.VerifiedDomains?.FirstOrDefault(d => d.IsInitial == true)?.Name;

            _client = client;
            logger.LogInformation("Signed in to Microsoft Graph ({Environment}) for tenant {TenantId}", environment.Environment, tenantId);
            return new EntraSignInResult(
                new TenantIdentity(tenantId, organization.DisplayName ?? defaultDomain ?? tenantId.ToString(), defaultDomain, environment.Environment),
                new AdministratorIdentity(me?.UserPrincipalName ?? record.Username, me?.DisplayName, Guid.TryParse(me?.Id, out var userId) ? userId : null));
        }
        catch (AuthenticationRequiredException ex)
        {
            // Not an expired session: the browser sign-in just succeeded, but no token was issued without another prompt.
            throw new GraphOperationException(FailureKind.AuthenticationFailed,
                "Sign-in completed in the browser, but Microsoft Graph did not issue a token for the application without another sign-in.",
                null, null, SensitiveDataRedactor.Redact(ex.Message),
                "Select Connect again. If this repeats, check the Microsoft Entra sign-in logs for this sign-in client (Conditional Access or session policies).", ex);
        }
        catch (Exception ex) when (ex is not StudioOperationException)
        {
            throw GraphErrorTranslator.Translate(ex, "reading the signed-in account");
        }
    }

    /// <summary>
    /// Continuous Access Evaluation is used for every Graph token. Azure.Identity caches CAE and non-CAE tokens separately,
    /// so the interactive sign-in and the client's silent requests must use the same mode; otherwise the client finds no
    /// token and, with automatic authentication disabled, fails with AuthenticationRequiredException.
    /// </summary>
    private const bool ContinuousAccessEvaluation = true;

    internal static TokenRequestContext SignInTokenRequest(string[] scopes) => new(scopes, isCaeEnabled: ContinuousAccessEvaluation);

    /// <summary>Tokens are only attached to requests for this cloud's Graph host.</summary>
    internal static GraphServiceClient CreateClient(TokenCredential credential, string[] scopes, CloudEnvironmentProfile environment) =>
        new(new AzureIdentityAuthenticationProvider(credential, [new Uri(environment.GraphEndpoint).Host], observabilityOptions: null,
                isCaeEnabled: ContinuousAccessEvaluation, scopes),
            environment.GraphBaseUrl);

    public void SignOut()
    {
        _client?.Dispose();
        _client = null;
        _resourceCache.Clear();
    }

    public async Task<IReadOnlyList<EntraServicePrincipal>> SearchServicePrincipalsAsync(string text, int maxResults, CancellationToken cancellationToken)
    {
        var client = RequireClient();
        var term = text.Replace("\"", string.Empty, StringComparison.Ordinal).Replace("\\", string.Empty, StringComparison.Ordinal).Trim();
        try
        {
            var response = await client.ServicePrincipals.GetAsync(r =>
            {
                r.QueryParameters.Search = $"\"displayName:{term}\"";
                r.QueryParameters.Select = ServicePrincipalSelect;
                r.QueryParameters.Top = Math.Clamp(maxResults, 1, 100);
                r.QueryParameters.Count = true;
                r.Headers.Add("ConsistencyLevel", "eventual");
            }, cancellationToken).ConfigureAwait(false);

            return (response?.Value ?? []).Select(Map).Where(sp => sp is not null).Cast<EntraServicePrincipal>()
                .OrderBy(sp => sp.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            throw GraphErrorTranslator.Translate(ex, "searching service principals");
        }
    }

    public async Task<EntraServicePrincipal?> GetServicePrincipalByObjectIdAsync(Guid objectId, CancellationToken cancellationToken)
    {
        var client = RequireClient();
        try
        {
            var sp = await client.ServicePrincipals[objectId.ToString("D")].GetAsync(r => r.QueryParameters.Select = ServicePrincipalSelect, cancellationToken).ConfigureAwait(false);
            return sp is null ? null : Map(sp);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
        catch (Exception ex)
        {
            throw GraphErrorTranslator.Translate(ex, "reading the service principal");
        }
    }

    public async Task<EntraServicePrincipal?> GetServicePrincipalByAppIdAsync(Guid appId, CancellationToken cancellationToken)
    {
        var client = RequireClient();
        try
        {
            var sp = await client.ServicePrincipalsWithAppId(appId.ToString("D")).GetAsync(r => r.QueryParameters.Select = ServicePrincipalSelect, cancellationToken).ConfigureAwait(false);
            return sp is null ? null : Map(sp);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
        catch (Exception ex)
        {
            throw GraphErrorTranslator.Translate(ex, "reading the service principal");
        }
    }

    public async Task<EntraApplicationObject?> GetApplicationByObjectIdAsync(Guid objectId, CancellationToken cancellationToken)
    {
        var client = RequireClient();
        try
        {
            var app = await client.Applications[objectId.ToString("D")].GetAsync(r => r.QueryParameters.Select = ["id", "appId", "displayName"], cancellationToken).ConfigureAwait(false);
            return app is not null && Guid.TryParse(app.Id, out var id) && Guid.TryParse(app.AppId, out var appId)
                ? new EntraApplicationObject(id, appId, app.DisplayName ?? appId.ToString())
                : null;
        }
        catch (ODataError ex) when (ex.ResponseStatusCode is 404 or 400)
        {
            return null;
        }
        catch (Exception ex)
        {
            throw GraphErrorTranslator.Translate(ex, "reading the app registration");
        }
    }

    public async Task<IReadOnlyList<EntraAppPermissionGrant>> GetAppPermissionGrantsAsync(Guid servicePrincipalObjectId, CancellationToken cancellationToken)
    {
        var client = RequireClient();
        var builder = client.ServicePrincipals[servicePrincipalObjectId.ToString("D")].AppRoleAssignments;
        var assignments = new List<AppRoleAssignment>();
        try
        {
            var page = await builder.GetAsync(r => r.QueryParameters.Top = 999, cancellationToken).ConfigureAwait(false);
            while (page is not null)
            {
                if (page.Value is not null) assignments.AddRange(page.Value);
                if (string.IsNullOrEmpty(page.OdataNextLink)) break;
                page = await builder.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw GraphErrorTranslator.Translate(ex, "reading granted application permissions (appRoleAssignments)");
        }

        var grants = new List<EntraAppPermissionGrant>(assignments.Count);
        foreach (var assignment in assignments)
        {
            if (assignment.ResourceId is not { } resourceId || assignment.AppRoleId is not { } appRoleId) continue;

            var resource = await GetResourceAsync(client, resourceId, cancellationToken).ConfigureAwait(false);
            var role = resource?.AppRoles?.FirstOrDefault(r => r.Id == appRoleId);
            grants.Add(new EntraAppPermissionGrant(
                Guid.TryParse(assignment.Id, out var assignmentId) ? assignmentId : Guid.Empty,
                resourceId,
                Guid.TryParse(resource?.AppId, out var resourceAppId) ? resourceAppId : null,
                assignment.ResourceDisplayName ?? resource?.DisplayName ?? resourceId.ToString(),
                appRoleId,
                role?.Value,
                role?.DisplayName,
                assignment.CreatedDateTime));
        }

        return grants;
    }

    private async Task<ServicePrincipal?> GetResourceAsync(GraphServiceClient client, Guid resourceId, CancellationToken cancellationToken)
    {
        if (_resourceCache.TryGetValue(resourceId, out var cached)) return cached;
        try
        {
            var resource = await client.ServicePrincipals[resourceId.ToString("D")]
                .GetAsync(r => r.QueryParameters.Select = ["id", "appId", "displayName", "appRoles"], cancellationToken).ConfigureAwait(false);
            if (resource is not null) _resourceCache[resourceId] = resource;
            return resource;
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
        catch (Exception ex)
        {
            throw GraphErrorTranslator.Translate(ex, "resolving the resource application's app roles");
        }
    }

    private GraphServiceClient RequireClient() =>
        _client ?? throw new StudioOperationException(FailureKind.NotConnected, "Microsoft Entra ID (Microsoft Graph) is not connected.");

    private static EntraServicePrincipal? Map(ServicePrincipal sp) =>
        Guid.TryParse(sp.Id, out var id) && Guid.TryParse(sp.AppId, out var appId)
            ? new EntraServicePrincipal(id, appId, sp.DisplayName ?? appId.ToString(), sp.ServicePrincipalType, sp.AccountEnabled,
                sp.AppOwnerOrganizationId, sp.Tags ?? [], sp.VerifiedPublisher?.DisplayName)
            : null;
}
