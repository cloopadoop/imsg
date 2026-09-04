using WinIMsg.Core.Models;
using WinIMsg.App.ViewModels;

namespace WinIMsg.App.Services;

public static class CapabilityActionPolicy
{
    public static bool CanSend(bool hasSendTarget, bool connected, ImsgCapabilities capabilities) =>
        hasSendTarget && connected && SupportsBaselineSend(capabilities);

    public static bool CanSendAttachment(bool hasSelectedChat, bool connected, ImsgCapabilities capabilities) =>
        hasSelectedChat && connected && SupportsBaselineSend(capabilities);

    public static bool CanSendRich(bool hasSelectedChat, bool connected, ImsgCapabilities capabilities) =>
        hasSelectedChat && connected && capabilities.HasAdvancedBridge && capabilities.Supports("send.rich");

    public static bool CanSendPoll(bool hasSelectedChat, bool connected, ImsgCapabilities capabilities) =>
        hasSelectedChat && connected && capabilities.HasAdvancedBridge && capabilities.SupportsAny("poll.send", "messages.poll.send");

    public static bool CanSendTyping(bool hasSelectedChat, bool connected, ImsgCapabilities capabilities) =>
        hasSelectedChat && connected && capabilities.TypingIndicators && capabilities.Supports("typing");

    // The Messages helper injection does not survive Messages.app restarts and
    // imsg status never re-injects; when the Mac is capable (SIP disabled,
    // advanced methods advertised) but the bridge is down, one imsg launch
    // brings tapback/read/typing back without manual Mac work.
    public static bool ShouldAttemptBridgeRepair(ImsgCapabilities capabilities) =>
        !capabilities.HasAdvancedBridge &&
        capabilities.Supports("tapback") &&
        string.Equals(JsonElementReaders.ReadString(capabilities.Sip), "disabled", StringComparison.OrdinalIgnoreCase);

    public static bool CanReply(ImsgMessage message, ImsgCapabilities capabilities) =>
        HasActionableServerGuid(message) && capabilities.HasAdvancedBridge && capabilities.Supports("send.rich");

    public static bool CanTapback(ImsgMessage message, ImsgCapabilities capabilities) =>
        HasActionableServerGuid(message) &&
        capabilities.HasAdvancedBridge &&
        capabilities.SupportsAny("tapback", "message.tapback", "send.tapback", "reaction", "message.reaction");

    public static bool CanEdit(ImsgMessage message, ImsgCapabilities capabilities) =>
        message.IsFromMe &&
        HasActionableServerGuid(message) &&
        capabilities.SupportsAny("message.edit", "edit") &&
        capabilities.SupportsSelector("editMessage", "editMessageItem");

    public static bool CanUnsend(ImsgMessage message, ImsgCapabilities capabilities) =>
        message.IsFromMe &&
        HasActionableServerGuid(message) &&
        capabilities.SupportsAny("message.unsend", "unsend") &&
        capabilities.SupportsSelector("retractMessagePart");

    public static bool CanDelete(ImsgMessage message, ImsgCapabilities capabilities) =>
        HasActionableServerGuid(message) && capabilities.SupportsAny("message.delete", "delete");

    public static bool CanNotifyAnyways(ImsgMessage message, ImsgCapabilities capabilities) =>
        message.IsFromMe &&
        HasActionableServerGuid(message) &&
        capabilities.SupportsAny("message.notifyAnyways", "notifyAnyways", "notify-anyways");

    public static bool CanMarkRead(bool hasSelectedChat, bool connected, ImsgCapabilities capabilities) =>
        hasSelectedChat && connected && capabilities.SupportsAny("read", "message.read");

    public static bool CanMarkUnread(bool hasSelectedChat, bool connected, ImsgCapabilities capabilities) =>
        hasSelectedChat && connected && capabilities.Supports("chats.markUnread");

    public static bool CanDeleteChat(bool hasSelectedChat, bool connected, ImsgCapabilities capabilities) =>
        hasSelectedChat && connected && capabilities.Supports("chats.delete");

    public static bool CanCreateFaceTimeLink(bool hasSelectedChat, bool connected, bool faceTimeLinkAvailable) =>
        hasSelectedChat && connected && faceTimeLinkAvailable;

    public static bool CanRenameGroup(bool hasGroupGuid, bool connected, ImsgCapabilities capabilities) =>
        hasGroupGuid && connected && capabilities.Supports("group.rename");

    public static bool CanSetGroupIcon(bool hasGroupGuid, bool connected, ImsgCapabilities capabilities) =>
        hasGroupGuid && connected && capabilities.Supports("group.setIcon");

    public static bool CanAddParticipant(bool hasGroupGuid, bool connected, ImsgCapabilities capabilities) =>
        hasGroupGuid && connected && capabilities.Supports("group.addParticipant");

    public static bool CanRemoveParticipant(bool hasGroupGuid, bool connected, ImsgCapabilities capabilities) =>
        hasGroupGuid && connected && capabilities.Supports("group.removeParticipant");

    public static bool CanLeaveGroup(bool hasGroupGuid, bool connected, ImsgCapabilities capabilities) =>
        hasGroupGuid && connected && capabilities.Supports("group.leave");

    private static bool HasActionableServerGuid(ImsgMessage message) =>
        !message.IsReactionEvent &&
        !string.IsNullOrWhiteSpace(message.MessageActionId) &&
        !message.MessageActionId.StartsWith("pending:", StringComparison.OrdinalIgnoreCase);

    private static bool SupportsBaselineSend(ImsgCapabilities capabilities) =>
        capabilities.RpcMethods.Count == 0 || capabilities.Supports("send");
}

public static class TapbackReactionPolicy
{
    public static string? NormalizeKind(string? value)
    {
        var normalized = DisplayTextFormatter.SingleLine(value, string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.StartsWith("remove-", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["remove-".Length..];
        }

        return normalized switch
        {
            "love" or "loved" or "heart" or "\u2764" or "\u2764\ufe0f" or "\ud83d\udc99" or "\ud83d\udc9a" or "\ud83d\udc9b" or "\ud83d\udc9c" =>
                "love",
            "like" or "liked" or "thumbsup" or "thumbs-up" or "thumbs_up" or "thumbs up" or "+1" or "\ud83d\udc4d" =>
                "like",
            "dislike" or "disliked" or "thumbsdown" or "thumbs-down" or "thumbs_down" or "thumbs down" or "-1" or "\ud83d\udc4e" =>
                "dislike",
            "laugh" or "laughed" or "haha" or "ha ha" or "lol" or "\ud83d\ude02" or "\ud83e\udd23" =>
                "laugh",
            "emphasis" or "emphasize" or "emphasized" or "exclaim" or "exclamation" or "!!" or "\u203c" or "\u203c\ufe0f" or "\u2757" or "\u2755" =>
                "emphasize",
            "question" or "questioned" or "questionmark" or "question-mark" or "?" or "??" or "\u2753" or "\u2754" =>
                "question",
            _ => null
        };
    }
}

public static class MessageActionTargetResolver
{
    public static ImsgChat Resolve(ChatListItem selectedChat, ImsgMessage message)
    {
        foreach (var source in selectedChat.Chats)
        {
            if (Matches(source, message))
            {
                return WithMessageTargetFallbacks(source, message);
            }
        }

        return HasMessageTarget(message)
            ? WithMessageTargetFallbacks(selectedChat.Chat, message)
            : selectedChat.Chat;
    }

    private static ImsgChat WithMessageTargetFallbacks(ImsgChat chat, ImsgMessage message)
    {
        return chat with
        {
            Id = message.ChatId ?? chat.Id,
            Identifier = string.IsNullOrWhiteSpace(message.ChatIdentifier) ? chat.Identifier : message.ChatIdentifier,
            Guid = string.IsNullOrWhiteSpace(message.ChatGuid) ? chat.Guid : message.ChatGuid
        };
    }

    private static bool Matches(ImsgChat chat, ImsgMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.ChatStableId) &&
            string.Equals(chat.StableId, message.ChatStableId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (message.ChatId is not null && chat.Id == message.ChatId)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(message.ChatGuid) &&
            string.Equals(chat.Guid, message.ChatGuid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(message.ChatIdentifier) &&
            string.Equals(chat.Identifier, message.ChatIdentifier, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasMessageTarget(ImsgMessage message) =>
        message.ChatId is not null ||
        !string.IsNullOrWhiteSpace(message.ChatIdentifier) ||
        !string.IsNullOrWhiteSpace(message.ChatGuid);
}
