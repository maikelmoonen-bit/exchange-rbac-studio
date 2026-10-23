using ExchangeRbacStudio.Domain.Clouds;
using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Identity;
using ExchangeRbacStudio.Domain.Permissions;

namespace ExchangeRbacStudio.TestSupport;

/// <summary>Realistic, deterministic test data modelled on a small Exchange Online tenant.</summary>
public static class Fixtures
{
    public static readonly Guid TenantId = Guid.Parse("8f2b6a1e-4c1d-4f3a-9d7e-2b5c6a7d8e9f");
    public static readonly Guid OtherTenantId = Guid.Parse("0c9d8e7f-6a5b-4c3d-2e1f-0a9b8c7d6e5f");
    public const string TenantName = "Contoso Ltd";
    public const string AdminUpn = "admin@contoso.onmicrosoft.com";

    public static readonly Guid PoloObjectId = Guid.Parse("6233fba6-0198-4277-892f-9275bf728bcc");
    public static readonly Guid PoloAppId = Guid.Parse("71487acd-ec93-476d-bd0e-6c8b31831053");
    public static readonly Guid PoloAppRegistrationObjectId = Guid.Parse("a1b2c3d4-0000-4000-8000-0000000000aa");

    public static readonly Guid GraphResourceSpId = Guid.Parse("d4c3b2a1-1111-4111-8111-000000000003");
    public static readonly Guid ExchangeResourceSpId = Guid.Parse("d4c3b2a1-2222-4222-8222-000000000002");

    public static TenantIdentity Tenant => new(TenantId, TenantName, "contoso.onmicrosoft.com", CloudEnvironment.Global);

    public static AdministratorIdentity Administrator => new(AdminUpn, "Contoso Admin", Guid.Parse("0f0f0f0f-0f0f-4f0f-8f0f-0f0f0f0f0f0f"));

    public static EntraServicePrincipal Polo => new(PoloObjectId, PoloAppId, "Polo Test Automation", "Application", true, TenantId,
        ["WindowsAzureActiveDirectoryIntegratedApp"], "Contoso");

    public static EntraServicePrincipal ReportingApp => new(Guid.Parse("5e5e5e5e-1234-4abc-9def-1234567890ab"), Guid.Parse("9a9a9a9a-4321-4cba-8fed-0987654321ba"),
        "Mailbox Reporting", "Application", true, TenantId, ["WindowsAzureActiveDirectoryIntegratedApp"], "Contoso");

    public static ExchangeRecipient Alice => Mailbox("Alice Wilber", "alice@contoso.com", "alice", "UserMailbox",
        "11111111-aaaa-4aaa-8aaa-111111111111", "aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa", "e1e1e1e1-0001-4001-8001-000000000001");

    public static ExchangeRecipient FinanceShared => Mailbox("Finance Shared", "finance@contoso.com", "finance", "SharedMailbox",
        "22222222-bbbb-4bbb-8bbb-222222222222", "bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb", "e1e1e1e1-0002-4002-8002-000000000002");

    public static ExchangeRecipient Bob => Mailbox("Bob Kelly", "bob@contoso.com", "bob", "UserMailbox",
        "33333333-cccc-4ccc-8ccc-333333333333", "cccccccc-3333-4333-8333-cccccccccccc", "e1e1e1e1-0003-4003-8003-000000000003");

    public static ExchangeRecipient SalesDistributionGroup => Mailbox("Sales Team", "sales@contoso.com", "sales", "MailUniversalDistributionGroup",
        null, "dddddddd-4444-4444-8444-dddddddddddd", "e1e1e1e1-0004-4004-8004-000000000004");

    public static ExchangeRecipient ProjectGroup => Mailbox("Project Phoenix", "phoenix@contoso.com", "phoenix", "GroupMailbox",
        "55555555-eeee-4eee-8eee-555555555555", "eeeeeeee-5555-4555-8555-eeeeeeeeeeee", "e1e1e1e1-0005-4005-8005-000000000005");

    public static ExchangeRecipient Contractor => Mailbox("External Contractor", "contractor@fabrikam.com", "contractor", "MailUser",
        null, "ffffffff-6666-4666-8666-ffffffffffff", "e1e1e1e1-0006-4006-8006-000000000006");

    public static IReadOnlyList<ExchangeRecipient> AllRecipients => [Alice, FinanceShared, Bob, SalesDistributionGroup, ProjectGroup, Contractor];

    public static List<ApplicationRoleInfo> DiscoveredApplicationRoles() =>
        ApplicationRoleCatalog.Curated.Select(r => new ApplicationRoleInfo(r.RoleName, r.Explanation, "Application")).ToList();

    public static ExchangeConnectionInfo Connection(Guid? tenantId = null) =>
        new("Connected", "Active", tenantId ?? TenantId, AdminUpn, "contoso.onmicrosoft.com", DateTimeOffset.UtcNow.AddMinutes(55), "https://outlook.office365.com/adminapi/beta/");

    public static EntraAppPermissionGrant GraphGrant(string permission) =>
        new(Guid.NewGuid(), GraphResourceSpId, WellKnownResourceApps.MicrosoftGraph, "Microsoft Graph", Guid.NewGuid(), permission, permission, DateTimeOffset.UtcNow.AddDays(-30));

    public static EntraAppPermissionGrant ExchangeGrant(string permission) =>
        new(Guid.NewGuid(), ExchangeResourceSpId, WellKnownResourceApps.Office365ExchangeOnline, "Office 365 Exchange Online", Guid.NewGuid(), permission, permission, DateTimeOffset.UtcNow.AddDays(-30));

    public static ExchangeRecipient Mailbox(string name, string smtp, string alias, string type, string? exchangeGuid, string guid, string? externalDirectoryObjectId) =>
        new($"EURPR01A001.prod.outlook.com/Microsoft Exchange Hosted Organizations/contoso.onmicrosoft.com/{alias}", name, smtp, alias,
            type.Contains("Group", StringComparison.Ordinal) ? "MailUniversalDistributionGroup" : type == "MailUser" ? "MailUser" : "UserMailbox",
            type,
            exchangeGuid is null ? Guid.Empty : Guid.Parse(exchangeGuid),
            Guid.Parse(guid),
            externalDirectoryObjectId);
}
