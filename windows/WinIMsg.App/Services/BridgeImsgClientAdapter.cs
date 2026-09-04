using System.Text.Json;
using WinIMsg.App.Contracts;
using WinIMsg.Core.Bridge;
using WinIMsg.Core.Models;
using WinIMsg.Core.Rpc;

namespace WinIMsg.App.Services;

public sealed class BridgeImsgClientAdapter : IImsgClient
{
    private readonly ImsgBridgeClient _inner;

    public BridgeImsgClientAdapter(ImsgBridgeClient? inner = null)
    {
        _inner = inner ?? new ImsgBridgeClient();
        _inner.NotificationReceived += (_, notification) => NotificationReceived?.Invoke(this, notification);
        _inner.ConnectionClosed += (_, args) => ConnectionClosed?.Invoke(this, args);
    }

    public event EventHandler<JsonRpcNotification>? NotificationReceived;

    public event EventHandler<JsonRpcConnectionClosedEventArgs>? ConnectionClosed;

    public bool IsConnected => _inner.IsConnected;

    public Task<ImsgProbeResult> ProbeAsync(ImsgBridgeSettings settings, CancellationToken cancellationToken = default) =>
        _inner.ProbeAsync(settings, cancellationToken);

    public Task ConnectAsync(ImsgBridgeSettings settings, CancellationToken cancellationToken = default) =>
        _inner.ConnectAsync(settings, cancellationToken);

    public Task DisconnectAsync() => _inner.DisconnectAsync();

    public Task<IReadOnlyList<ImsgChat>> ListChatsAsync(int limit = 10000, CancellationToken cancellationToken = default) =>
        _inner.ListChatsAsync(limit, cancellationToken);

    public Task<IReadOnlyList<ImsgMessage>> GetHistoryAsync(
        long chatId,
        int limit = 100,
        bool includeAttachments = true,
        bool convertAttachments = false,
        bool includeReactions = true,
        CancellationToken cancellationToken = default) =>
        _inner.GetHistoryAsync(chatId, limit, includeAttachments, convertAttachments, includeReactions, cancellationToken);

    public Task<JsonElement> SubscribeAsync(
        long? chatId = null,
        long? sinceRowId = null,
        bool includeAttachments = true,
        bool includeReactions = true,
        CancellationToken cancellationToken = default) =>
        _inner.SubscribeAsync(chatId, sinceRowId, includeAttachments, includeReactions, cancellationToken);

    public Task<JsonElement> SendTextAsync(
        long? chatId,
        string? chatIdentifier,
        string text,
        string transport = "auto",
        string? service = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        _inner.SendTextAsync(chatId, chatIdentifier, text, transport, service, chatGuid, cancellationToken);

    public Task<JsonElement> SendDirectTextAsync(
        string recipient,
        string text,
        string transport = "auto",
        string? service = null,
        string? region = null,
        CancellationToken cancellationToken = default) =>
        _inner.SendDirectTextAsync(recipient, text, transport, service, region, cancellationToken);

    public Task<JsonElement> SendFileAsync(
        long? chatId,
        string? chatIdentifier,
        string remoteFilePath,
        string? text = null,
        string transport = "auto",
        string? service = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        _inner.SendFileAsync(chatId, chatIdentifier, remoteFilePath, text, transport, service, chatGuid, cancellationToken);

    public Task<JsonElement> CreateChatAsync(
        IReadOnlyList<string> addresses,
        string? name = null,
        string? text = null,
        CancellationToken cancellationToken = default) =>
        _inner.CreateChatAsync(addresses, name, text, cancellationToken);

    public Task<JsonElement> DeleteChatAsync(
        long? chatId,
        string? chatIdentifier,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        _inner.DeleteChatAsync(chatId, chatIdentifier, chatGuid, cancellationToken);

    public Task<JsonElement> MarkUnreadAsync(
        long? chatId,
        string? chatIdentifier,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        _inner.MarkUnreadAsync(chatId, chatIdentifier, chatGuid, cancellationToken);

    public Task<JsonElement> SendAttachmentAsync(
        long? chatId,
        string? chatIdentifier,
        string remoteFilePath,
        bool audio,
        string? replyTo,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        _inner.SendAttachmentAsync(chatId, chatIdentifier, remoteFilePath, audio, replyTo, chatGuid, cancellationToken);

    public Task<JsonElement> SendPollAsync(
        long? chatId,
        string? chatIdentifier,
        string question,
        IReadOnlyList<string> options,
        string? replyTo = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        _inner.SendPollAsync(chatId, chatIdentifier, question, options, replyTo, chatGuid, cancellationToken);

    public Task<JsonElement> SendRichAsync(
        long? chatId,
        string? chatIdentifier,
        string text,
        string? effect = null,
        string? replyTo = null,
        IReadOnlyList<RichTextFormattingRange>? textFormatting = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        _inner.SendRichAsync(chatId, chatIdentifier, text, effect, replyTo, textFormatting, chatGuid, cancellationToken);

    public Task<JsonElement> SetTypingAsync(
        long? chatId,
        string? chatIdentifier,
        bool typing,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        _inner.SetTypingAsync(chatId, chatIdentifier, typing, chatGuid, cancellationToken);

    public Task<JsonElement> TapbackAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string reaction,
        bool remove,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        _inner.TapbackAsync(chatId, chatIdentifier, messageGuid, reaction, remove, chatGuid, cancellationToken);

    public Task<JsonElement> MarkReadAsync(
        long? chatId,
        string? chatIdentifier,
        string? messageGuid = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        _inner.MarkReadAsync(chatId, chatIdentifier, messageGuid, chatGuid, cancellationToken);

    public Task<JsonElement> EditMessageAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string text,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        _inner.EditMessageAsync(chatId, chatIdentifier, messageGuid, text, chatGuid, cancellationToken);

    public Task<JsonElement> UnsendMessageAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        _inner.UnsendMessageAsync(chatId, chatIdentifier, messageGuid, chatGuid, cancellationToken);

    public Task<JsonElement> DeleteMessageAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        _inner.DeleteMessageAsync(chatId, chatIdentifier, messageGuid, chatGuid, cancellationToken);

    public Task<JsonElement> NotifyAnywaysAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string? chatGuid = null,
        CancellationToken cancellationToken = default) =>
        _inner.NotifyAnywaysAsync(chatId, chatIdentifier, messageGuid, chatGuid, cancellationToken);

    public Task<JsonElement> GetSendStatusAsync(string messageGuid, CancellationToken cancellationToken = default) =>
        _inner.GetSendStatusAsync(messageGuid, cancellationToken);

    public Task<JsonElement> RenameGroupAsync(string chatGuid, string name, CancellationToken cancellationToken = default) =>
        _inner.RenameGroupAsync(chatGuid, name, cancellationToken);

    public Task<JsonElement> SetGroupIconAsync(string chatGuid, string? remoteFilePath = null, CancellationToken cancellationToken = default) =>
        _inner.SetGroupIconAsync(chatGuid, remoteFilePath, cancellationToken);

    public Task<JsonElement> AddParticipantAsync(string chatGuid, string address, CancellationToken cancellationToken = default) =>
        _inner.AddParticipantAsync(chatGuid, address, cancellationToken);

    public Task<JsonElement> RemoveParticipantAsync(string chatGuid, string address, CancellationToken cancellationToken = default) =>
        _inner.RemoveParticipantAsync(chatGuid, address, cancellationToken);

    public Task<JsonElement> LeaveGroupAsync(string chatGuid, CancellationToken cancellationToken = default) =>
        _inner.LeaveGroupAsync(chatGuid, cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
