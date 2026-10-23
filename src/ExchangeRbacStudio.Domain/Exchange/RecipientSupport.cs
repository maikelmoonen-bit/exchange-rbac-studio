namespace ExchangeRbacStudio.Domain.Exchange;

public sealed record RecipientValidation(bool IsSupported, string Status, string? Reason);

/// <summary>
/// Decides which recipients can be direct targets of an application RBAC mailbox scope in this release.
/// Validation is always based on the actual RecipientTypeDetails returned by Exchange Online.
/// </summary>
public static class RecipientSupport
{
    public static IReadOnlySet<string> SupportedTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "UserMailbox", "SharedMailbox" };

    public static RecipientValidation Validate(ExchangeRecipient recipient)
    {
        var type = recipient.RecipientTypeDetails;
        if (string.IsNullOrWhiteSpace(type))
            return new(false, "Unknown type", "Exchange Online did not return a recipient type for this object, so it cannot be validated as a mailbox.");

        if (SupportedTypes.Contains(type))
        {
            if (recipient.Guid is null || recipient.Guid == Guid.Empty)
                return new(false, "Missing identity", "The recipient has no directory object GUID, so an exact scope filter cannot be built.");
            return new(true, "Validated", null);
        }

        return new(false, "Unsupported", ExplainUnsupported(type));
    }

    public static string ExplainUnsupported(string recipientTypeDetails) => recipientTypeDetails switch
    {
        "MailUniversalDistributionGroup" or "MailNonUniversalGroup" or "MailUniversalSecurityGroup" or "DynamicDistributionGroup" or "RoomList" =>
            "This is a group, not a mailbox. Application RBAC authorizes access to the target mailbox itself, so groups cannot be selected as direct targets. Select the member mailboxes instead.",
        "GroupMailbox" =>
            "This is a Microsoft 365 group mailbox. Microsoft 365 groups are not supported as direct mailbox targets in this release.",
        "RoomMailbox" or "EquipmentMailbox" =>
            "Resource mailboxes (rooms and equipment) are not supported as targets in this release. Only user and shared mailboxes are accepted.",
        "MailUser" or "GuestMailUser" or "RemoteUserMailbox" or "RemoteSharedMailbox" or "RemoteRoomMailbox" or "RemoteEquipmentMailbox" =>
            "This recipient has no mailbox in Exchange Online (it is a mail user or an on-premises remote mailbox). Application RBAC can only authorize access to mailboxes hosted in Exchange Online.",
        "MailContact" or "GuestMailContact" =>
            "This is a mail contact. Contacts have no mailbox, so they cannot be targets of application access.",
        "PublicFolder" or "PublicFolderMailbox" =>
            "Public folders and public folder mailboxes are not supported as application RBAC targets in this release.",
        "DiscoveryMailbox" or "SchedulingMailbox" or "TeamMailbox" or "LinkedMailbox" or "LegacyMailbox" =>
            $"Mailboxes of type '{recipientTypeDetails}' are not supported as targets in this release. Only user and shared mailboxes are accepted.",
        _ =>
            $"Recipients of type '{recipientTypeDetails}' are not supported. Only UserMailbox and SharedMailbox recipients can be selected.",
    };
}
