using System.Globalization;
using WinIMsg.App.Contracts;
using WinIMsg.Core.Cache;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public sealed class ConversationSyncService(IMessageCache cache, IImsgClient client)
{
    public async Task<ConversationSyncResult> SyncAsync(
        IReadOnlyList<ImsgChat> chats,
        int historyLimit,
        TimeSpan failureBackoff,
        IProgress<SyncProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await SyncAsync(
            chats,
            new ConversationSyncOptions(historyLimit, failureBackoff),
            historyLoader: null,
            progress,
            syncedChatProgress: null,
            cancellationToken);
    }

    public async Task<ConversationSyncResult> SyncAsync(
        IReadOnlyList<ImsgChat> chats,
        ConversationSyncOptions options,
        ConversationSyncHistoryLoader? historyLoader = null,
        IProgress<SyncProgressSnapshot>? progress = null,
        IProgress<ConversationSyncedChat>? syncedChatProgress = null,
        CancellationToken cancellationToken = default)
    {
        var orderedChats = OrderChatsForSync(chats.Where(chat => chat.Id is not null)).ToList();
        var cappedOut = 0;
        if (options.MaxActiveChats is { } cap && cap > 0 && orderedChats.Count > cap)
        {
            // Background sweeps only walk the most recently active chats; a
            // full-archive walk is opt-in via manual sync. Keep the selected
            // chat in scope even when it falls past the cap.
            var kept = orderedChats.Take(cap).ToList();
            var selectedId = options.GetSelectedStableId?.Invoke() ?? options.SelectedStableId;
            if (!string.IsNullOrWhiteSpace(selectedId) &&
                !kept.Any(chat => string.Equals(chat.StableId, selectedId, StringComparison.OrdinalIgnoreCase)))
            {
                var selected = orderedChats.Skip(cap).FirstOrDefault(chat =>
                    string.Equals(chat.StableId, selectedId, StringComparison.OrdinalIgnoreCase));
                if (selected is not null)
                {
                    kept.Add(selected);
                }
            }

            cappedOut = orderedChats.Count - kept.Count;
            orderedChats = kept;
        }

        var syncStates = await cache.GetChatSyncStatesAsync(cancellationToken);
        var startedAt = DateTimeOffset.UtcNow;
        var runStates = orderedChats
            .Select((chat, index) => new ConversationSyncRunState(
                chat,
                index,
                syncStates.TryGetValue(chat.StableId, out var state) ? state : null))
            .ToList();
        var failed = 0;
        var skipped = 0;
        var synced = 0;
        var partial = 0;
        var failures = new List<ConversationSyncFailure>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completed = runStates.Count(state => state.IsComplete);
            if (completed >= runStates.Count)
            {
                break;
            }

            var selectedStableId = options.GetSelectedStableId?.Invoke() ?? options.SelectedStableId;
            var runState = SelectNextState(runStates, selectedStableId);
            if (runState is null)
            {
                break;
            }

            var chat = runState.Chat;
            ReportProgress(progress, chat, completed, runStates.Count, failed, skipped, startedAt);

            if (IsChatSyncCurrent(chat, runState.DurableState, options.HistoryLimit) ||
                IsChatInSyncBackoff(runState.DurableState))
            {
                runState.IsSkipped = true;
                skipped++;
                ReportProgress(progress, chat, completed + 1, runStates.Count, failed, skipped, startedAt);
                continue;
            }

            if (options.TryTakeExternallySatisfied?.Invoke(chat.StableId) == true)
            {
                await cache.MarkChatSyncSucceededAsync(
                    chat.StableId,
                    options.ExternallySatisfiedFetchedLimit ?? options.HistoryLimit,
                    cancellationToken);
                runState.IsSynced = true;
                synced++;
                ReportProgress(progress, chat, completed + 1, runStates.Count, failed, skipped, startedAt);
                continue;
            }

            try
            {
                ConversationSyncHistoryResult historyResult;
                if (historyLoader is not null)
                {
                    historyResult = await historyLoader(chat, options.HistoryLimit, cancellationToken);
                }
                else if (client.IsConnected && chat.Id is not null)
                {
                    var messages = await client.GetHistoryAsync(
                        chat.Id.Value,
                        options.HistoryLimit,
                        includeAttachments: true,
                        convertAttachments: true,
                        includeReactions: true,
                        cancellationToken);
                    historyResult = new ConversationSyncHistoryResult(messages, options.HistoryLimit, IsPartial: false);
                }
                else
                {
                    runState.IsSkipped = true;
                    skipped++;
                    ReportProgress(progress, chat, completed + 1, runStates.Count, failed, skipped, startedAt);
                    continue;
                }

                await cache.UpsertMessagesAsync(historyResult.Messages, cancellationToken);
                if (!historyResult.IsPartial)
                {
                    await cache.ReconcileMessagesForFetchedWindowAsync(
                        chat.StableId,
                        historyResult.Messages,
                        historyResult.FetchedLimit > 0 && historyResult.Messages.Count < historyResult.FetchedLimit,
                        cancellationToken);
                }

                await cache.MarkChatSyncSucceededAsync(chat.StableId, historyResult.FetchedLimit, cancellationToken);
                runState.IsSynced = true;
                synced++;
                if (historyResult.IsPartial)
                {
                    partial++;
                }

                syncedChatProgress?.Report(new ConversationSyncedChat(chat, historyResult.Messages, historyResult.FetchedLimit, historyResult.IsPartial));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                runState.IsFailed = true;
                failed++;
                failures.Add(new ConversationSyncFailure(chat.StableId, chat.DisplayName, ex.Message));
                await cache.MarkChatSyncFailedAsync(
                    chat.StableId,
                    ex.Message,
                    DateTimeOffset.UtcNow.Add(options.FailureBackoff),
                    CancellationToken.None);
            }

            ReportProgress(progress, chat, completed + 1, runStates.Count, failed, skipped, startedAt);
        }

        return new ConversationSyncResult(orderedChats.Count, failed, skipped, failures, synced, partial, cappedOut);
    }

    public static IEnumerable<ImsgChat> OrderChatsForSync(IEnumerable<ImsgChat> chats)
    {
        return chats
            .Select((chat, index) => new { Chat = chat, Index = index, SortDate = ReadChatSortDate(chat.LastMessageAt) })
            .OrderByDescending(item => item.SortDate ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Chat);
    }

    public static bool IsChatSyncCurrent(ImsgChat chat, ChatSyncState? syncState, int requiredHistoryLimit = 0)
    {
        if (syncState?.LastSuccessfulSyncAt is null ||
            (requiredHistoryLimit > 0 && syncState.DeepestFetchedLimit < requiredHistoryLimit))
        {
            return false;
        }

        var lastSyncAt = ReadChatSortDate(syncState.LastSuccessfulSyncAt);
        var lastMessageAt = ReadChatSortDate(chat.LastMessageAt);
        return lastSyncAt is not null && (lastMessageAt is null || lastSyncAt.Value >= lastMessageAt.Value);
    }

    public static bool IsChatInSyncBackoff(ChatSyncState? syncState)
    {
        var retryAfter = ReadChatSortDate(syncState?.RetryAfter);
        return retryAfter is not null && retryAfter.Value > DateTimeOffset.UtcNow;
    }

    public static TimeSpan? EstimateRemainingTime(int completedChats, int totalChats, TimeSpan elapsed)
    {
        if (completedChats <= 0 || totalChats <= 0 || completedChats >= totalChats)
        {
            return null;
        }

        var averageSeconds = elapsed.TotalSeconds / completedChats;
        return TimeSpan.FromSeconds(Math.Max(0, averageSeconds * (totalChats - completedChats)));
    }

    private static DateTimeOffset? ReadChatSortDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : null;
    }

    private static void ReportProgress(
        IProgress<SyncProgressSnapshot>? progress,
        ImsgChat chat,
        int completed,
        int total,
        int failed,
        int skipped,
        DateTimeOffset startedAt)
    {
        if (progress is null)
        {
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        progress.Report(new SyncProgressSnapshot(
            ActiveChatName: chat.DisplayName,
            CompletedCount: completed,
            TotalCount: total,
            FailedCount: failed,
            SkippedCount: skipped,
            Percent: total <= 0 ? 0 : (double)completed / total * 100,
            Elapsed: elapsed,
            EstimatedRemaining: EstimateRemainingTime(completed, total, elapsed)));
    }

    private static ConversationSyncRunState? SelectNextState(IReadOnlyList<ConversationSyncRunState> states, string? selectedStableId)
    {
        if (!string.IsNullOrWhiteSpace(selectedStableId))
        {
            var selected = states.FirstOrDefault(state =>
                !state.IsComplete &&
                string.Equals(state.Chat.StableId, selectedStableId, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                return selected;
            }
        }

        return states
            .Where(state => !state.IsComplete)
            .OrderBy(state => state.SortIndex)
            .FirstOrDefault();
    }

    private sealed class ConversationSyncRunState(ImsgChat chat, int sortIndex, ChatSyncState? durableState)
    {
        public ImsgChat Chat { get; } = chat;

        public int SortIndex { get; } = sortIndex;

        public ChatSyncState? DurableState { get; } = durableState;

        public bool IsSynced { get; set; }

        public bool IsSkipped { get; set; }

        public bool IsFailed { get; set; }

        public bool IsComplete => IsSynced || IsSkipped || IsFailed;
    }
}

public sealed record ConversationSyncResult(
    int TotalChats,
    int FailedChats,
    int SkippedChats,
    IReadOnlyList<ConversationSyncFailure> Failures,
    int SyncedChats = 0,
    int PartialChats = 0,
    int CappedOutChats = 0);

public sealed record ConversationSyncFailure(string StableId, string DisplayName, string Error);

public sealed record ConversationSyncOptions(
    int HistoryLimit,
    TimeSpan FailureBackoff,
    string? SelectedStableId = null,
    int? ExternallySatisfiedFetchedLimit = null)
{
    public Func<string, bool>? TryTakeExternallySatisfied { get; init; }

    public Func<string?>? GetSelectedStableId { get; init; }

    /// <summary>Most-recently-active chat cap for background sweeps; null walks everything.</summary>
    public int? MaxActiveChats { get; init; }
}

public delegate Task<ConversationSyncHistoryResult> ConversationSyncHistoryLoader(
    ImsgChat chat,
    int requestedLimit,
    CancellationToken cancellationToken);

public sealed record ConversationSyncHistoryResult(IReadOnlyList<ImsgMessage> Messages, int FetchedLimit, bool IsPartial);

public sealed record ConversationSyncedChat(ImsgChat Chat, IReadOnlyList<ImsgMessage> Messages, int FetchedLimit, bool IsPartial);
