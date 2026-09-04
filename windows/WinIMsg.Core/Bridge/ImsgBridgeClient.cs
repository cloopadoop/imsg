using System.Text.Json;
using WinIMsg.Core.Models;
using WinIMsg.Core.Rpc;
using WinIMsg.Core.Ssh;

namespace WinIMsg.Core.Bridge;

public sealed class ImsgBridgeClient : IAsyncDisposable
{
    private readonly SshCommandRunner _commandRunner;
    private SshImsgRpcSession? _session;

    public ImsgBridgeClient(SshCommandRunner? commandRunner = null)
    {
        _commandRunner = commandRunner ?? new SshCommandRunner();
    }

    public event EventHandler<JsonRpcNotification>? NotificationReceived;

    public event EventHandler<JsonRpcConnectionClosedEventArgs>? ConnectionClosed;

    public bool IsConnected => _session is { IsRunning: true };

    public async Task<ImsgProbeResult> ProbeAsync(ImsgBridgeSettings settings, CancellationToken cancellationToken = default)
    {
        var version = await _commandRunner.RunImsgCommandAsync(settings, ["--version"], cancellationToken);
        if (!version.Succeeded)
        {
            return ImsgProbeResult.Failed(
                $"Unable to run imsg over SSH: {version.ErrorSummary}",
                version.StandardOutput,
                version.StandardError,
                "imsg --version");
        }

        var status = await _commandRunner.RunImsgCommandAsync(settings, ["status", "--json"], cancellationToken);
        if (!status.Succeeded)
        {
            return ImsgProbeResult.Failed(
                $"Unable to read imsg status: {status.ErrorSummary}",
                version.StandardOutput,
                status.StandardError,
                "imsg status --json");
        }

        try
        {
            var capabilities = JsonSerializer.Deserialize<ImsgCapabilities>(
                status.StandardOutput,
                ImsgJson.Options) ?? new ImsgCapabilities();
            var baselineRead = await ProbeBaselineChatListAsync(settings, cancellationToken);
            return ImsgProbeResult.Success(
                version.StandardOutput.Trim(),
                capabilities,
                status.StandardOutput,
                baselineRead);
        }
        catch (JsonException ex)
        {
            return ImsgProbeResult.Failed(
                $"imsg status did not return valid JSON: {ex.Message}",
                status.StandardOutput,
                status.StandardError,
                "imsg status --json");
        }
    }

    private async Task<ImsgProbeCommandResult> ProbeBaselineChatListAsync(
        ImsgBridgeSettings settings,
        CancellationToken cancellationToken)
    {
        const string command = "imsg chats --limit 1 --json";
        var chats = await _commandRunner.RunImsgCommandAsync(
            settings,
            ["chats", "--limit", "1", "--json"],
            cancellationToken);
        if (!chats.Succeeded)
        {
            return ImsgProbeCommandResult.Failed(
                command,
                $"Unable to read Messages chat list/history through the SSH-launched context: {chats.ErrorSummary}");
        }

        var rowCount = chats.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
        return rowCount == 0
            ? ImsgProbeCommandResult.Success(
                command,
                "The Messages database opened, but imsg returned zero chats. If this Mac should have history, open Messages.app on the Mac and complete iMessage/SMS Forwarding setup.")
            : ImsgProbeCommandResult.Success(
                command,
                $"Read Messages chat database successfully; imsg returned {rowCount} chat row(s).");
    }

    public async Task ConnectAsync(ImsgBridgeSettings settings, CancellationToken cancellationToken = default)
    {
        await DisposeSessionAsync();
        _session = await SshImsgRpcSession.StartAsync(settings, cancellationToken);
        _session.Rpc.NotificationReceived += (_, notification) => NotificationReceived?.Invoke(this, notification);
        _session.Rpc.ConnectionClosed += (_, args) => ConnectionClosed?.Invoke(this, args);
    }

    public Task DisconnectAsync() => DisposeSessionAsync();

    public async Task<IReadOnlyList<ImsgChat>> ListChatsAsync(int limit = 10000, CancellationToken cancellationToken = default)
    {
        var result = await Rpc.InvokeAsync<JsonElement>("chats.list", new { limit }, cancellationToken);
        return RpcResultMapper.ReadArray<ImsgChat>(result, "chats");
    }

    public async Task<IReadOnlyList<ImsgMessage>> GetHistoryAsync(
        long chatId,
        int limit = 100,
        bool includeAttachments = true,
        bool convertAttachments = false,
        bool includeReactions = true,
        CancellationToken cancellationToken = default)
    {
        var result = await Rpc.InvokeAsync<JsonElement>(
            "messages.history",
            new
            {
                chat_id = chatId,
                limit,
                attachments = includeAttachments,
                convert_attachments = convertAttachments,
                include_reactions = includeReactions
            },
            cancellationToken);
        return RpcResultMapper.ReadArray<ImsgMessage>(result, "messages");
    }

    public Task<JsonElement> SubscribeAsync(
        long? chatId = null,
        long? sinceRowId = null,
        bool includeAttachments = true,
        bool includeReactions = true,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["attachments"] = includeAttachments,
            ["include_reactions"] = includeReactions
        };

        if (chatId is not null)
        {
            parameters["chat_id"] = chatId.Value;
        }

        if (sinceRowId is not null)
        {
            parameters["since_rowid"] = sinceRowId.Value;
        }

        return Rpc.InvokeAsync<JsonElement>("watch.subscribe", parameters, cancellationToken);
    }

    public Task<JsonElement> UnsubscribeAsync(long subscription, CancellationToken cancellationToken = default)
    {
        return Rpc.InvokeAsync<JsonElement>("watch.unsubscribe", new { subscription }, cancellationToken);
    }

    public Task<JsonElement> SendTextAsync(
        long? chatId,
        string? chatIdentifier,
        string text,
        string transport = "auto",
        string? service = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = ChatTarget(chatId, chatIdentifier, chatGuid);
        parameters["text"] = text;
        parameters["transport"] = transport;
        AddIfPresent(parameters, "service", service);
        return Rpc.InvokeAsync<JsonElement>("send", parameters, cancellationToken);
    }

    public Task<JsonElement> SendDirectTextAsync(
        string recipient,
        string text,
        string transport = "auto",
        string? service = null,
        string? region = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipient))
        {
            throw new ArgumentException("A recipient is required.", nameof(recipient));
        }

        var parameters = new Dictionary<string, object?>
        {
            ["to"] = recipient,
            ["text"] = text,
            ["transport"] = transport
        };
        AddIfPresent(parameters, "service", service);
        AddIfPresent(parameters, "region", region);
        return Rpc.InvokeAsync<JsonElement>("send", parameters, cancellationToken);
    }

    public Task<JsonElement> SendFileAsync(
        long? chatId,
        string? chatIdentifier,
        string remoteFilePath,
        string? text = null,
        string transport = "auto",
        string? service = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteFilePath))
        {
            throw new ArgumentException("A file path is required.", nameof(remoteFilePath));
        }

        var parameters = ChatTarget(chatId, chatIdentifier, chatGuid);
        parameters["file"] = remoteFilePath;
        parameters["transport"] = transport;
        AddIfPresent(parameters, "text", text);
        AddIfPresent(parameters, "service", service);
        return Rpc.InvokeAsync<JsonElement>("send", parameters, cancellationToken);
    }

    public Task<JsonElement> CreateChatAsync(
        IReadOnlyList<string> addresses,
        string? name = null,
        string? text = null,
        CancellationToken cancellationToken = default)
    {
        var cleanAddresses = addresses
            .Where(static address => !string.IsNullOrWhiteSpace(address))
            .Select(static address => address.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (cleanAddresses.Count == 0)
        {
            throw new ArgumentException("At least one chat address is required.", nameof(addresses));
        }

        var parameters = new Dictionary<string, object?>
        {
            ["addresses"] = cleanAddresses,
            ["service"] = "iMessage"
        };
        AddIfPresent(parameters, "name", name);
        AddIfPresent(parameters, "text", text);
        return Rpc.InvokeAsync<JsonElement>("chats.create", parameters, cancellationToken);
    }

    public Task<JsonElement> DeleteChatAsync(
        long? chatId,
        string? chatIdentifier,
        string? chatGuid = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = ChatTarget(chatId, chatIdentifier, chatGuid);
        return Rpc.InvokeAsync<JsonElement>("chats.delete", parameters, cancellationToken);
    }

    public Task<JsonElement> MarkUnreadAsync(
        long? chatId,
        string? chatIdentifier,
        string? chatGuid = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = ChatTarget(chatId, chatIdentifier, chatGuid);
        return Rpc.InvokeAsync<JsonElement>("chats.markUnread", parameters, cancellationToken);
    }

    public Task<JsonElement> SendRichAsync(
        long? chatId,
        string? chatIdentifier,
        string text,
        string? effect,
        string? replyTo,
        IReadOnlyList<RichTextFormattingRange>? textFormatting = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = ChatTarget(chatId, chatIdentifier, chatGuid);
        parameters["text"] = text;
        AddIfPresent(parameters, "effect", effect);
        AddIfPresent(parameters, "reply_to", replyTo);
        if (textFormatting is { Count: > 0 })
        {
            parameters["text_formatting"] = textFormatting;
        }

        return Rpc.InvokeAsync<JsonElement>("send.rich", parameters, cancellationToken);
    }

    public Task<JsonElement> SendPollAsync(
        long? chatId,
        string? chatIdentifier,
        string question,
        IReadOnlyList<string> options,
        string? replyTo = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException("A poll question is required.", nameof(question));
        }

        if (options.Count < 2)
        {
            throw new ArgumentException("At least two poll options are required.", nameof(options));
        }

        var parameters = ChatTarget(chatId, chatIdentifier, chatGuid);
        parameters["question"] = question;
        parameters["options"] = options;
        AddIfPresent(parameters, "reply_to", replyTo);
        return Rpc.InvokeAsync<JsonElement>("poll.send", parameters, cancellationToken);
    }

    public Task<JsonElement> SendAttachmentAsync(
        long? chatId,
        string? chatIdentifier,
        string remoteFilePath,
        bool audio,
        string? replyTo,
        string? chatGuid = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = ChatTarget(chatId, chatIdentifier, chatGuid);
        parameters["file"] = remoteFilePath;
        parameters["audio"] = audio;
        AddIfPresent(parameters, "reply_to", replyTo);
        return Rpc.InvokeAsync<JsonElement>("send.attachment", parameters, cancellationToken);
    }

    public Task<JsonElement> TapbackAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string reaction,
        bool remove,
        string? chatGuid = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = ChatTarget(chatId, chatIdentifier, chatGuid);
        parameters["message_id"] = messageGuid;
        parameters["message_guid"] = messageGuid;
        parameters["reaction"] = reaction;
        parameters["kind"] = reaction;
        parameters["remove"] = remove;
        return Rpc.InvokeAsync<JsonElement>("tapback", parameters, cancellationToken);
    }

    public Task<JsonElement> SetTypingAsync(
        long? chatId,
        string? chatIdentifier,
        bool typing,
        string? chatGuid = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = ChatTarget(chatId, chatIdentifier, chatGuid);
        parameters["typing"] = typing;
        return Rpc.InvokeAsync<JsonElement>("typing", parameters, cancellationToken);
    }

    public Task<JsonElement> MarkReadAsync(
        long? chatId,
        string? chatIdentifier,
        string? messageGuid = null,
        string? chatGuid = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = ChatTarget(chatId, chatIdentifier, chatGuid);
        AddIfPresent(parameters, "message_id", messageGuid);
        return Rpc.InvokeAsync<JsonElement>("read", parameters, cancellationToken);
    }

    public Task<JsonElement> EditMessageAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string text,
        string? chatGuid = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = ChatTarget(chatId, chatIdentifier, chatGuid);
        parameters["message_id"] = messageGuid;
        parameters["message_guid"] = messageGuid;
        parameters["text"] = text;
        return Rpc.InvokeAsync<JsonElement>("message.edit", parameters, cancellationToken);
    }

    public Task<JsonElement> UnsendMessageAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string? chatGuid = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = ChatTarget(chatId, chatIdentifier, chatGuid);
        parameters["message_id"] = messageGuid;
        parameters["message_guid"] = messageGuid;
        return Rpc.InvokeAsync<JsonElement>("message.unsend", parameters, cancellationToken);
    }

    public Task<JsonElement> DeleteMessageAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string? chatGuid = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = ChatTarget(chatId, chatIdentifier, chatGuid);
        parameters["message_id"] = messageGuid;
        parameters["message_guid"] = messageGuid;
        return Rpc.InvokeAsync<JsonElement>("message.delete", parameters, cancellationToken);
    }

    public Task<JsonElement> NotifyAnywaysAsync(
        long? chatId,
        string? chatIdentifier,
        string messageGuid,
        string? chatGuid = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = ChatTarget(chatId, chatIdentifier, chatGuid);
        parameters["message_id"] = messageGuid;
        parameters["message_guid"] = messageGuid;
        return Rpc.InvokeAsync<JsonElement>("message.notifyAnyways", parameters, cancellationToken);
    }

    public Task<JsonElement> GetSendStatusAsync(string messageGuid, CancellationToken cancellationToken = default)
    {
        return Rpc.InvokeAsync<JsonElement>("message.send_status", new { guid = messageGuid }, cancellationToken);
    }

    public Task<JsonElement> RenameGroupAsync(string chatGuid, string name, CancellationToken cancellationToken = default)
    {
        return Rpc.InvokeAsync<JsonElement>("group.rename", new { chat_guid = chatGuid, name }, cancellationToken);
    }

    public Task<JsonElement> SetGroupIconAsync(string chatGuid, string? remoteFilePath = null, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["chat_guid"] = chatGuid
        };
        AddIfPresent(parameters, "file", remoteFilePath);
        return Rpc.InvokeAsync<JsonElement>("group.setIcon", parameters, cancellationToken);
    }

    public Task<JsonElement> AddParticipantAsync(string chatGuid, string address, CancellationToken cancellationToken = default)
    {
        return Rpc.InvokeAsync<JsonElement>("group.addParticipant", new { chat_guid = chatGuid, address }, cancellationToken);
    }

    public Task<JsonElement> RemoveParticipantAsync(string chatGuid, string address, CancellationToken cancellationToken = default)
    {
        return Rpc.InvokeAsync<JsonElement>("group.removeParticipant", new { chat_guid = chatGuid, address }, cancellationToken);
    }

    public Task<JsonElement> LeaveGroupAsync(string chatGuid, CancellationToken cancellationToken = default)
    {
        return Rpc.InvokeAsync<JsonElement>("group.leave", new { chat_guid = chatGuid }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeSessionAsync();
    }

    private JsonRpcClient Rpc => _session?.Rpc ?? throw new InvalidOperationException("The imsg RPC session is not connected.");

    private async Task DisposeSessionAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }

    private static Dictionary<string, object?> ChatTarget(long? chatId, string? chatIdentifier, string? chatGuid = null)
    {
        var parameters = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(chatGuid))
        {
            parameters["chat_guid"] = chatGuid;
        }
        else if (chatId is not null)
        {
            parameters["chat_id"] = chatId.Value;
        }
        else if (!string.IsNullOrWhiteSpace(chatIdentifier))
        {
            parameters["chat_identifier"] = chatIdentifier;
        }
        else
        {
            throw new ArgumentException("A chat id or chat identifier is required.");
        }

        return parameters;
    }

    private static void AddIfPresent(Dictionary<string, object?> parameters, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters[key] = value;
        }
    }
}
