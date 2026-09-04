using System.Text.Json;
using WinIMsg.Core.Bridge;
using WinIMsg.Core.Models;
using WinIMsg.Core.Rpc;
using WinIMsg.App.Contracts;

namespace WinIMsg.App.Services;

/// <summary>
/// Routes a category of RPC traffic over its own SSH + imsg rpc process.
/// The imsg rpc server handles requests strictly in order, so a slow call
/// (bridge-backed read, a large history fetch) blocks everything queued
/// behind it on the same session. Giving sends, message actions, and
/// background niceties their own channels keeps user-visible sends as fast
/// as a direct imsg send.
///
/// The channel connects lazily on first use, reconnects transparently after
/// its process dies, and reports <see cref="IsConnected"/> from the main
/// bridge so capability/connection gating stays a single source of truth.
/// </summary>
public sealed class DedicatedBridgeChannel(
    string name,
    IImsgClient mainClient,
    Func<ImsgBridgeSettings> settingsProvider,
    Action<string>? logInfo = null,
    Action<string>? logWarning = null) : IImsgClient
{
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private BridgeImsgClientAdapter? _channel;

    // Channels never carry watch traffic, so these are interface-compat only.
    public event EventHandler<JsonRpcNotification>? NotificationReceived
    {
        add { }
        remove { }
    }

    public event EventHandler<JsonRpcConnectionClosedEventArgs>? ConnectionClosed
    {
        add { }
        remove { }
    }

    public bool IsConnected => mainClient.IsConnected;

    public Task<ImsgProbeResult> ProbeAsync(ImsgBridgeSettings settings, CancellationToken cancellationToken = default) =>
        mainClient.ProbeAsync(settings, cancellationToken);

    // The main connection lifecycle owns real connects; a channel connect just
    // clears any stale session so the next call dials fresh settings.
    public Task ConnectAsync(ImsgBridgeSettings settings, CancellationToken cancellationToken = default) => ResetAsync();

    public Task DisconnectAsync() => ResetAsync();

    public async Task ResetAsync()
    {
        BridgeImsgClientAdapter? channel;
        await _connectGate.WaitAsync();
        try
        {
            channel = _channel;
            _channel = null;
        }
        finally
        {
            _connectGate.Release();
        }

        if (channel is not null)
        {
            try
            {
                await channel.DisconnectAsync();
            }
            catch
            {
                // Best-effort teardown of a possibly-dead session.
            }
        }
    }

    public async ValueTask DisposeAsync() => await ResetAsync();

    /// <summary>Dials the channel session ahead of first use.</summary>
    public Task WarmUpAsync(CancellationToken cancellationToken = default) => ChannelAsync(cancellationToken);

    private async Task<IImsgClient> ChannelAsync(CancellationToken cancellationToken)
    {
        var existing = _channel;
        if (existing is { IsConnected: true })
        {
            return existing;
        }

        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsConnected: true } connected)
            {
                return connected;
            }

            if (_channel is not null)
            {
                try
                {
                    await _channel.DisconnectAsync();
                }
                catch
                {
                }

                _channel = null;
                logWarning?.Invoke($"Bridge channel '{name}' session ended; reconnecting.");
            }

            var channel = new BridgeImsgClientAdapter();
            try
            {
                await channel.ConnectAsync(settingsProvider(), cancellationToken);
            }
            catch
            {
                try
                {
                    await channel.DisconnectAsync();
                }
                catch
                {
                }

                throw;
            }

            _channel = channel;
            logInfo?.Invoke($"Bridge channel '{name}' connected.");
            return channel;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task<IReadOnlyList<ImsgChat>> ListChatsAsync(int limit = 10000, CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).ListChatsAsync(limit, cancellationToken);

    public async Task<IReadOnlyList<ImsgMessage>> GetHistoryAsync(
        long chatId,
        int limit = 100,
        bool includeAttachments = true,
        bool convertAttachments = false,
        bool includeReactions = true,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).GetHistoryAsync(chatId, limit, includeAttachments, convertAttachments, includeReactions, cancellationToken);

    public Task<JsonElement> SubscribeAsync(
        long? chatId = null,
        long? sinceRowId = null,
        bool includeAttachments = true,
        bool includeReactions = true,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Watch subscriptions belong to the main bridge session, not a dedicated channel.");

    public async Task<JsonElement> SendTextAsync(
        long? chatId,
        string? chatIdentifier,
        string text,
        string transport = "auto",
        string? service = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).SendTextAsync(chatId, chatIdentifier, text, transport, service, chatGuid, cancellationToken);

    public async Task<JsonElement> SendDirectTextAsync(
        string recipient,
        string text,
        string transport = "auto",
        string? service = null,
        string? region = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).SendDirectTextAsync(recipient, text, transport, service, region, cancellationToken);

    public async Task<JsonElement> SendFileAsync(
        long? chatId,
        string? chatIdentifier,
        string remoteFilePath,
        string? text = null,
        string transport = "auto",
        string? service = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).SendFileAsync(chatId, chatIdentifier, remoteFilePath, text, transport, service, chatGuid, cancellationToken);

    public async Task<JsonElement> CreateChatAsync(
        IReadOnlyList<string> addresses,
        string? name = null,
        string? text = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).CreateChatAsync(addresses, name, text, cancellationToken);

    public async Task<JsonElement> DeleteChatAsync(
        long? chatId,
        string? chatIdentifier,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).DeleteChatAsync(chatId, chatIdentifier, chatGuid, cancellationToken);

    public async Task<JsonElement> MarkUnreadAsync(
        long? chatId,
        string? chatIdentifier,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).MarkUnreadAsync(chatId, chatIdentifier, chatGuid, cancellationToken);

    public async Task<JsonElement> SendAttachmentAsync(
        long? chatId,
        string? chatIdentifier,
        string remoteFilePath,
        bool audio,
        string? replyTo,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).SendAttachmentAsync(chatId, chatIdentifier, remoteFilePath, audio, replyTo, chatGuid, cancellationToken);

    public async Task<JsonElement> SendPollAsync(
        long? chatId,
        string? chatIdentifier,
        string question,
        IReadOnlyList<string> options,
        string? replyTo = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).SendPollAsync(chatId, chatIdentifier, question, options, replyTo, chatGuid, cancellationToken);

    public async Task<JsonElement> SendRichAsync(
        long? chatId,
        string? chatIdentifier,
        string text,
        string? effect = null,
        string? replyTo = null,
        IReadOnlyList<RichTextFormattingRange>? textFormatting = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).SendRichAsync(chatId, chatIdentifier, text, effect, replyTo, textFormatting, chatGuid, cancellationToken);

    public async Task<JsonElement> SetTypingAsync(
        long? chatId,
        string? chatIdentifier,
        bool typing,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).SetTypingAsync(chatId, chatIdentifier, typing, chatGuid, cancellationToken);

    public async Task<JsonElement> TapbackAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string reaction,
        bool remove,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).TapbackAsync(chatId, chatIdentifier, messageGuid, reaction, remove, chatGuid, cancellationToken);

    public async Task<JsonElement> MarkReadAsync(
        long? chatId,
        string? chatIdentifier,
        string? messageGuid = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).MarkReadAsync(chatId, chatIdentifier, messageGuid, chatGuid, cancellationToken);

    public async Task<JsonElement> EditMessageAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string text,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).EditMessageAsync(chatId, chatIdentifier, messageGuid, text, chatGuid, cancellationToken);

    public async Task<JsonElement> UnsendMessageAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).UnsendMessageAsync(chatId, chatIdentifier, messageGuid, chatGuid, cancellationToken);

    public async Task<JsonElement> DeleteMessageAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).DeleteMessageAsync(chatId, chatIdentifier, messageGuid, chatGuid, cancellationToken);

    public async Task<JsonElement> NotifyAnywaysAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).NotifyAnywaysAsync(chatId, chatIdentifier, messageGuid, chatGuid, cancellationToken);

    public async Task<JsonElement> GetSendStatusAsync(string messageGuid, CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).GetSendStatusAsync(messageGuid, cancellationToken);

    public async Task<JsonElement> RenameGroupAsync(string chatGuid, string groupName, CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).RenameGroupAsync(chatGuid, groupName, cancellationToken);

    public async Task<JsonElement> SetGroupIconAsync(string chatGuid, string? remoteFilePath = null, CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).SetGroupIconAsync(chatGuid, remoteFilePath, cancellationToken);

    public async Task<JsonElement> AddParticipantAsync(string chatGuid, string address, CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).AddParticipantAsync(chatGuid, address, cancellationToken);

    public async Task<JsonElement> RemoveParticipantAsync(string chatGuid, string address, CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).RemoveParticipantAsync(chatGuid, address, cancellationToken);

    public async Task<JsonElement> LeaveGroupAsync(string chatGuid, CancellationToken cancellationToken = default) =>
        await (await ChannelAsync(cancellationToken)).LeaveGroupAsync(chatGuid, cancellationToken);
}
