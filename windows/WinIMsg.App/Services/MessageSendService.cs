using System.Text.Json;
using WinIMsg.App.Contracts;
using WinIMsg.Core.Models;
using WinIMsg.Core.Rpc;

namespace WinIMsg.App.Services;

public sealed class MessageSendService(IImsgClient client)
{
    public async Task<MessageSendResult> SendTextAsync(
        ConversationTarget target,
        string text,
        string service,
        CancellationToken cancellationToken = default)
    {
        var result = await client.SendTextAsync(
            target.ChatId,
            target.ChatIdentifier,
            text,
            service: service,
            chatGuid: target.ChatGuid,
            cancellationToken: cancellationToken);

        ValidateSendResult(result);
        ValidateSendTarget(result, target);
        return new MessageSendResult(result, ReadSentMessageGuid(result));
    }

    public async Task<MessageSendResult> SendDirectTextAsync(
        string recipient,
        string text,
        string service,
        string? region = null,
        CancellationToken cancellationToken = default)
    {
        var result = await client.SendDirectTextAsync(
            recipient,
            text,
            service: service,
            region: region,
            cancellationToken: cancellationToken);

        ValidateSendResult(result);
        return new MessageSendResult(result, ReadSentMessageGuid(result));
    }

    public async Task<MessageSendResult> CreateChatAsync(
        IReadOnlyList<string> addresses,
        string? name,
        string text,
        CancellationToken cancellationToken = default)
    {
        var result = await client.CreateChatAsync(addresses, name, text, cancellationToken);
        ValidateSendResult(result);
        return new MessageSendResult(result, ReadSentMessageGuid(result));
    }

    public async Task<MessageSendResult> SendAttachmentAsync(
        ConversationTarget target,
        string remoteFilePath,
        bool audio,
        string? replyTo,
        CancellationToken cancellationToken = default)
    {
        var result = await client.SendAttachmentAsync(
            target.ChatId,
            target.ChatIdentifier,
            remoteFilePath,
            audio,
            replyTo,
            target.ChatGuid,
            cancellationToken);

        ValidateSendResult(result);
        ValidateSendTarget(result, target);
        return new MessageSendResult(result, ReadSentMessageGuid(result));
    }

    public async Task<MessageSendResult> SendPollAsync(
        ConversationTarget target,
        string question,
        IReadOnlyList<string> options,
        string? replyTo,
        CancellationToken cancellationToken = default)
    {
        var result = await client.SendPollAsync(
            target.ChatId,
            target.ChatIdentifier,
            question,
            options,
            replyTo,
            target.ChatGuid,
            cancellationToken);

        ValidateSendResult(result);
        ValidateSendTarget(result, target);
        return new MessageSendResult(result, ReadSentMessageGuid(result));
    }

    public async Task<MessageSendResult> SendRichAsync(
        ConversationTarget target,
        string text,
        string? effect,
        string? replyTo,
        IReadOnlyList<RichTextFormattingRange>? textFormatting = null,
        CancellationToken cancellationToken = default)
    {
        var result = await client.SendRichAsync(
            target.ChatId,
            target.ChatIdentifier,
            text,
            effect,
            replyTo,
            textFormatting,
            target.ChatGuid,
            cancellationToken);

        ValidateSendResult(result);
        ValidateSendTarget(result, target);
        return new MessageSendResult(result, ReadSentMessageGuid(result));
    }

    public async Task<MessageSendResult> SendFileAsync(
        ConversationTarget target,
        string remoteFilePath,
        string? text,
        string service,
        CancellationToken cancellationToken = default)
    {
        var result = await client.SendFileAsync(
            target.ChatId,
            target.ChatIdentifier,
            remoteFilePath,
            text,
            service: service,
            chatGuid: target.ChatGuid,
            cancellationToken: cancellationToken);

        ValidateSendResult(result);
        ValidateSendTarget(result, target);
        return new MessageSendResult(result, ReadSentMessageGuid(result));
    }

    public Task<JsonElement> GetSendStatusAsync(string messageGuid, CancellationToken cancellationToken = default) =>
        client.GetSendStatusAsync(messageGuid, cancellationToken);

    public async Task<MessageSendWorkflowResult> SendTextWithStatusAsync(
        ConversationTarget target,
        string text,
        string service,
        bool pollSendStatus,
        CancellationToken cancellationToken = default)
    {
        var sendResult = await SendTextAsync(target, text, service, cancellationToken);
        string? statusSummary = null;
        Exception? statusError = null;

        if (pollSendStatus && !string.IsNullOrWhiteSpace(sendResult.MessageGuid))
        {
            try
            {
                var status = await GetSendStatusAsync(sendResult.MessageGuid, cancellationToken);
                statusSummary = ReadSendStatusSummary(status);
                ValidateSendStatus(status);
            }
            catch (ImsgSendResultException)
            {
                throw;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                statusError = ex;
            }
        }

        return new MessageSendWorkflowResult(sendResult, statusSummary, statusError);
    }

    public async Task<MessageSendWorkflowResult> SendDirectTextWithStatusAsync(
        string recipient,
        string text,
        string service,
        bool pollSendStatus,
        string? region = null,
        CancellationToken cancellationToken = default)
    {
        var sendResult = await SendDirectTextAsync(recipient, text, service, region, cancellationToken);
        var status = await ReadStatusAfterSendAsync(sendResult, pollSendStatus, cancellationToken);
        return new MessageSendWorkflowResult(sendResult, status.Summary, status.Error);
    }

    public static string? ReadReturnedChatGuid(JsonElement result)
    {
        return result.ValueKind == JsonValueKind.Object &&
            TryReadString(result, "chat_guid", out var chatGuid)
                ? chatGuid
                : null;
    }

    public static string BuildSendFailureMessage(Exception exception, TimeSpan requestTimeout)
    {
        return exception switch
        {
            TimeoutException => $"Send timed out after {requestTimeout.TotalSeconds:0} seconds. Refresh this chat before retrying so a delayed send is not duplicated.",
            OperationCanceledException => $"Send timed out after {requestTimeout.TotalSeconds:0} seconds. Refresh this chat before retrying so a delayed send is not duplicated.",
            InvalidOperationException => "Send failed because the imsg RPC session is not connected. Reconnect the active Mac profile and retry.",
            ArgumentException => "Send failed because the selected chat does not include a chat id or identifier.",
            JsonRpcRemoteException remote => $"imsg send failed: {remote.Message}",
            ImsgSendResultException sendResult => sendResult.Message,
            _ => $"Send failed: {exception.Message}"
        };
    }

    public static void ValidateSendResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (TryReadString(result, "error", out var error))
        {
            throw new ImsgSendResultException($"imsg send failed: {error}");
        }

        var ok = ReadOptionalBoolean(result, "ok");
        if (ok is false)
        {
            throw new ImsgSendResultException("imsg send failed: imsg returned ok=false.");
        }

        var success = ReadOptionalBoolean(result, "success");
        if (success is false)
        {
            throw new ImsgSendResultException("imsg send failed: imsg returned success=false.");
        }
    }

    public static void ValidateSendTarget(JsonElement result, ConversationTarget target)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(target.ChatGuid) &&
            TryReadString(result, "chat_guid", out var actualChatGuid) &&
            !string.Equals(target.ChatGuid, actualChatGuid, StringComparison.OrdinalIgnoreCase))
        {
            throw TargetMismatch("chat_guid", target.ChatGuid, actualChatGuid);
        }

        if (target.ChatId is not null &&
            TryReadInt64(result, ["chat_id", "chatId"], out var actualChatId) &&
            target.ChatId.Value != actualChatId)
        {
            throw TargetMismatch("chat_id", target.ChatId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), actualChatId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(target.ChatIdentifier) &&
            TryReadString(result, ["chat_identifier", "chatIdentifier", "identifier"], out var actualChatIdentifier) &&
            !string.Equals(target.ChatIdentifier, actualChatIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            throw TargetMismatch("chat_identifier", target.ChatIdentifier, actualChatIdentifier);
        }
    }

    public static string? ReadSentMessageGuid(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in new[] { "guid", "message_guid", "message_id", "id" })
        {
            if (result.TryGetProperty(property, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    public static bool? ReadOptionalBoolean(JsonElement result, string propertyName)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    public static string? ReadSendStatusSummary(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.String)
        {
            return string.IsNullOrWhiteSpace(result.GetString())
                ? null
                : result.GetString();
        }

        if (result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var property in new[] { "send_state", "status", "state", "delivery_status", "message_status" })
        {
            if (TryReadString(result, property, out var value))
            {
                parts.Add(FormatSendStatusState(value));
                break;
            }
        }

        if (TryReadString(result, "error", out var error))
        {
            parts.Add(error);
        }
        else if (TryReadStatusFieldInt(result, "error", out var statusFieldError) && statusFieldError != 0)
        {
            parts.Add($"error {statusFieldError}");
        }

        AddFlagSummary(result, "sent", "sent", parts);
        AddFlagSummary(result, "delivered", "delivered", parts);
        AddFlagSummary(result, "read", "read", parts);

        return parts.Count == 0 ? null : string.Join(" - ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private async Task<(string? Summary, Exception? Error)> ReadStatusAfterSendAsync(
        MessageSendResult sendResult,
        bool pollSendStatus,
        CancellationToken cancellationToken)
    {
        string? statusSummary = null;
        Exception? statusError = null;

        if (pollSendStatus && !string.IsNullOrWhiteSpace(sendResult.MessageGuid))
        {
            try
            {
                var status = await GetSendStatusAsync(sendResult.MessageGuid, cancellationToken);
                statusSummary = ReadSendStatusSummary(status);
                ValidateSendStatus(status);
            }
            catch (ImsgSendResultException)
            {
                throw;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                statusError = ex;
            }
        }

        return (statusSummary, statusError);
    }

    public static void ValidateSendStatus(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.String)
        {
            var value = result.GetString();
            if (LooksLikeFailedStatus(value))
            {
                throw new ImsgSendResultException(BuildSendStatusFailureMessage(value, null));
            }

            return;
        }

        if (result.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string? state = null;
        foreach (var property in new[] { "send_state", "status", "state", "delivery_status", "message_status" })
        {
            if (TryReadString(result, property, out var value))
            {
                state = value;
                break;
            }
        }

        int? errorCode = null;
        if (TryReadStatusFieldInt(result, "error", out var statusFieldError))
        {
            errorCode = statusFieldError;
        }

        var failed =
            LooksLikeFailedStatus(state) ||
            ReadOptionalBoolean(result, "failed") is true ||
            ReadOptionalBoolean(result, "success") is false ||
            (TryReadStatusFieldBoolean(result, "is_finished", out var isFinished) &&
                isFinished &&
                errorCode.GetValueOrDefault() != 0);

        if (failed)
        {
            throw new ImsgSendResultException(BuildSendStatusFailureMessage(state, errorCode));
        }
    }

    private static string BuildSendStatusFailureMessage(string? state, int? errorCode)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(state))
        {
            details.Add($"send_state={state}");
        }

        if (errorCode is not null)
        {
            details.Add($"error={errorCode.Value}");
        }

        var suffix = details.Count > 0 ? $" ({string.Join(", ", details)})" : string.Empty;
        return $"iMessage send failed on the Mac{suffix}. Check that Messages is signed in on the Mac, then reconnect and retry.";
    }

    private static bool LooksLikeFailedStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("undeliver", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSendStatusState(string value)
    {
        return value.Equals("pending", StringComparison.OrdinalIgnoreCase)
            ? "delivery pending"
            : value;
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }

        return false;
    }

    private static bool TryReadString(JsonElement element, IReadOnlyList<string> propertyNames, out string value)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryReadString(element, propertyName, out value))
            {
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadInt64(JsonElement element, IReadOnlyList<string> propertyNames, out long value)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.String &&
                long.TryParse(property.GetString(), out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static ImsgSendResultException TargetMismatch(string field, string expected, string actual) =>
        new($"Target mismatch: imsg send returned {field}={actual}, but the selected conversation expected {field}={expected}. The pending send was marked failed so the user can refresh before retrying.");

    private static bool TryReadStatusFieldBoolean(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("status_fields", out var statusFields) ||
            statusFields.ValueKind != JsonValueKind.Object ||
            !statusFields.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadStatusFieldInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("status_fields", out var statusFields) ||
            statusFields.ValueKind != JsonValueKind.Object ||
            !statusFields.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
        {
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
            int.TryParse(property.GetString(), out value);
    }

    private static void AddFlagSummary(JsonElement element, string propertyName, string label, ICollection<string> parts)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.True)
        {
            parts.Add(label);
        }
    }
}

public sealed record MessageSendResult(JsonElement RawResult, string? MessageGuid);

public sealed record MessageSendWorkflowResult(
    MessageSendResult SendResult,
    string? SendStatusSummary,
    Exception? SendStatusError);

public sealed class ImsgSendResultException(string message) : Exception(message);
