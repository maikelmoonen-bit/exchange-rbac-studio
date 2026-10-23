using Azure.Core;
using ExchangeRbacStudio.Domain.Clouds;
using ExchangeRbacStudio.Infrastructure.Graph;

namespace ExchangeRbacStudio.Tests.Infrastructure;

/// <summary>
/// Azure.Identity keeps separate token caches for requests with and without Continuous Access Evaluation (CAE).
/// Because automatic (interactive) authentication is disabled after sign-in, the Graph client's silent token requests
/// only find the token acquired by the interactive sign-in when both use the same CAE mode and scopes.
/// </summary>
public sealed class GraphSignInTests
{
    [Theory]
    [InlineData(CloudEnvironment.Global)]
    [InlineData(CloudEnvironment.UsGovGccHigh)]
    [InlineData(CloudEnvironment.UsGovDod)]
    [InlineData(CloudEnvironment.China)]
    public async Task Graph_requests_use_the_same_token_request_as_the_interactive_sign_in(CloudEnvironment cloud)
    {
        var environment = CloudEnvironments.Get(cloud);
        string[] scopes = [.. GraphEntraDirectory.DelegatedPermissions.Select(environment.GraphScope)];
        var credential = new RecordingCredential();
        using var client = GraphEntraDirectory.CreateClient(credential, scopes, environment);

        await Assert.ThrowsAnyAsync<Exception>(() => client.Me.GetAsync());

        var signIn = GraphEntraDirectory.SignInTokenRequest(scopes);
        var request = Assert.Single(credential.Requests);
        Assert.Equal(signIn.IsCaeEnabled, request.IsCaeEnabled);
        Assert.Equal(signIn.Scopes, request.Scopes);
    }

    /// <summary>Records the token request and fails it, so no network call is made.</summary>
    private sealed class RecordingCredential : TokenCredential
    {
        public List<TokenRequestContext> Requests { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Requests.Add(requestContext);
            throw new InvalidOperationException("Token request recorded.");
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
