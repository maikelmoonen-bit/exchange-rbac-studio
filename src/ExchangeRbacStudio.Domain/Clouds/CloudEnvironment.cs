namespace ExchangeRbacStudio.Domain.Clouds;

/// <summary>
/// Microsoft 365 national/sovereign cloud the administrator is operating in.
/// GCC (moderate) tenants use the <see cref="Global"/> endpoints.
/// </summary>
public enum CloudEnvironment
{
    Global,
    UsGovGccHigh,
    UsGovDod,
    China,
}

/// <summary>
/// Endpoint profile for a cloud environment. Values follow the Microsoft Graph national cloud
/// deployment documentation and the ExchangeEnvironmentName values accepted by Connect-ExchangeOnline.
/// </summary>
public sealed record CloudEnvironmentProfile(
    CloudEnvironment Environment,
    string DisplayName,
    string GraphEndpoint,
    string AuthorityHost,
    string ExchangeEnvironmentName)
{
    public string GraphBaseUrl => $"{GraphEndpoint}/v1.0";

    /// <summary>Builds a fully qualified delegated Graph scope for this cloud, e.g. https://graph.microsoft.com/User.Read.</summary>
    public string GraphScope(string permission) => $"{GraphEndpoint}/{permission}";
}

public static class CloudEnvironments
{
    public static readonly CloudEnvironmentProfile Global = new(
        CloudEnvironment.Global,
        "Microsoft 365 (Commercial / GCC)",
        "https://graph.microsoft.com",
        "https://login.microsoftonline.com/",
        "O365Default");

    public static readonly CloudEnvironmentProfile UsGovGccHigh = new(
        CloudEnvironment.UsGovGccHigh,
        "Microsoft 365 GCC High",
        "https://graph.microsoft.us",
        "https://login.microsoftonline.us/",
        "O365USGovGCCHigh");

    public static readonly CloudEnvironmentProfile UsGovDod = new(
        CloudEnvironment.UsGovDod,
        "Microsoft 365 DoD",
        "https://dod-graph.microsoft.us",
        "https://login.microsoftonline.us/",
        "O365USGovDoD");

    public static readonly CloudEnvironmentProfile China = new(
        CloudEnvironment.China,
        "Microsoft 365 operated by 21Vianet",
        "https://microsoftgraph.chinacloudapi.cn",
        "https://login.chinacloudapi.cn/",
        "O365China");

    public static IReadOnlyList<CloudEnvironmentProfile> All { get; } = [Global, UsGovGccHigh, UsGovDod, China];

    public static CloudEnvironmentProfile Get(CloudEnvironment environment) => environment switch
    {
        CloudEnvironment.Global => Global,
        CloudEnvironment.UsGovGccHigh => UsGovGccHigh,
        CloudEnvironment.UsGovDod => UsGovDod,
        CloudEnvironment.China => China,
        _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "Unknown cloud environment."),
    };
}
