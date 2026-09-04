using WinIMsg.App.ViewModels;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public static class InboundNotificationIdentityPolicy
{
    public static ImsgMessage WithTrustedChatTitle(
        ImsgMessage message,
        IEnumerable<ChatListItem> chats)
    {
        var chat = chats.FirstOrDefault(candidate => candidate.ContainsStableId(message.ChatStableId));
        return chat is null
            ? message with { ChatName = null }
            : message with { ChatName = chat.DisplayName };
    }
}
