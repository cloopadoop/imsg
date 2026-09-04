using WinIMsg.Core.Models;

namespace WinIMsg.App.Contracts;

public sealed record ConversationTarget(
    string StableId,
    long? ChatId,
    string? ChatIdentifier,
    string? ChatGuid,
    string? Service,
    string DisplayName,
    bool IsGroup)
{
    public static ConversationTarget FromChat(ImsgChat chat, string displayName)
    {
        return new ConversationTarget(
            StableId: chat.StableId,
            ChatId: chat.Id,
            ChatIdentifier: chat.Identifier,
            ChatGuid: chat.Guid,
            Service: chat.Service,
            DisplayName: displayName,
            IsGroup: chat.IsGroup);
    }
}
