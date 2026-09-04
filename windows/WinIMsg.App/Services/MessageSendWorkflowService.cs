using WinIMsg.App.Contracts;
using WinIMsg.App.ViewModels;
using WinIMsg.Core.Models;
using WinIMsg.Core.Ssh;
using System.Globalization;

namespace WinIMsg.App.Services;

public sealed class MessageSendWorkflowService(MessageSendService messageSendService)
{
    public const string SentSyncingDeliveryStatus = "Sent from Mac; syncing local history.";
    public const string SendUnconfirmedDeliveryStatus = "Send unconfirmed; not found in Messages history. Refresh before retrying.";

    public async Task<MessageSendWorkflowOutcome> SendTextAsync(
        ConversationViewModel conversation,
        ChatListItem chat,
        string text,
        bool pollSendStatus,
        TimeSpan requestTimeout,
        Func<ChatListItem, bool> isSelectedChat,
        Func<ChatListItem, CancellationToken, Task> refreshHistoryAsync,
        Action? selectedConversationChanged = null,
        ImsgMessage? pendingMessage = null,
        bool refreshAfterSend = true,
        CancellationToken cancellationToken = default)
    {
        MessageListItem? pendingItem = null;
        if (isSelectedChat(chat))
        {
            pendingItem = conversation.AddOrGetPendingMessage(pendingMessage ?? CreatePendingMessage(chat, text));
            selectedConversationChanged?.Invoke();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);

        try
        {
            var sendStartedAt = DateTimeOffset.UtcNow;
            var outgoingService = OutgoingServiceForChat(chat.Chat.Service);
            var target = ConversationTarget.FromChat(chat.Chat, chat.DisplayName);
            var workflowResult = await messageSendService.SendTextWithStatusAsync(
                target,
                text,
                outgoingService,
                pollSendStatus,
                timeout.Token);
            var sendResult = workflowResult.SendResult;
            var sentGuid = sendResult.MessageGuid;
            if (!string.IsNullOrWhiteSpace(sentGuid) && isSelectedChat(chat) && pendingItem is not null)
            {
                conversation.MarkPendingMessageSent(pendingItem, sentGuid, SentSyncingDeliveryStatus);
            }

            var chatStillSelected = isSelectedChat(chat);
            var observedInHistory = false;
            if (refreshAfterSend)
            {
                await refreshHistoryAsync(chat, timeout.Token);
                chatStillSelected = isSelectedChat(chat);
                observedInHistory = chatStillSelected && conversation.HasObservedSentMessage(text, sentGuid, sendStartedAt);
            }

            var sendUnconfirmed = string.IsNullOrWhiteSpace(sentGuid) && !observedInHistory;

            if (sendUnconfirmed)
            {
                if (chatStillSelected && pendingItem is not null)
                {
                    conversation.AddPostSendPlaceholder(
                        pendingItem,
                        pendingItem.Message.Guid,
                        SendUnconfirmedDeliveryStatus,
                        isFailed: true);
                    selectedConversationChanged?.Invoke();
                }

                return new MessageSendWorkflowOutcome(
                    SentGuid: sentGuid,
                    OutgoingService: outgoingService,
                    IsUnconfirmed: true,
                    IsFailed: false,
                    StatusMessage: "Send unconfirmed. Open Settings > Diagnostics for logs.",
                    ErrorMessage: null,
                    SendStatusSummary: workflowResult.SendStatusSummary,
                    SendStatusError: workflowResult.SendStatusError);
            }

            if (!string.IsNullOrWhiteSpace(sentGuid) && !observedInHistory && chatStillSelected && pendingItem is not null)
            {
                conversation.AddPostSendPlaceholder(
                    pendingItem,
                    sentGuid,
                    SentSyncingDeliveryStatus,
                    isFailed: false);
                selectedConversationChanged?.Invoke();
            }

            if (!string.IsNullOrWhiteSpace(sentGuid) &&
                !string.IsNullOrWhiteSpace(workflowResult.SendStatusSummary) &&
                isSelectedChat(chat))
            {
                conversation.UpdateMessageDeliveryStatus(sentGuid, workflowResult.SendStatusSummary);
            }

            return new MessageSendWorkflowOutcome(
                SentGuid: sentGuid,
                OutgoingService: outgoingService,
                IsUnconfirmed: false,
                IsFailed: false,
                StatusMessage: null,
                ErrorMessage: null,
                SendStatusSummary: workflowResult.SendStatusSummary,
                SendStatusError: workflowResult.SendStatusError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var sendError = MessageSendService.BuildSendFailureMessage(ex, requestTimeout);
            if (isSelectedChat(chat) && pendingItem is not null)
            {
                conversation.MarkPendingMessageFailed(pendingItem, sendError);
                selectedConversationChanged?.Invoke();
            }

            return new MessageSendWorkflowOutcome(
                SentGuid: null,
                OutgoingService: OutgoingServiceForChat(chat.Chat.Service),
                IsUnconfirmed: false,
                IsFailed: true,
                StatusMessage: "Send failed. Open Settings > Diagnostics for logs.",
                ErrorMessage: sendError,
                SendStatusSummary: null,
                SendStatusError: null);
        }
    }

    public static string OutgoingServiceForChat(string? service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return "auto";
        }

        if (service.Contains("sms", StringComparison.OrdinalIgnoreCase) ||
            service.Contains("rcs", StringComparison.OrdinalIgnoreCase) ||
            service.Contains("text message", StringComparison.OrdinalIgnoreCase))
        {
            return "sms";
        }

        return service.Contains("imessage", StringComparison.OrdinalIgnoreCase)
            ? "imessage"
            : "auto";
    }

    public static ImsgMessage CreatePendingMessage(ChatListItem chat, string text)
    {
        return new ImsgMessage
        {
            Guid = $"pending:{Guid.NewGuid():N}",
            ChatId = chat.Chat.Id,
            ChatIdentifier = chat.Chat.Identifier,
            ChatGuid = chat.Chat.Guid,
            ChatName = chat.DisplayName,
            Text = text,
            Service = chat.Chat.Service,
            IsFromMe = true,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };
    }
}

public sealed record MessageSendWorkflowOutcome(
    string? SentGuid,
    string OutgoingService,
    bool IsUnconfirmed,
    bool IsFailed,
    string? StatusMessage,
    string? ErrorMessage,
    string? SendStatusSummary,
    Exception? SendStatusError);

public sealed class AttachmentSendWorkflowService(
    IAttachmentUploadService attachmentUploadService,
    MessageSendService messageSendService)
{
    public async Task<AttachmentBatchSendResult> SendAsync(
        ImsgBridgeSettings settings,
        ChatListItem chat,
        IReadOnlyList<string> filePaths,
        string? text,
        bool supportsAdvancedAttachmentFallback,
        IProgress<AttachmentSendProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = new List<AttachmentFileSendResult>();
        var target = ConversationTarget.FromChat(chat.Chat, chat.DisplayName);
        var outgoingService = MessageSendWorkflowService.OutgoingServiceForChat(chat.Chat.Service);
        var captionText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        var captionPending = !string.IsNullOrWhiteSpace(captionText);
        var textSentWithoutAttachment = false;
        string? textFallbackError = null;

        foreach (var filePath in filePaths)
        {
            var caption = captionPending ? captionText : null;
            var displayName = Path.GetFileName(filePath);
            string? remotePath = null;

            try
            {
                progress?.Report(AttachmentSendProgress.Staging(displayName));
                remotePath = await attachmentUploadService.UploadAsync(
                    settings,
                    filePath,
                    new Progress<FileTransferProgress>(transfer => progress?.Report(AttachmentSendProgress.Uploading(displayName, transfer))),
                    cancellationToken);
                progress?.Report(AttachmentSendProgress.Uploaded(displayName));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var failureStage = ClassifyUploadFailure(ex);
                files.Add(AttachmentFileSendResult.Failed(
                    filePath,
                    displayName,
                    remotePath,
                    failureStage,
                    BuildAttachmentFailureMessage(failureStage, displayName, ex, settings.RequestTimeout)));

                if (!string.IsNullOrWhiteSpace(caption) && !textSentWithoutAttachment)
                {
                    try
                    {
                        await messageSendService.SendTextAsync(target, caption, outgoingService, cancellationToken);
                        textSentWithoutAttachment = true;
                    }
                    catch (Exception textEx) when (textEx is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        textFallbackError = MessageSendService.BuildSendFailureMessage(textEx, settings.RequestTimeout);
                    }
                    finally
                    {
                        captionPending = false;
                    }
                }

                continue;
            }

            var presentationKind = AttachmentPresentationPolicy.ClassifyLocalFile(filePath);
            try
            {
                progress?.Report(AttachmentSendProgress.Sending(displayName));
                await SendAttachmentMessageAsync(
                    target,
                    remotePath,
                    caption,
                    outgoingService,
                    presentationKind,
                    supportsAdvancedAttachmentFallback,
                    cancellationToken);

                files.Add(AttachmentFileSendResult.Success(
                    filePath,
                    displayName,
                    remotePath,
                    captionIncluded: !string.IsNullOrWhiteSpace(caption)));
                await CleanupUploadedAttachmentAsync(settings, remotePath, cancellationToken);

                if (!string.IsNullOrWhiteSpace(caption))
                {
                    captionPending = false;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var failureStage = ClassifyRemoteSendFailure(ex);
                var message = BuildAttachmentFailureMessage(failureStage, displayName, ex, settings.RequestTimeout);
                files.Add(failureStage == AttachmentSendFailureStage.RemoteSendTimeout
                    ? AttachmentFileSendResult.Unconfirmed(filePath, displayName, remotePath, message)
                    : AttachmentFileSendResult.Failed(filePath, displayName, remotePath, failureStage, message));

                if (!string.IsNullOrWhiteSpace(caption))
                {
                    captionPending = false;
                }
            }
        }

        return new AttachmentBatchSendResult(
            files,
            TextWasPresent: !string.IsNullOrWhiteSpace(captionText),
            TextSentWithoutAttachment: textSentWithoutAttachment,
            TextFallbackError: textFallbackError);
    }

    private async Task CleanupUploadedAttachmentAsync(
        ImsgBridgeSettings settings,
        string remotePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await attachmentUploadService.DeleteUploadedAsync(settings, remotePath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A confirmed send should not be turned into a failed send because temp cleanup failed.
        }
    }

    private async Task SendAttachmentMessageAsync(
        ConversationTarget target,
        string remotePath,
        string? text,
        string outgoingService,
        AttachmentPresentationKind presentationKind,
        bool supportsAdvancedAttachmentFallback,
        CancellationToken cancellationToken)
    {
        try
        {
            await messageSendService.SendFileAsync(
                target,
                remotePath,
                text,
                outgoingService,
                cancellationToken);
        }
        catch (Exception ex) when (CanFallbackToAdvancedAttachmentSend(ex, text, supportsAdvancedAttachmentFallback))
        {
            await messageSendService.SendAttachmentAsync(
                target,
                remotePath,
                audio: presentationKind == AttachmentPresentationKind.Audio,
                replyTo: null,
                cancellationToken);
        }
    }

    private static bool CanFallbackToAdvancedAttachmentSend(
        Exception exception,
        string? text,
        bool supportsAdvancedAttachmentFallback)
    {
        return string.IsNullOrWhiteSpace(text) &&
            supportsAdvancedAttachmentFallback &&
            LooksLikeUnsupportedFileSend(exception);
    }

    private static bool LooksLikeUnsupportedFileSend(Exception exception)
    {
        var message = exception.Message;
        return message.Contains("file", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unexpected", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("invalid parameter", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("missing parameter", StringComparison.OrdinalIgnoreCase));
    }

    private static AttachmentSendFailureStage ClassifyUploadFailure(Exception exception)
    {
        return exception switch
        {
            FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or PathTooLongException or NotSupportedException =>
                AttachmentSendFailureStage.Staging,
            _ => AttachmentSendFailureStage.Upload
        };
    }

    private static AttachmentSendFailureStage ClassifyRemoteSendFailure(Exception exception)
    {
        if (IsTargetMismatch(exception))
        {
            return AttachmentSendFailureStage.TargetMismatch;
        }

        if (exception is TimeoutException or OperationCanceledException)
        {
            return AttachmentSendFailureStage.RemoteSendTimeout;
        }

        return LooksLikeUnsupportedFileSend(exception)
            ? AttachmentSendFailureStage.UnsupportedMethod
            : AttachmentSendFailureStage.RemoteSend;
    }

    private static bool IsTargetMismatch(Exception exception) =>
        exception is ImsgSendResultException &&
        exception.Message.StartsWith("Target mismatch:", StringComparison.OrdinalIgnoreCase);

    private static string BuildAttachmentFailureMessage(
        AttachmentSendFailureStage stage,
        string displayName,
        Exception exception,
        TimeSpan requestTimeout)
    {
        var detail = stage is AttachmentSendFailureStage.RemoteSend or
            AttachmentSendFailureStage.UnsupportedMethod or
            AttachmentSendFailureStage.TargetMismatch
                ? MessageSendService.BuildSendFailureMessage(exception, requestTimeout)
                : exception.Message;

        return stage switch
        {
            AttachmentSendFailureStage.Staging =>
                $"Attachment staging failed for {displayName}: {detail}",
            AttachmentSendFailureStage.Upload =>
                $"Attachment upload to the Mac failed for {displayName}: {detail}",
            AttachmentSendFailureStage.UnsupportedMethod =>
                $"Unsupported attachment send method for {displayName}: {detail}",
            AttachmentSendFailureStage.TargetMismatch =>
                $"Attachment target mismatch for {displayName}: {detail}",
            AttachmentSendFailureStage.RemoteSendTimeout =>
                $"Mac send did not confirm {displayName} within {requestTimeout.TotalSeconds:0} seconds. It may still finish on the Mac; refresh before retrying so a delayed send is not duplicated.",
            _ =>
                $"Mac attachment send failed for {displayName}: {detail}"
        };
    }
}

public sealed record AttachmentBatchSendResult(
    IReadOnlyList<AttachmentFileSendResult> Files,
    bool TextWasPresent,
    bool TextSentWithoutAttachment,
    string? TextFallbackError)
{
    public int AttemptedAttachments => Files.Count;

    public int SentAttachments => Files.Count(file => file.Sent);

    public int UnconfirmedAttachments => Files.Count(file => file.IsUnconfirmed);

    public int FailedAttachments => Files.Count(file => !file.Sent && !file.IsUnconfirmed);

    public bool TextSentWithAttachment => Files.Any(file => file.Sent && file.CaptionIncluded);

    public bool HasFailures => FailedAttachments > 0 || !string.IsNullOrWhiteSpace(TextFallbackError);

    public bool HasUnconfirmedRemoteSends => UnconfirmedAttachments > 0;

    public bool AnyRemoteChangeLikely =>
        SentAttachments > 0 ||
        TextSentWithoutAttachment ||
        Files.Any(file => file.FailureStage is AttachmentSendFailureStage.RemoteSend or AttachmentSendFailureStage.RemoteSendTimeout);

    public string StatusMessage
    {
        get
        {
            if (HasUnconfirmedRemoteSends && !HasFailures)
            {
                return UnconfirmedAttachments == 1
                    ? "Attachment send is still pending on the Mac; refresh before retrying."
                    : $"{UnconfirmedAttachments} attachment sends are still pending on the Mac; refresh before retrying.";
            }

            if (!HasFailures)
            {
                if (TextSentWithAttachment)
                {
                    return SentAttachments == 1
                        ? "Sent message with attachment."
                        : $"Sent message with {SentAttachments} attachments.";
                }

                return SentAttachments == 1
                    ? "Sent attachment."
                    : $"Sent {SentAttachments} attachments.";
            }

            if (SentAttachments > 0)
            {
                if (HasUnconfirmedRemoteSends)
                {
                    return TextSentWithoutAttachment
                        ? $"Sent text and {SentAttachments} attachment{Plural(SentAttachments)}; {UnconfirmedAttachments} attachment{Plural(UnconfirmedAttachments)} still pending on the Mac."
                        : $"Sent {SentAttachments} attachment{Plural(SentAttachments)}; {UnconfirmedAttachments} attachment{Plural(UnconfirmedAttachments)} still pending on the Mac.";
                }

                return TextSentWithoutAttachment
                    ? $"Sent text and {SentAttachments} attachment{Plural(SentAttachments)}; {FailedAttachments} attachment{Plural(FailedAttachments)} failed."
                    : $"Sent {SentAttachments} attachment{Plural(SentAttachments)}; {FailedAttachments} attachment{Plural(FailedAttachments)} failed.";
            }

            if (TextSentWithoutAttachment)
            {
                return FailedAttachments == 1
                    ? "Attachment transfer failed; sent the text without the attachment."
                    : "Attachment transfers failed; sent the text without the attachments.";
            }

            if (!string.IsNullOrWhiteSpace(TextFallbackError))
            {
                return $"Attachment transfer failed, and the text fallback also failed: {TextFallbackError}";
            }

            var firstError = Files.FirstOrDefault(file => !file.Sent)?.ErrorMessage;
            return string.IsNullOrWhiteSpace(firstError)
                ? "Attachment send failed."
                : $"Attachment send failed: {firstError}";
        }
    }

    private static string Plural(int count) => count == 1 ? string.Empty : "s";
}

public sealed record AttachmentFileSendResult(
    string LocalPath,
    string DisplayName,
    string? RemotePath,
    bool Sent,
    bool IsUnconfirmed,
    bool CaptionIncluded,
    AttachmentSendFailureStage? FailureStage,
    string? ErrorMessage)
{
    public static AttachmentFileSendResult Success(
        string localPath,
        string displayName,
        string remotePath,
        bool captionIncluded) =>
        new(localPath, displayName, remotePath, Sent: true, IsUnconfirmed: false, captionIncluded, FailureStage: null, ErrorMessage: null);

    public static AttachmentFileSendResult Unconfirmed(
        string localPath,
        string displayName,
        string? remotePath,
        string message) =>
        new(localPath, displayName, remotePath, Sent: false, IsUnconfirmed: true, CaptionIncluded: false, AttachmentSendFailureStage.RemoteSendTimeout, message);

    public static AttachmentFileSendResult Failed(
        string localPath,
        string displayName,
        string? remotePath,
        AttachmentSendFailureStage failureStage,
        string errorMessage) =>
        new(localPath, displayName, remotePath, Sent: false, IsUnconfirmed: false, CaptionIncluded: false, failureStage, errorMessage);
}

public enum AttachmentSendFailureStage
{
    Staging,
    Upload,
    UnsupportedMethod,
    TargetMismatch,
    RemoteSendTimeout,
    RemoteSend
}

public sealed record AttachmentSendProgress(string DisplayName, string Phase, long? BytesTransferred = null, long? TotalBytes = null)
{
    public string StatusText
    {
        get
        {
            if (BytesTransferred is { } transferred && TotalBytes is { } total && total > 0)
            {
                var percent = Math.Clamp((double)transferred / total, 0, 1) * 100;
                return $"{Phase} {DisplayName} ({percent:0}%)...";
            }

            return $"{Phase} {DisplayName}...";
        }
    }

    public static AttachmentSendProgress Staging(string displayName) => new(displayName, "Staging");

    public static AttachmentSendProgress Uploading(string displayName, FileTransferProgress progress) =>
        new(displayName, "Uploading", progress.BytesTransferred, progress.TotalBytes);

    public static AttachmentSendProgress Uploaded(string displayName) => new(displayName, "Uploaded");

    public static AttachmentSendProgress Sending(string displayName) => new(displayName, "Waiting for Mac to send");
}
