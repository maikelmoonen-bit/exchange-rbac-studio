using ExchangeRbacStudio.Domain.Exchange;
using ExchangeRbacStudio.Domain.Identity;
using ExchangeRbacStudio.Domain.Permissions;

namespace ExchangeRbacStudio.Tests.Domain;

public sealed class RecipientSupportTests
{
    [Fact]
    public void User_and_shared_mailboxes_are_supported()
    {
        Assert.True(RecipientSupport.Validate(Fixtures.Alice).IsSupported);
        Assert.True(RecipientSupport.Validate(Fixtures.FinanceShared).IsSupported);
    }

    [Theory]
    [InlineData("MailUniversalDistributionGroup", "group")]
    [InlineData("DynamicDistributionGroup", "group")]
    [InlineData("GroupMailbox", "Microsoft 365 group")]
    [InlineData("MailUser", "no mailbox in Exchange Online")]
    [InlineData("RemoteUserMailbox", "no mailbox in Exchange Online")]
    [InlineData("RoomMailbox", "Resource mailboxes")]
    [InlineData("MailContact", "mail contact")]
    public void Unsupported_recipient_types_are_rejected_with_an_explanation(string type, string expectedExplanation)
    {
        var recipient = Fixtures.Alice with { RecipientTypeDetails = type };
        var result = RecipientSupport.Validate(recipient);
        Assert.False(result.IsSupported);
        Assert.Contains(expectedExplanation, result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_recipient_type_is_never_accepted()
    {
        var result = RecipientSupport.Validate(Fixtures.Alice with { RecipientTypeDetails = null });
        Assert.False(result.IsSupported);
    }

    [Fact]
    public void Mailbox_without_directory_guid_is_rejected()
    {
        var result = RecipientSupport.Validate(Fixtures.Alice with { Guid = null });
        Assert.False(result.IsSupported);
        Assert.Contains("GUID", result.Reason);
    }
}

public sealed class PermissionMappingTests
{
    [Fact]
    public void Curated_catalog_contains_the_documented_application_roles()
    {
        var names = ApplicationRoleCatalog.Curated.Select(r => r.RoleName).ToHashSet();
        Assert.Equal(24, names.Count);
        foreach (var expected in new[]
                 {
                     "Application Mail.Read", "Application Mail.ReadBasic", "Application Mail.ReadWrite", "Application Mail.Send",
                     "Application Calendars.Read", "Application Calendars.ReadWrite", "Application Contacts.Read", "Application Contacts.ReadWrite",
                     "Application MailboxSettings.Read", "Application MailboxSettings.ReadWrite", "Application Mail Full Access",
                     "Application Exchange Full Access", "Application EWS.AccessAsApp", "Application SMTP.SendAsApp",
                     "Application Mail-Advanced.ReadWrite.All", "Application MailboxItem.ImportExport",
                 })
            Assert.Contains(expected, names);
    }

    [Fact]
    public void Every_curated_role_has_explanation_permissions_and_security_implications()
    {
        foreach (var role in ApplicationRoleCatalog.Curated)
        {
            Assert.False(string.IsNullOrWhiteSpace(role.Explanation), role.RoleName);
            Assert.False(string.IsNullOrWhiteSpace(role.SecurityImplications), role.RoleName);
            Assert.NotEmpty(role.ApiPermissions);
            Assert.NotEqual(MailboxCapability.None, role.Grants);
            Assert.NotEmpty(role.EffectiveCapabilities);
        }
    }

    [Fact]
    public void Mail_ReadWrite_does_not_include_Mail_Send()
    {
        Assert.False(ApplicationRoleCatalog.IsEquivalentOrBroader("Application Mail.ReadWrite", "Application Mail.Send"));
        Assert.True(ApplicationRoleCatalog.IsEquivalentOrBroader("Application Mail.ReadWrite", "Application Mail.Read"));
        Assert.True(ApplicationRoleCatalog.IsEquivalentOrBroader("Application Mail Full Access", "Application Mail.Send"));
    }

    [Fact]
    public void Lattice_expands_write_to_read_and_ews_to_all_mailbox_data()
    {
        var expanded = CapabilityLattice.Expand(MailboxCapability.CalendarsWrite);
        Assert.True(expanded.HasFlag(MailboxCapability.CalendarsRead));
        Assert.True(expanded.HasFlag(MailboxCapability.CalendarsReadBasic));

        var ews = CapabilityLattice.Expand(MailboxCapability.EwsFullAccess);
        Assert.True(ews.HasFlag(MailboxCapability.MailSend));
        Assert.True(ews.HasFlag(MailboxCapability.ContactsWrite));
        Assert.False(ews.HasFlag(MailboxCapability.ExchangeAdministration));
    }

    [Fact]
    public void Merge_marks_tenant_availability_and_adds_unknown_discovered_roles_conservatively()
    {
        var discovered = new[]
        {
            new ApplicationRoleInfo("Application Mail.Read", null, "Application"),
            new ApplicationRoleInfo("Application Tasks.ReadWrite", "Future role", "Application"),
            new ApplicationRoleInfo("Mail Recipients", null, "MailRecipients"),
        };

        var merged = ApplicationRoleCatalog.Merge(discovered);
        Assert.True(merged.Single(r => r.RoleName == "Application Mail.Read").IsAvailableInTenant);
        Assert.False(merged.Single(r => r.RoleName == "Application Mail.Send").IsAvailableInTenant);
        var unknown = merged.Single(r => r.RoleName == "Application Tasks.ReadWrite");
        Assert.False(unknown.IsCurated);
        Assert.Equal(PrivilegeLevel.High, unknown.Privilege);
        Assert.DoesNotContain(merged, r => r.RoleName == "Mail Recipients");
    }

    [Fact]
    public void Uncurated_roles_are_treated_as_overlapping_everything()
    {
        Assert.True(ApplicationRoleCatalog.Overlaps("Application Tasks.ReadWrite", "Application Mail.Read"));
    }

    [Theory]
    [InlineData("00000003-0000-0000-c000-000000000000", "Mail.Read", EntraGrantReach.TenantWideMailboxAccess)]
    [InlineData("00000003-0000-0000-c000-000000000000", "Mail.ReadBasic.All", EntraGrantReach.TenantWideMailboxAccess)]
    [InlineData("00000002-0000-0ff1-ce00-000000000000", "full_access_as_app", EntraGrantReach.TenantWideMailboxAccess)]
    [InlineData("00000002-0000-0ff1-ce00-000000000000", "IMAP.AccessAsApp", EntraGrantReach.RequiresExchangeMailboxPermission)]
    [InlineData("00000003-0000-0000-c000-000000000000", "AppRoleAssignment.ReadWrite.All", EntraGrantReach.Administrative)]
    [InlineData("00000002-0000-0ff1-ce00-000000000000", "Exchange.ManageAsApp", EntraGrantReach.Administrative)]
    public void Entra_permissions_map_to_their_reach(string resourceAppId, string permission, EntraGrantReach reach)
    {
        var definition = EntraPermissionCatalog.TryGet(Guid.Parse(resourceAppId), permission);
        Assert.NotNull(definition);
        Assert.Equal(reach, definition.Reach);
    }

    [Fact]
    public void Unrelated_entra_permissions_are_not_mailbox_relevant()
    {
        Assert.Null(EntraPermissionCatalog.TryGet(WellKnownResourceApps.MicrosoftGraph, "User.Read.All"));
        Assert.Null(EntraPermissionCatalog.TryGet(Guid.NewGuid(), "Mail.Read"));
    }
}
