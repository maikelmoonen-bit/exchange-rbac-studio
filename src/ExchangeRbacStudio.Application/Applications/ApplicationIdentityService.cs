using ExchangeRbacStudio.Application.Abstractions;
using ExchangeRbacStudio.Application.Errors;
using ExchangeRbacStudio.Application.Inventory;
using ExchangeRbacStudio.Application.Sessions;
using ExchangeRbacStudio.Application.Validation;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Identity;
using ExchangeRbacStudio.Domain.Permissions;
using ExchangeRbacStudio.Domain.Security;

namespace ExchangeRbacStudio.Application.Applications;

public enum ExchangePointerStatus
{
    Present,
    Missing,
    Mismatch,
    Unknown,
}

public sealed record ApplicationSearchResult(EntraServicePrincipal ServicePrincipal, ExchangePointerStatus PointerStatus, string? MatchNote);

public sealed record ApplicationIdentityDetails(
    EntraServicePrincipal ServicePrincipal,
    TenantIdentity? Tenant,
    ExchangePointerStatus PointerStatus,
    ExchangeServicePrincipal? Pointer,
    string PointerMessage,
    IReadOnlyList<EntraAppPermissionGrant> Grants,
    string? GrantsError,
    EntraGrantAnalysis? GrantAnalysis,
    IReadOnlyList<string> Notes,
    string? BlockingError)
{
    public bool IsValid => BlockingError is null;

    public IReadOnlyList<EntraAppPermissionGrant> MailboxRelevantGrants => GrantAnalysis?.MailboxRelevantGrants ?? [];
}

/// <summary>Finds Enterprise Applications and validates their identifiers against Microsoft Graph and Exchange Online.</summary>
public sealed class ApplicationIdentityService(
    ConnectionManager connections,
    IEntraDirectory entra,
    IExchangeRbacGateway exchange,
    RbacInventoryService inventory)
{
    public const int SearchResultLimit = 25;

    public async Task<IReadOnlyList<ApplicationSearchResult>> SearchAsync(string text, CancellationToken cancellationToken)
    {
        connections.RequireGraph();
        var term = InputValidation.NormalizeSearchText(text);
        var pointers = inventory.Current?.ServicePrincipals;

        if (InputValidation.TryParseGuid(term, out var id))
        {
            var results = new List<ApplicationSearchResult>();
            var byObjectId = await entra.GetServicePrincipalByObjectIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (byObjectId is not null)
                results.Add(new(byObjectId, PointerStatusFor(byObjectId, pointers), "Matched by service principal object ID."));

            var byAppId = await entra.GetServicePrincipalByAppIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (byAppId is not null && results.All(r => r.ServicePrincipal.ObjectId != byAppId.ObjectId))
                results.Add(new(byAppId, PointerStatusFor(byAppId, pointers), "Matched by application (client) ID."));

            if (results.Count > 0) return results;

            var application = await entra.GetApplicationByObjectIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (application is not null)
            {
                var sp = await entra.GetServicePrincipalByAppIdAsync(application.AppId, cancellationToken).ConfigureAwait(false);
                var note = $"{id} is the Object ID of the App Registration '{application.DisplayName}', not of its Enterprise Application. "
                    + "Exchange RBAC requires the service principal (Enterprise Application) object ID. ";
                if (sp is null)
                    throw new StudioOperationException(FailureKind.NotFound,
                        note + "This app registration has no service principal in this tenant yet, so it cannot be configured. Create the Enterprise Application (for example by granting consent) first.");
                return [new ApplicationSearchResult(sp, PointerStatusFor(sp, pointers), note + $"The matching service principal object ID is {sp.ObjectId}.")];
            }

            return [];
        }

        var found = await entra.SearchServicePrincipalsAsync(term, SearchResultLimit, cancellationToken).ConfigureAwait(false);
        return found.Select(sp => new ApplicationSearchResult(sp, PointerStatusFor(sp, pointers), null)).ToList();
    }

    /// <summary>Re-reads the service principal from Graph and checks the Exchange pointer and granted permissions.</summary>
    public async Task<ApplicationIdentityDetails> ValidateAsync(EntraServicePrincipal selected, CancellationToken cancellationToken)
    {
        var session = connections.RequireGraph();
        var notes = new List<string>();

        var current = await entra.GetServicePrincipalByObjectIdAsync(selected.ObjectId, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return Blocked(selected, session, $"The service principal {selected.ObjectId} no longer exists in Microsoft Entra ID.");

        if (current.AppId != selected.AppId)
            return Blocked(current, session, $"Identifier conflict: service principal {current.ObjectId} has AppId {current.AppId}, not {selected.AppId}.");

        if (current.AccountEnabled == false)
            notes.Add("The service principal is disabled (accountEnabled = false). It cannot sign in until it is enabled.");
        if (current.IsMicrosoftFirstParty)
            notes.Add("This is a Microsoft first-party application. Assigning mailbox permissions to Microsoft services is rarely required; verify the documented requirement.");
        if (current.IsManagedIdentity)
            notes.Add("This is a managed identity.");
        if (session.Tenant is { } tenant && current.AppOwnerOrganizationId is { } owner && owner != tenant.TenantId && !current.IsMicrosoftFirstParty)
            notes.Add($"This is a multi-tenant application registered in another tenant ({owner}). Its credentials are managed by that tenant.");

        ExchangePointerStatus pointerStatus;
        ExchangeServicePrincipal? pointer = null;
        string pointerMessage;
        string? blocking = null;

        if (connections.Current.IsExchangeConnected)
        {
            var pointers = await exchange.GetServicePrincipalsAsync(cancellationToken).ConfigureAwait(false);
            pointer = pointers.FirstOrDefault(p => p.AppId == current.AppId || p.ObjectId == current.ObjectId);
            if (pointer is null)
            {
                pointerStatus = ExchangePointerStatus.Missing;
                pointerMessage = "No Exchange service principal pointer exists yet. It will be created with New-ServicePrincipal.";
            }
            else if (pointer.Matches(current.AppId, current.ObjectId))
            {
                pointerStatus = ExchangePointerStatus.Present;
                pointerMessage = $"Exchange service principal pointer '{pointer.DisplayName}' exists and matches the Entra identifiers. It will be reused.";
            }
            else
            {
                pointerStatus = ExchangePointerStatus.Mismatch;
                pointerMessage = $"An Exchange pointer exists with AppId {pointer.AppId} and ObjectId {pointer.ObjectId}, which do not both match the Entra service principal (AppId {current.AppId}, ObjectId {current.ObjectId}).";
                blocking = pointerMessage + " Remove the stale pointer (Remove-ServicePrincipal) after reviewing its role assignments, then retry.";
            }
        }
        else
        {
            pointerStatus = ExchangePointerStatus.Unknown;
            pointerMessage = "Exchange Online is not connected, so the pointer could not be checked.";
        }

        IReadOnlyList<EntraAppPermissionGrant> grants = [];
        string? grantsError = null;
        EntraGrantAnalysis? analysis = null;
        try
        {
            grants = await entra.GetAppPermissionGrantsAsync(current.ObjectId, cancellationToken).ConfigureAwait(false);
            analysis = EntraGrantAnalyzer.Analyze(grants, MailboxCapability.None);
        }
        catch (StudioOperationException ex) when (!ex.IsSessionProblem)
        {
            grantsError = ex.Message;
        }

        return new ApplicationIdentityDetails(current, session.Tenant, pointerStatus, pointer, pointerMessage, grants, grantsError, analysis, notes, blocking);
    }

    private static ApplicationIdentityDetails Blocked(EntraServicePrincipal sp, SessionSnapshot session, string error) =>
        new(sp, session.Tenant, ExchangePointerStatus.Unknown, null, "Not checked.", [], null, null, [], error);

    private static ExchangePointerStatus PointerStatusFor(EntraServicePrincipal sp, IReadOnlyList<ExchangeServicePrincipal>? pointers)
    {
        if (pointers is null) return ExchangePointerStatus.Unknown;
        var pointer = pointers.FirstOrDefault(p => p.AppId == sp.AppId || p.ObjectId == sp.ObjectId);
        if (pointer is null) return ExchangePointerStatus.Missing;
        return pointer.Matches(sp.AppId, sp.ObjectId) ? ExchangePointerStatus.Present : ExchangePointerStatus.Mismatch;
    }
}
