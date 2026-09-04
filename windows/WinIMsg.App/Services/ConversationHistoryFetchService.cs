using WinIMsg.App.Contracts;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public sealed record SelectedHistoryFetchOptions(
    TimeSpan PrimaryTimeout,
    int FallbackLimit,
    TimeSpan FallbackTimeout);

public sealed record BackgroundHistoryFetchOptions(
    TimeSpan PrimaryTimeout,
    int FallbackLimit,
    TimeSpan FallbackTimeout,
    int LatestFallbackLimit,
    TimeSpan LatestFallbackTimeout);

public sealed record SelectedHistoryFetchResult(
    ConversationRemoteHistoryResult History,
    IReadOnlyList<string> Warnings);

public sealed record BackgroundHistoryFetchResult(
    ConversationSyncHistoryResult History,
    IReadOnlyList<string> Warnings);

public sealed class ConversationHistoryFetchService(IImsgClient client)
{
    public async Task<SelectedHistoryFetchResult> FetchSelectedAsync(
        long chatId,
        int requestedLimit,
        SelectedHistoryFetchOptions options,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        try
        {
            var messages = await GetHistorySliceAsync(
                chatId,
                requestedLimit,
                options.PrimaryTimeout,
                cancellationToken);
            return new SelectedHistoryFetchResult(
                new ConversationRemoteHistoryResult(messages, requestedLimit),
                warnings);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsTimeoutLike(ex))
        {
            warnings.Add($"Selected chat history timed out at {requestedLimit}; retrying recent slice.");
            var messages = await GetHistorySliceAsync(
                chatId,
                options.FallbackLimit,
                options.FallbackTimeout,
                cancellationToken);
            return new SelectedHistoryFetchResult(
                new ConversationRemoteHistoryResult(messages, options.FallbackLimit),
                warnings);
        }
    }

    public async Task<BackgroundHistoryFetchResult> FetchBackgroundSyncAsync(
        ImsgChat chat,
        int requestedLimit,
        BackgroundHistoryFetchOptions options,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        if (chat.Id is null)
        {
            return new BackgroundHistoryFetchResult(
                new ConversationSyncHistoryResult([], 0, IsPartial: false),
                warnings);
        }

        try
        {
            var messages = await GetHistorySliceAsync(
                chat.Id.Value,
                requestedLimit,
                options.PrimaryTimeout,
                cancellationToken);
            return new BackgroundHistoryFetchResult(
                new ConversationSyncHistoryResult(messages, requestedLimit, IsPartial: false),
                warnings);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsTimeoutLike(ex))
        {
            warnings.Add($"Cache sync history timed out at {requestedLimit}; retrying recent slice.");
            try
            {
                var messages = await GetHistorySliceAsync(
                    chat.Id.Value,
                    options.FallbackLimit,
                    options.FallbackTimeout,
                    cancellationToken);
                return new BackgroundHistoryFetchResult(
                    new ConversationSyncHistoryResult(messages, options.FallbackLimit, IsPartial: true),
                    warnings);
            }
            catch (Exception fallbackEx) when (!cancellationToken.IsCancellationRequested && IsTimeoutLike(fallbackEx))
            {
                warnings.Add($"Cache sync recent slice timed out at {options.FallbackLimit}; retrying latest message only.");
                var messages = await GetHistorySliceAsync(
                    chat.Id.Value,
                    options.LatestFallbackLimit,
                    options.LatestFallbackTimeout,
                    cancellationToken);
                return new BackgroundHistoryFetchResult(
                    new ConversationSyncHistoryResult(messages, options.LatestFallbackLimit, IsPartial: true),
                    warnings);
            }
        }
    }

    private async Task<IReadOnlyList<ImsgMessage>> GetHistorySliceAsync(
        long chatId,
        int limit,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linkedTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedTimeout.CancelAfter(timeout);
        return await client.GetHistoryAsync(
            chatId,
            limit,
            includeAttachments: true,
            convertAttachments: true,
            includeReactions: true,
            linkedTimeout.Token);
    }

    private static bool IsTimeoutLike(Exception exception)
    {
        return exception is TimeoutException or OperationCanceledException;
    }
}
