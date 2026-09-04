using System.Text.Json;
using WinIMsg.Core.Bridge;
using WinIMsg.Core.Models;
using WinIMsg.Core.Rpc;

namespace WinIMsg.App.Contracts;

public interface IImsgClient : IAsyncDisposable
{
    event EventHandler<JsonRpcNotification>? NotificationReceived;

    event EventHandler<JsonRpcConnectionClosedEventArgs>? ConnectionClosed;

    bool IsConnected { get; }

    Task<ImsgProbeResult> ProbeAsync(ImsgBridgeSettings settings, CancellationToken cancellationToken = default);

    Task ConnectAsync(ImsgBridgeSettings settings, CancellationToken cancellationToken = default);

    Task DisconnectAsync();

    Task<IReadOnlyList<ImsgChat>> ListChatsAsync(int limit = 10000, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImsgMessage>> GetHistoryAsync(
        long chatId,
        int limit = 100,
        bool includeAttachments = true,
        bool convertAttachments = false,
        bool includeReactions = true,
        CancellationToken cancellationToken = default);

    Task<JsonElement> SubscribeAsync(
        long? chatId = null,
        long? sinceRowId = null,
        bool includeAttachments = true,
        bool includeReactions = true,
        CancellationToken cancellationToken = default);

    Task<JsonElement> SendTextAsync(
        long? chatId,
        string? chatIdentifier,
        string text,
        string transport = "auto",
        string? service = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> SendDirectTextAsync(
        string recipient,
        string text,
        string transport = "auto",
        string? service = null,
        string? region = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> SendFileAsync(
        long? chatId,
        string? chatIdentifier,
        string remoteFilePath,
        string? text = null,
        string transport = "auto",
        string? service = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> CreateChatAsync(
        IReadOnlyList<string> addresses,
        string? name = null,
        string? text = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> DeleteChatAsync(
        long? chatId,
        string? chatIdentifier,
        string? chatGuid = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> MarkUnreadAsync(
        long? chatId,
        string? chatIdentifier,
        string? chatGuid = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> SendAttachmentAsync(
        long? chatId,
        string? chatIdentifier,
        string remoteFilePath,
        bool audio,
        string? replyTo,
        string? chatGuid = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> SendPollAsync(
        long? chatId,
        string? chatIdentifier,
        string question,
        IReadOnlyList<string> options,
        string? replyTo = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> SendRichAsync(
        long? chatId,
        string? chatIdentifier,
        string text,
        string? effect = null,
        string? replyTo = null,
        IReadOnlyList<RichTextFormattingRange>? textFormatting = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> SetTypingAsync(
        long? chatId,
        string? chatIdentifier,
        bool typing,
        string? chatGuid = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> TapbackAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string reaction,
        bool remove,
        string? chatGuid = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> MarkReadAsync(
        long? chatId,
        string? chatIdentifier,
        string? messageGuid = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> EditMessageAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string text,
        string? chatGuid = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> UnsendMessageAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string? chatGuid = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> DeleteMessageAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string? chatGuid = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> NotifyAnywaysAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string? chatGuid = null,
        CancellationToken cancellationToken = default);

    Task<JsonElement> GetSendStatusAsync(string messageGuid, CancellationToken cancellationToken = default);

    Task<JsonElement> RenameGroupAsync(string chatGuid, string name, CancellationToken cancellationToken = default);

    Task<JsonElement> SetGroupIconAsync(string chatGuid, string? remoteFilePath = null, CancellationToken cancellationToken = default);

    Task<JsonElement> AddParticipantAsync(string chatGuid, string address, CancellationToken cancellationToken = default);

    Task<JsonElement> RemoveParticipantAsync(string chatGuid, string address, CancellationToken cancellationToken = default);

    Task<JsonElement> LeaveGroupAsync(string chatGuid, CancellationToken cancellationToken = default);
}
