using WinIMsg.Core.Cache;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Contracts;

public interface IMessageCache
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task UpsertChatsAsync(IEnumerable<ImsgChat> chats, CancellationToken cancellationToken = default);

    Task UpsertMessagesAsync(IEnumerable<ImsgMessage> messages, CancellationToken cancellationToken = default);

    Task<int> ReconcileMessagesForFetchedWindowAsync(
        string chatStableId,
        IEnumerable<ImsgMessage> fetchedMessages,
        bool fetchedAllAvailableHistory = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImsgChat>> GetChatsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImsgMessage>> GetMessagesAsync(string chatStableId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImsgMessage>> GetRecentMessagesAsync(
        string chatStableId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, string>> GetLatestMessageTextByChatStableIdAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, DateTimeOffset>> GetLatestMessageDateByChatStableIdAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImsgMessage>> GetMessagesBeforeAsync(
        string chatStableId,
        string beforeDateValue,
        long? beforeMessageRowId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> SearchChatStableIdsByMessageContentAsync(
        string query,
        int limit = 5000,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, ChatSyncState>> GetChatSyncStatesAsync(CancellationToken cancellationToken = default);

    Task<long?> GetWatchCursorAsync(string scope, CancellationToken cancellationToken = default);

    Task SaveWatchCursorAsync(string scope, long rowId, CancellationToken cancellationToken = default);

    Task MarkChatSyncSucceededAsync(
        string stableId,
        int deepestFetchedLimit,
        CancellationToken cancellationToken = default);

    Task MarkChatSyncFailedAsync(
        string stableId,
        string error,
        DateTimeOffset retryAfter,
        CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
