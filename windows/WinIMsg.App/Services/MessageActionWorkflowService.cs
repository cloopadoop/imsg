using WinIMsg.App.Contracts;
using WinIMsg.App.ViewModels;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public sealed class MessageActionWorkflowService(IImsgClient client)
{
    public async Task<string?> SendTapbackAsync(
        ChatListItem selectedChat,
        ImsgMessage message,
        TapbackChoice tapback,
        ImsgCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        var messageActionId = RequireActionId(message, "Tapbacks require an actionable message GUID and imsg tapback support.");
        if (!CapabilityActionPolicy.CanTapback(message, capabilities))
        {
            throw new InvalidOperationException("Tapbacks require an actionable message GUID and imsg tapback support.");
        }

        var reactionKind = TapbackReactionPolicy.NormalizeKind(tapback.Reaction);
        if (string.IsNullOrWhiteSpace(reactionKind))
        {
            throw new InvalidOperationException("Unsupported tapback reaction. Use love, like, dislike, laugh, emphasize, or question.");
        }

        var actionChat = MessageActionTargetResolver.Resolve(selectedChat, message);
        await client.TapbackAsync(
            actionChat.Id,
            actionChat.Identifier,
            messageActionId,
            reactionKind,
            tapback.Remove,
            actionChat.Guid,
            cancellationToken);
        return tapback.Remove ? "Tapback removed." : "Tapback sent.";
    }

    public async Task<string?> EditMessageAsync(
        ChatListItem selectedChat,
        ImsgMessage message,
        string text,
        ImsgCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        var messageActionId = RequireActionId(message, "Only sent, non-reaction messages with an upstream GUID can be edited.");
        if (!CapabilityActionPolicy.CanEdit(message, capabilities))
        {
            throw new InvalidOperationException("Only sent, non-reaction messages with an upstream GUID can be edited.");
        }

        var actionChat = MessageActionTargetResolver.Resolve(selectedChat, message);
        await client.EditMessageAsync(
            actionChat.Id,
            actionChat.Identifier,
            messageActionId,
            text,
            actionChat.Guid,
            cancellationToken);
        return null;
    }

    public async Task<string?> UnsendMessageAsync(
        ChatListItem selectedChat,
        ImsgMessage message,
        ImsgCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        var messageActionId = RequireActionId(message, "Only sent, non-reaction messages with an upstream GUID can be unsent.");
        if (!CapabilityActionPolicy.CanUnsend(message, capabilities))
        {
            throw new InvalidOperationException("Only sent, non-reaction messages with an upstream GUID can be unsent.");
        }

        var actionChat = MessageActionTargetResolver.Resolve(selectedChat, message);
        await client.UnsendMessageAsync(
            actionChat.Id,
            actionChat.Identifier,
            messageActionId,
            actionChat.Guid,
            cancellationToken);
        return null;
    }

    public async Task<string?> DeleteMessageAsync(
        ChatListItem selectedChat,
        ImsgMessage message,
        ImsgCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        var messageActionId = RequireActionId(message, "Delete requires an actionable non-reaction message GUID and imsg delete support.");
        if (!CapabilityActionPolicy.CanDelete(message, capabilities))
        {
            throw new InvalidOperationException("Delete requires an actionable non-reaction message GUID and imsg delete support.");
        }

        var actionChat = MessageActionTargetResolver.Resolve(selectedChat, message);
        await client.DeleteMessageAsync(
            actionChat.Id,
            actionChat.Identifier,
            messageActionId,
            actionChat.Guid,
            cancellationToken);
        return null;
    }

    public async Task<string?> NotifyAnywaysAsync(
        ChatListItem selectedChat,
        ImsgMessage message,
        ImsgCapabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        var messageActionId = RequireActionId(message, "Notify Anyway requires a sent message with an upstream GUID and imsg notifyAnyways support.");
        if (!CapabilityActionPolicy.CanNotifyAnyways(message, capabilities))
        {
            throw new InvalidOperationException("Notify Anyway requires a sent message with an upstream GUID and imsg notifyAnyways support.");
        }

        var actionChat = MessageActionTargetResolver.Resolve(selectedChat, message);
        await client.NotifyAnywaysAsync(
            actionChat.Id,
            actionChat.Identifier,
            messageActionId,
            actionChat.Guid,
            cancellationToken);
        return "Notify Anyway sent.";
    }

    private static string RequireActionId(ImsgMessage message, string failureMessage)
    {
        if (string.IsNullOrWhiteSpace(message.MessageActionId))
        {
            throw new InvalidOperationException(failureMessage);
        }

        return message.MessageActionId;
    }
}
