using System.Runtime.CompilerServices;
using WinIMsg.App.Contracts;
using WinIMsg.App.ViewModels;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public sealed class ConversationDataService(IMessageCache cache, IImsgClient client)
{
    public async Task<IReadOnlyList<ChatListItem>> LoadCachedChatsAsync(CancellationToken cancellationToken = default)
    {
        var chats = await cache.GetChatsAsync(cancellationToken);
        var latestMessagePreviews = await cache.GetLatestMessageTextByChatStableIdAsync(cancellationToken);
        var latestMessageDates = await cache.GetLatestMessageDateByChatStableIdAsync(cancellationToken);
        return ChatListItem.FromChats(
            chats,
            mergeByParticipants: true,
            latestMessagePreviewsByStableId: latestMessagePreviews,
            latestMessageDatesByStableId: latestMessageDates);
    }

    public async Task<IReadOnlyList<ChatListItem>> RefreshChatsAsync(int limit = 10000, CancellationToken cancellationToken = default)
    {
        var chats = await client.ListChatsAsync(limit, cancellationToken);
        await cache.UpsertChatsAsync(chats, cancellationToken);
        var latestMessagePreviews = await cache.GetLatestMessageTextByChatStableIdAsync(cancellationToken);
        var latestMessageDates = await cache.GetLatestMessageDateByChatStableIdAsync(cancellationToken);
        return ChatListItem.FromChats(
            chats,
            mergeByParticipants: true,
            latestMessagePreviewsByStableId: latestMessagePreviews,
            latestMessageDatesByStableId: latestMessageDates);
    }

    public async IAsyncEnumerable<ConversationLoadResult> LoadSelectedConversationAsync(
        ChatListItem chat,
        int remoteLimit,
        ConversationRemoteHistoryLoader? remoteLoader = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        int cachedInitialLimit = 0)
    {
        var target = ConversationTarget.FromChat(chat.Chat, chat.DisplayName);
        var sourceChats = chat.Chats.Count > 0 ? chat.Chats : [chat.Chat];
        var cacheLimit = cachedInitialLimit > 0 ? cachedInitialLimit : remoteLimit;
        var cachedMessagesBySource = new List<IReadOnlyList<ImsgMessage>>();
        foreach (var sourceChat in sourceChats)
        {
            cachedMessagesBySource.Add(await cache.GetRecentMessagesAsync(sourceChat.StableId, cacheLimit, cancellationToken));
        }

        var cachedMessages = SortMessages(cachedMessagesBySource.SelectMany(static messages => messages));
        var sourceChatsWithRemoteId = sourceChats.Where(static sourceChat => sourceChat.Id is not null).ToList();
        var cachedMayHaveOlderHistory = cachedMessagesBySource.Any(messages => messages.Count >= cacheLimit);
        yield return new ConversationLoadResult(
            target,
            cachedMessages,
            ConversationLoadSource.Cache,
            IsLoading: client.IsConnected && sourceChatsWithRemoteId.Count > 0,
            Error: null,
            MayHaveOlderHistory: cachedMayHaveOlderHistory);

        if (!client.IsConnected || sourceChatsWithRemoteId.Count == 0)
        {
            yield break;
        }

        var remoteMessages = new List<ImsgMessage>();
        var remoteErrors = new List<string>();
        var successfulFetches = new List<SuccessfulHistoryFetch>();
        foreach (var sourceChat in sourceChatsWithRemoteId)
        {
            try
            {
                IReadOnlyList<ImsgMessage> sourceMessages;
                var fetchedLimit = remoteLimit;
                if (remoteLoader is not null)
                {
                    var remoteResult = await remoteLoader(ChatListItem.From(sourceChat), remoteLimit, cancellationToken);
                    sourceMessages = remoteResult.Messages;
                    fetchedLimit = remoteResult.FetchedLimit;
                }
                else
                {
                    sourceMessages = await client.GetHistoryAsync(
                        sourceChat.Id!.Value,
                        remoteLimit,
                        includeAttachments: true,
                        convertAttachments: true,
                        includeReactions: true,
                        cancellationToken);
                }

                remoteMessages.AddRange(sourceMessages);
                successfulFetches.Add(new SuccessfulHistoryFetch(sourceChat.StableId, fetchedLimit, sourceMessages));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                remoteErrors.Add($"{sourceChat.DisplayName}: {ex.Message}");
            }
        }

        if (remoteMessages.Count == 0)
        {
            yield return new ConversationLoadResult(
                target,
                cachedMessages,
                ConversationLoadSource.CacheFallback,
                IsLoading: false,
                Error: string.Join("; ", remoteErrors),
                MayHaveOlderHistory: cachedMayHaveOlderHistory);
            yield break;
        }

        await cache.UpsertMessagesAsync(remoteMessages, cancellationToken);
        foreach (var fetch in successfulFetches)
        {
            await cache.ReconcileMessagesForFetchedWindowAsync(
                fetch.StableId,
                fetch.Messages,
                fetch.FetchedLimit > 0 && fetch.Messages.Count < fetch.FetchedLimit,
                cancellationToken);
            await cache.MarkChatSyncSucceededAsync(fetch.StableId, fetch.FetchedLimit, cancellationToken);
        }

        yield return new ConversationLoadResult(
            target,
            SortMessages(remoteMessages),
            ConversationLoadSource.Remote,
            IsLoading: false,
            Error: remoteErrors.Count == 0 ? null : string.Join("; ", remoteErrors),
            MayHaveOlderHistory: successfulFetches.Any(fetch => fetch.Messages.Count >= fetch.FetchedLimit));
    }

    public async Task<IReadOnlyList<ImsgMessage>> LoadOlderCachedMessagesAsync(
        ChatListItem chat,
        string beforeDateValue,
        long? beforeMessageRowId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (pageSize <= 0 || string.IsNullOrWhiteSpace(beforeDateValue))
        {
            return [];
        }

        var sourceChats = chat.Chats.Count > 0 ? chat.Chats : [chat.Chat];
        var pages = new List<ImsgMessage>();
        foreach (var sourceChat in sourceChats)
        {
            pages.AddRange(await cache.GetMessagesBeforeAsync(
                sourceChat.StableId,
                beforeDateValue,
                beforeMessageRowId,
                pageSize,
                cancellationToken));
        }

        var sorted = SortMessages(pages);
        return sorted.Count <= pageSize
            ? sorted
            : sorted.Skip(sorted.Count - pageSize).ToList();
    }

    private static IReadOnlyList<ImsgMessage> SortMessages(IEnumerable<ImsgMessage> messages)
    {
        return messages
            .Select((message, index) => new { Message = message, Index = index })
            .OrderBy(item => item.Message.SortDate ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Message.Id ?? long.MaxValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Message)
            .ToList();
    }
}

file sealed record SuccessfulHistoryFetch(
    string StableId,
    int FetchedLimit,
    IReadOnlyList<ImsgMessage> Messages);

public delegate Task<ConversationRemoteHistoryResult> ConversationRemoteHistoryLoader(
    ChatListItem chat,
    int requestedLimit,
    CancellationToken cancellationToken);

public sealed record ConversationRemoteHistoryResult(IReadOnlyList<ImsgMessage> Messages, int FetchedLimit);
