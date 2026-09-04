using WinIMsg.App.Contracts;
using WinIMsg.Core.Cache;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public sealed class MessageCacheAdapter(SqliteMessageCache inner) : IMessageCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.InitializeAsync(cancellationToken), cancellationToken);

    public Task UpsertChatsAsync(IEnumerable<ImsgChat> chats, CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.UpsertChatsAsync(chats.ToList(), cancellationToken), cancellationToken);

    public Task UpsertMessagesAsync(IEnumerable<ImsgMessage> messages, CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.UpsertMessagesAsync(messages.ToList(), cancellationToken), cancellationToken);

    public Task<int> ReconcileMessagesForFetchedWindowAsync(
        string chatStableId,
        IEnumerable<ImsgMessage> fetchedMessages,
        bool fetchedAllAvailableHistory = false,
        CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.ReconcileMessagesForFetchedWindowAsync(chatStableId, fetchedMessages.ToList(), fetchedAllAvailableHistory, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<ImsgChat>> GetChatsAsync(CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.GetChatsAsync(cancellationToken), cancellationToken);

    public Task<IReadOnlyList<ImsgMessage>> GetMessagesAsync(string chatStableId, CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.GetMessagesAsync(chatStableId, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<ImsgMessage>> GetRecentMessagesAsync(
        string chatStableId,
        int limit,
        CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.GetRecentMessagesAsync(chatStableId, limit, cancellationToken), cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> GetLatestMessageTextByChatStableIdAsync(
        CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.GetLatestMessageTextByChatStableIdAsync(cancellationToken), cancellationToken);

    public Task<IReadOnlyDictionary<string, DateTimeOffset>> GetLatestMessageDateByChatStableIdAsync(
        CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.GetLatestMessageDateByChatStableIdAsync(cancellationToken), cancellationToken);

    public Task<IReadOnlyList<ImsgMessage>> GetMessagesBeforeAsync(
        string chatStableId,
        string beforeDateValue,
        long? beforeMessageRowId,
        int limit,
        CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.GetMessagesBeforeAsync(chatStableId, beforeDateValue, beforeMessageRowId, limit, cancellationToken), cancellationToken);

    public Task<IReadOnlySet<string>> SearchChatStableIdsByMessageContentAsync(
        string query,
        int limit = 5000,
        CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.SearchChatStableIdsByMessageContentAsync(query, limit, cancellationToken), cancellationToken);

    public Task<IReadOnlyDictionary<string, ChatSyncState>> GetChatSyncStatesAsync(CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.GetChatSyncStatesAsync(cancellationToken), cancellationToken);

    public Task<long?> GetWatchCursorAsync(string scope, CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.GetWatchCursorAsync(scope, cancellationToken), cancellationToken);

    public Task SaveWatchCursorAsync(string scope, long rowId, CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.SaveWatchCursorAsync(scope, rowId, cancellationToken), cancellationToken);

    public Task MarkChatSyncSucceededAsync(
        string stableId,
        int deepestFetchedLimit,
        CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.MarkChatSyncSucceededAsync(stableId, deepestFetchedLimit, cancellationToken), cancellationToken);

    public Task MarkChatSyncFailedAsync(
        string stableId,
        string error,
        DateTimeOffset retryAfter,
        CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.MarkChatSyncFailedAsync(stableId, error, retryAfter, cancellationToken), cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        RunCacheAsync(() => inner.ClearAsync(cancellationToken), cancellationToken);

    private Task RunCacheAsync(Func<Task> action, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await action();
            }
            finally
            {
                _gate.Release();
            }
        }, cancellationToken);

    private Task<T> RunCacheAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await action();
            }
            finally
            {
                _gate.Release();
            }
        }, cancellationToken);
}
