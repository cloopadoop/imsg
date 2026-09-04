using System.Globalization;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinIMsg.App.Contracts;
using WinIMsg.App.ViewModels;
using WinIMsg.Core.Models;
using WinIMsg.Core.Rpc;

namespace WinIMsg.App.Services;

/// <summary>
/// Loopback-only, token-protected web companion: a small JSON API plus an
/// embedded single-page UI that Ferdium (or any browser) can host. Security
/// boundaries: binds 127.0.0.1 only, all data and
/// action APIs require the session token, and the browser never sees SSH, Mac, or
/// profile secrets - it talks only to this in-app server, which reuses the
/// local cache for reads and the dedicated send channel for text sends.
/// </summary>
public sealed class WebCompanionService : IAsyncDisposable
{
    private readonly AppLogService _log;
    private readonly IMessageCache _cache;
    private readonly MessageSendService _messageSendService;
    private readonly IImsgClient _actionClient;
    private readonly AttachmentSendWorkflowService _attachmentSendWorkflow;
    private readonly Func<ImsgBridgeSettings> _settingsProvider;
    private readonly Func<WinIMsgSettings> _appSettingsProvider;
    private readonly Func<ImsgCapabilities> _capabilitiesProvider;
    private readonly Func<bool> _isBridgeConnected;
    private readonly Func<int> _unreadCountProvider;
    private readonly Func<string, Task<bool>> _markChatReadAsync;
    private readonly Func<string, Task>? _refreshChatAsync;
    private readonly Func<ImsgMessage, ImsgAttachment, string> _attachmentLocalPathResolver;
    private readonly List<HttpListenerResponse> _eventClients = [];
    private readonly Dictionary<string, DraftAttachment> _draftAttachments = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _draftAttachmentsLock = new();
    private readonly object _clientsLock = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _bootstrapNonces = new(StringComparer.Ordinal);
    private HttpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private string _token = string.Empty;

    public WebCompanionService(
        AppLogService log,
        IMessageCache cache,
        MessageSendService messageSendService,
        IImsgClient actionClient,
        AttachmentSendWorkflowService attachmentSendWorkflow,
        Func<ImsgBridgeSettings> settingsProvider,
        Func<WinIMsgSettings> appSettingsProvider,
        Func<ImsgCapabilities> capabilitiesProvider,
        Func<bool> isBridgeConnected,
        Func<int> unreadCountProvider,
        Func<string, Task<bool>> markChatReadAsync,
        Func<ImsgMessage, ImsgAttachment, string> attachmentLocalPathResolver,
        Func<string, Task>? refreshChatAsync = null)
    {
        _attachmentLocalPathResolver = attachmentLocalPathResolver;
        _log = log;
        _cache = cache;
        _messageSendService = messageSendService;
        _actionClient = actionClient;
        _attachmentSendWorkflow = attachmentSendWorkflow;
        _settingsProvider = settingsProvider;
        _appSettingsProvider = appSettingsProvider;
        _capabilitiesProvider = capabilitiesProvider;
        _isBridgeConnected = isBridgeConnected;
        _unreadCountProvider = unreadCountProvider;
        _markChatReadAsync = markChatReadAsync;
        _refreshChatAsync = refreshChatAsync;
    }

    public bool IsRunning => _listener is { IsListening: true };

    public int Port { get; private set; }

    public string CompanionUrl => $"http://127.0.0.1:{Port}/?token={_token}";

    public static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public bool AuthorizeBootstrapNonce(string nonce)
    {
        if (nonce.Length != 32 || nonce.Any(character => !Uri.IsHexDigit(character)))
        {
            return false;
        }

        _bootstrapNonces[nonce] = DateTimeOffset.UtcNow.AddMinutes(2);
        return true;
    }

    public void Start(int port, string token)
    {
        Stop();
        Port = port;
        _token = token;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _listener = listener;
        _lifetime = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(listener, _lifetime.Token));
        _ = Task.Run(() => KeepAliveLoopAsync(_lifetime.Token));
        _log.Info($"Web companion listening on 127.0.0.1:{port}.");
    }

    public void Stop()
    {
        List<DraftAttachment> drafts;
        lock (_draftAttachmentsLock)
        {
            drafts = [.. _draftAttachments.Values];
            _draftAttachments.Clear();
        }

        foreach (var draft in drafts)
        {
            TryDeleteDraftPath(draft.Path);
        }

        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        lock (_clientsLock)
        {
            foreach (var client in _eventClients)
            {
                TryClose(client);
            }

            _eventClients.Clear();
        }

        if (_listener is not null)
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
            }

            _listener = null;
            _log.Info("Web companion stopped.");
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    public void PublishMessageEvent(ImsgMessage message)
    {
        if (!IsRunning || message.IsReactionEvent)
        {
            return;
        }

        Broadcast("message", new
        {
            chatStableId = message.ChatStableId,
            fromMe = message.IsFromMe,
            preview = ChatListStateStore.BuildMessagePreview(message),
            date = message.SortDate?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        });
    }

    public void PublishUnreadCount(int unreadCount)
    {
        if (IsRunning)
        {
            Broadcast("unread", new { count = unreadCount });
        }
    }

    public void PublishActivity(string label, string state)
    {
        if (IsRunning)
        {
            Broadcast("activity", new { label, state, at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) });
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || !listener.IsListening)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.Warning($"Web companion accept failed. {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleRequestSafeAsync(context, cancellationToken));
        }
    }

    private async Task HandleRequestSafeAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            await HandleRequestAsync(context, cancellationToken);
        }
        catch (Exception ex)
        {
            _log.Warning($"Web companion request failed. {ex.GetType().Name}: {ex.Message}");
            TryClose(context.Response);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        var path = request.Url?.AbsolutePath ?? "/";

        // The cached PWA shell can outlive the native process and its session
        // cookie. A one-time nonce authorized through the winimsg: OS protocol
        // proves that the native launch occurred without exposing the API token.
        if (request.HttpMethod == "GET" && path == "/bootstrap")
        {
            var nonce = request.QueryString["nonce"] ?? string.Empty;
            if (!TryConsumeBootstrapNonce(nonce))
            {
                await WriteJsonAsync(response, 401, new { error = "invalid or expired launch nonce" });
                return;
            }

            SetSessionCookie(response);
            await WriteJsonAsync(response, 200, new { ready = true });
            return;
        }

        if (!IsAuthorized(request))
        {
            await WriteJsonAsync(response, 401, new { error = "missing or invalid token" });
            return;
        }

        // A valid query token upgrades to a session cookie so the page's own
        // fetch/EventSource calls stay authorized without repeating the token.
        if (QueryToken(request) is { } queryToken && TokenMatches(queryToken))
        {
            SetSessionCookie(response);
        }

        switch (request.HttpMethod, path)
        {
            case ("GET", "/"):
                await WriteTextAsync(response, 200, "text/html; charset=utf-8", WebCompanionUi.Html);
                return;
            case ("GET", "/manifest.webmanifest"):
                response.Headers["Cache-Control"] = "public, max-age=86400";
                await WriteTextAsync(response, 200, "application/manifest+json; charset=utf-8", WebCompanionUi.Manifest, cancellationToken);
                return;
            case ("GET", "/sw.js"):
                response.Headers["Cache-Control"] = "no-cache";
                await WriteTextAsync(response, 200, "application/javascript; charset=utf-8", WebCompanionUi.ServiceWorker, cancellationToken);
                return;
            case ("GET", "/api/chats"):
                await HandleChatsAsync(response, cancellationToken);
                return;
            case ("GET", "/api/badge"):
                await WriteJsonAsync(response, 200, new { unread = _unreadCountProvider() });
                return;
            case ("GET", "/api/capabilities"):
                await HandleCapabilitiesAsync(response);
                return;
            case ("GET", "/api/attachments/file"):
                await HandleAttachmentFileAsync(request, response, cancellationToken);
                return;
            case ("GET", "/api/emoji"):
                response.Headers["Cache-Control"] = "private, max-age=86400";
                await WriteTextAsync(response, 200, "application/json; charset=utf-8", WebCompanionEmojiData.Json, cancellationToken);
                return;
            case ("GET", "/api/events"):
                await HandleEventStreamAsync(response);
                return;
            case ("POST", "/api/refresh"):
                if (_refreshChatAsync is not null)
                {
                    await _refreshChatAsync(string.Empty);
                }
                await WriteJsonAsync(response, 200, new { ok = true });
                return;
            case ("POST", "/api/send"):
                await HandleSendAsync(request, response, cancellationToken);
                return;
            case ("POST", "/api/messages/tapback"):
            case ("POST", "/api/messages/edit"):
            case ("POST", "/api/messages/unsend"):
            case ("POST", "/api/messages/delete"):
                await HandleMessageActionAsync(request, response, path, cancellationToken);
                return;
            case ("POST", "/api/attachments"):
                await HandleAttachmentUploadAsync(request, response, cancellationToken);
                return;
            case ("POST", "/api/attachments/discard"):
                await HandleAttachmentDiscardAsync(request, response, cancellationToken);
                return;
        }

        if (request.HttpMethod == "POST" &&
            path.StartsWith("/api/chats/", StringComparison.Ordinal) &&
            path.EndsWith("/refresh", StringComparison.Ordinal))
        {
            var stableId = WebUtility.UrlDecode(path["/api/chats/".Length..^"/refresh".Length]);
            if (_refreshChatAsync is not null)
            {
                await _refreshChatAsync(stableId);
            }
            await WriteJsonAsync(response, 200, new { ok = true });
            return;
        }

        if (request.HttpMethod == "GET" &&
            path.StartsWith("/api/chats/", StringComparison.Ordinal) &&
            path.EndsWith("/messages", StringComparison.Ordinal))
        {
            var stableId = WebUtility.UrlDecode(path["/api/chats/".Length..^"/messages".Length]);
            var limit = int.TryParse(request.QueryString["limit"], out var parsed) && parsed > 0
                ? parsed
                : 100;
            await HandleMessagesAsync(response, stableId, limit, cancellationToken);
            return;
        }

        if (request.HttpMethod == "POST" &&
            path.StartsWith("/api/chats/", StringComparison.Ordinal) &&
            path.EndsWith("/read", StringComparison.Ordinal))
        {
            var stableId = WebUtility.UrlDecode(path["/api/chats/".Length..^"/read".Length]);
            var marked = await _markChatReadAsync(stableId);
            await WriteJsonAsync(response, 200, new { ok = true, marked, unread = _unreadCountProvider() });
            return;
        }

        await WriteJsonAsync(response, 404, new { error = "not found" });
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            return false;
        }

        if (QueryToken(request) is { } queryToken && TokenMatches(queryToken))
        {
            return true;
        }

        var authorization = request.Headers["Authorization"];
        if (authorization is not null &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            TokenMatches(authorization["Bearer ".Length..].Trim()))
        {
            return true;
        }

        var cookie = request.Cookies["winimsg_token"];
        return cookie is not null && TokenMatches(cookie.Value);
    }

    private static string? QueryToken(HttpListenerRequest request) => request.QueryString["token"];

    private void SetSessionCookie(HttpListenerResponse response) =>
        response.Headers.Add("Set-Cookie", $"winimsg_token={_token}; Path=/; HttpOnly; SameSite=Strict");

    private bool TryConsumeBootstrapNonce(string nonce)
    {
        return _bootstrapNonces.TryRemove(nonce, out var expiresAt) && expiresAt >= DateTimeOffset.UtcNow;
    }

    private bool TokenMatches(string candidate) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate),
            Encoding.UTF8.GetBytes(_token));

    private async Task HandleChatsAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        var chats = await _cache.GetChatsAsync(cancellationToken);
        var previews = await _cache.GetLatestMessageTextByChatStableIdAsync(cancellationToken);
        var dates = await _cache.GetLatestMessageDateByChatStableIdAsync(cancellationToken);
        var appSettings = _appSettingsProvider();
        var groupedChats = ChatListItem.FromChats(
            chats,
            appSettings.MergeChatsByParticipants,
            appSettings.PhoneNumberRegion,
            previews,
            dates);
        var rows = groupedChats
            .Select(item => new
            {
                stableId = item.StableId,
                sourceStableIds = item.StableIds,
                name = item.DisplayName,
                service = item.Chat.Service,
                isGroup = item.Chat.IsGroup,
                unread = item.UnreadCount,
                lastMessageAt = item.EffectiveLatestMessageDate?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                preview = CompanionPreview(item, previews)
            })
            .OrderByDescending(row => row.lastMessageAt ?? string.Empty, StringComparer.Ordinal)
            .ToList();
        await WriteJsonAsync(response, 200, rows, cancellationToken);
    }

    private async Task HandleMessagesAsync(
        HttpListenerResponse response,
        string stableId,
        int limit,
        CancellationToken cancellationToken)
    {
        var chats = await _cache.GetChatsAsync(cancellationToken);
        var groupedChats = ChatListItem.FromChats(
            chats,
            _appSettingsProvider().MergeChatsByParticipants,
            _appSettingsProvider().PhoneNumberRegion);
        var sourceIds = groupedChats
            .FirstOrDefault(item => item.ContainsStableId(stableId))?.StableIds
            ?? [stableId];
        var messages = (await Task.WhenAll(sourceIds.Select(sourceId =>
                _cache.GetRecentMessagesAsync(sourceId, limit, cancellationToken))))
            .SelectMany(static batch => batch)
            .GroupBy(message => message.MessageActionId ?? $"row:{message.ChatStableId}:{message.DateText}:{message.Text}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(message => message.SortDate ?? DateTimeOffset.MinValue)
            .TakeLast(limit)
            .ToList();
        var rows = messages
            .Select(message => new
            {
                guid = message.Guid,
                text = DisplayTextFormatter.MessageText(message.Text, string.Empty),
                fromMe = message.IsFromMe,
                sender = message.DisplaySender,
                date = message.SortDate?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                attachments = message.Attachments
                    .Select((attachment, index) => (Attachment: attachment, Index: index))
                    .Where(pair => AttachmentPresentationPolicy.IsDisplayable(pair.Attachment))
                    .Select(pair =>
                    {
                        var localPath = _attachmentLocalPathResolver(message, pair.Attachment);
                        var hasLocalFile = localPath is { Length: > 0 } && File.Exists(localPath);
                        var kind = AttachmentPresentationPolicy.Classify(pair.Attachment, localPath);
                        return new
                        {
                            name = pair.Attachment.DisplayName,
                            kind = kind.ToString().ToLowerInvariant(),
                            contentType = ContentTypeForAttachment(pair.Attachment, localPath, kind),
                            url = hasLocalFile && kind is AttachmentPresentationKind.Image or AttachmentPresentationKind.Audio or AttachmentPresentationKind.Video
                                ? $"/api/attachments/file?stableId={Uri.EscapeDataString(stableId)}&guid={Uri.EscapeDataString(message.Guid ?? string.Empty)}&index={pair.Index}"
                                : null
                        };
                    })
                    .ToList(),
                reactions = message.Reactions
                    .Select(reaction => new
                    {
                        emoji = reaction.Emoji,
                        type = reaction.Type,
                        fromMe = reaction.IsFromMe
                    })
                    .ToList()
            })
            .ToList();
        await WriteJsonAsync(response, 200, rows, cancellationToken);
    }

    private async Task HandleAttachmentFileAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        var stableId = request.QueryString["stableId"];
        var guid = request.QueryString["guid"];
        if (string.IsNullOrWhiteSpace(stableId) ||
            string.IsNullOrWhiteSpace(guid) ||
            !int.TryParse(request.QueryString["index"], out var index) ||
            index < 0)
        {
            await WriteJsonAsync(response, 400, new { error = "stableId, guid, and index are required" });
            return;
        }

        // Resolution goes through the message cache rather than raw paths, so
        // the endpoint can only ever serve files the attachment pipeline
        // already downloaded for this conversation.
        var messages = await _cache.GetMessagesAsync(stableId, cancellationToken);
        var message = messages.FirstOrDefault(candidate =>
            string.Equals(candidate.Guid, guid, StringComparison.OrdinalIgnoreCase));
        if (message is null || index >= message.Attachments.Count)
        {
            await WriteJsonAsync(response, 404, new { error = "unknown attachment" });
            return;
        }

        var localPath = _attachmentLocalPathResolver(message, message.Attachments[index]);
        if (localPath is not { Length: > 0 } || !File.Exists(localPath))
        {
            await WriteJsonAsync(response, 404, new { error = "attachment not downloaded" });
            return;
        }

        var kind = AttachmentPresentationPolicy.Classify(message.Attachments[index], localPath);
        var contentType = ContentTypeForAttachment(message.Attachments[index], localPath, kind);
        var info = new FileInfo(localPath);
        var range = ParseRange(request.Headers["Range"], info.Length);
        response.ContentType = contentType;
        response.Headers["Accept-Ranges"] = "bytes";
        response.Headers["Content-Disposition"] = "inline";
        response.Headers["Cache-Control"] = "private, max-age=3600";
        if (range is null)
        {
            response.StatusCode = 200;
            response.ContentLength64 = info.Length;
            await using var stream = File.OpenRead(localPath);
            await stream.CopyToAsync(response.OutputStream, cancellationToken);
        }
        else
        {
            response.StatusCode = 206;
            response.Headers["Accept-Ranges"] = "bytes";
            response.Headers["Content-Range"] = $"bytes {range.Value.Start}-{range.Value.End}/{info.Length}";
            response.ContentLength64 = range.Value.End - range.Value.Start + 1;
            await using var stream = File.OpenRead(localPath);
            stream.Position = range.Value.Start;
            await CopyExactlyAsync(stream, response.OutputStream, response.ContentLength64, cancellationToken);
        }
        response.Close();
    }

    private static string CompanionPreview(ChatListItem item, IReadOnlyDictionary<string, string> previews)
    {
        var preview = item.LatestMessagePreview;
        if (!string.IsNullOrWhiteSpace(preview))
        {
            return preview;
        }

        // Attachment-only messages are stored with U+FFFC as their text. The
        // native list calls these "Attachment"; preserve that useful fallback
        // after the placeholder has been normalized away.
        return item.StableIds.Any(id => previews.TryGetValue(id, out var raw) && !string.IsNullOrWhiteSpace(raw))
            ? "Attachment"
            : string.Empty;
    }

    private static string ContentTypeForAttachment(ImsgAttachment attachment, string path, AttachmentPresentationKind kind)
    {
        var mime = attachment.ConvertedMimeType ?? attachment.MimeType;
        if (mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ||
            mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
            mime?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return mime;
        }

        return kind switch
        {
            AttachmentPresentationKind.Image => "image/*",
            AttachmentPresentationKind.Audio => ContentTypeForFile(path) is { } audioType && audioType != "application/octet-stream"
                ? audioType
                : "audio/mp4",
            AttachmentPresentationKind.Video => ContentTypeForFile(path) is { } videoType && videoType != "application/octet-stream"
                ? videoType
                : "video/mp4",
            _ => ContentTypeForFile(path)
        };
    }

    private static string ContentTypeForFile(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".m4a" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".mp4" => "video/mp4",
        ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        _ => "application/octet-stream"
    };

    private static (long Start, long End)? ParseRange(string? value, long length)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var part = value[6..].Split(',', 2)[0].Trim();
        var pieces = part.Split('-', 2);
        if (pieces.Length != 2 || !long.TryParse(pieces[0], out var start) || start < 0 || start >= length)
        {
            return null;
        }

        var end = string.IsNullOrWhiteSpace(pieces[1])
            ? length - 1
            : long.TryParse(pieces[1], out var requestedEnd) ? Math.Min(requestedEnd, length - 1) : -1;
        return end >= start ? (start, end) : null;
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            count -= read;
        }
    }

    private async Task HandleCapabilitiesAsync(HttpListenerResponse response)
    {
        var capabilities = _capabilitiesProvider();
        await WriteJsonAsync(response, 200, new
        {
            connected = _isBridgeConnected(),
            canTapback = capabilities.HasAdvancedBridge && capabilities.Supports("tapback"),
            canEdit = capabilities.HasAdvancedBridge &&
                capabilities.SupportsAny("message.edit", "edit") &&
                capabilities.SupportsSelector("editMessage", "editMessageItem"),
            canUnsend = capabilities.HasAdvancedBridge && capabilities.SupportsAny("message.unsend", "unsend"),
            canDelete = capabilities.HasAdvancedBridge && capabilities.SupportsAny("message.delete", "delete")
        });
    }

    private async Task HandleMessageActionAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        string path,
        CancellationToken cancellationToken)
    {
        if (!_isBridgeConnected())
        {
            await WriteJsonAsync(response, 503, new { error = "bridge disconnected" });
            return;
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync(cancellationToken);
        MessageActionBody? payload;
        try
        {
            payload = JsonSerializer.Deserialize<MessageActionBody>(body, ImsgJson.Options);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.StableId) ||
            string.IsNullOrWhiteSpace(payload.MessageGuid))
        {
            await WriteJsonAsync(response, 400, new { error = "stableId and messageGuid are required" });
            return;
        }

        var chats = await _cache.GetChatsAsync(cancellationToken);
        var chat = chats.FirstOrDefault(candidate =>
            string.Equals(candidate.StableId, payload.StableId, StringComparison.OrdinalIgnoreCase));
        if (chat is null)
        {
            await WriteJsonAsync(response, 404, new { error = "unknown chat" });
            return;
        }

        try
        {
            PublishActivity("Refreshing after action...", "busy");
            switch (path)
            {
                case "/api/messages/tapback":
                    if (string.IsNullOrWhiteSpace(payload.Reaction))
                    {
                        await WriteJsonAsync(response, 400, new { error = "reaction is required" });
                        return;
                    }

                    await _actionClient.TapbackAsync(
                        chat.Id, chat.Identifier, payload.MessageGuid, payload.Reaction, payload.Remove, chat.Guid, cancellationToken);
                    break;
                case "/api/messages/edit":
                    if (string.IsNullOrWhiteSpace(payload.Text))
                    {
                        await WriteJsonAsync(response, 400, new { error = "text is required" });
                        return;
                    }

                    await _actionClient.EditMessageAsync(
                        chat.Id, chat.Identifier, payload.MessageGuid, payload.Text, chat.Guid, cancellationToken);
                    break;
                case "/api/messages/unsend":
                    await _actionClient.UnsendMessageAsync(
                        chat.Id, chat.Identifier, payload.MessageGuid, chat.Guid, cancellationToken);
                    break;
                case "/api/messages/delete":
                    await _actionClient.DeleteMessageAsync(
                        chat.Id, chat.Identifier, payload.MessageGuid, chat.Guid, cancellationToken);
                    break;
            }

            if (_refreshChatAsync is not null)
            {
                await _refreshChatAsync(chat.StableId);
            }

            _log.Info($"Web companion message action completed. action={path}; chat={chat.StableId}");
            PublishActivity("Refresh complete", "ready");
            await WriteJsonAsync(response, 200, new { ok = true });
        }
        catch (Exception ex)
        {
            _log.Warning($"Web companion message action failed. action={path}; {ex.GetType().Name}: {ex.Message}");
            PublishActivity("Action failed", "error");
            await WriteJsonAsync(response, 502, new { error = ex.Message });
        }
    }

    private async Task HandleAttachmentUploadAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        if (!_isBridgeConnected())
        {
            await WriteJsonAsync(response, 503, new { error = "bridge disconnected" });
            return;
        }

        var stableId = request.QueryString["stableId"];
        var fileName = request.QueryString["name"];
        if (string.IsNullOrWhiteSpace(stableId) || string.IsNullOrWhiteSpace(fileName))
        {
            await WriteJsonAsync(response, 400, new { error = "stableId and name are required" });
            return;
        }

        var chats = await _cache.GetChatsAsync(cancellationToken);
        var chat = chats.FirstOrDefault(candidate =>
            string.Equals(candidate.StableId, stableId, StringComparison.OrdinalIgnoreCase));
        if (chat is null)
        {
            await WriteJsonAsync(response, 404, new { error = "unknown chat" });
            return;
        }

        var safeName = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "attachment";
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "win-imsg-web", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var tempPath = Path.Combine(tempDirectory, safeName);
        try
        {
            await using (var file = File.Create(tempPath))
            {
                await request.InputStream.CopyToAsync(file, cancellationToken);
            }

            var draftId = Guid.NewGuid().ToString("N");
            lock (_draftAttachmentsLock)
            {
                CleanupDraftAttachmentsLocked();
                _draftAttachments[draftId] = new DraftAttachment(draftId, chat.StableId, safeName, tempPath, DateTimeOffset.UtcNow);
            }

            // The browser owns the draft until the user presses Send. Keeping
            // this endpoint as a staging operation prevents accidental
            // attachment sends from file-picker or drag/drop events.
            await WriteJsonAsync(response, 200, new { ok = true, draftAttachmentId = draftId, name = safeName });
        }
        catch (Exception ex)
        {
            _log.Warning($"Web companion attachment failed. {ex.GetType().Name}: {ex.Message}");
            await WriteJsonAsync(response, 502, new { error = ex.Message });
        }
        finally
        {
            lock (_draftAttachmentsLock)
            {
                if (!_draftAttachments.Values.Any(draft => string.Equals(draft.Path, tempPath, StringComparison.OrdinalIgnoreCase)))
                {
                    TryDeleteDraftPath(tempPath);
                }
            }
        }
    }

    private async Task HandleAttachmentDiscardAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync(cancellationToken);
        AttachmentDiscardBody? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AttachmentDiscardBody>(body, ImsgJson.Options);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.DraftAttachmentId))
        {
            await WriteJsonAsync(response, 400, new { error = "draftAttachmentId is required" });
            return;
        }

        DraftAttachment? draft = null;
        lock (_draftAttachmentsLock)
        {
            if (_draftAttachments.Remove(payload.DraftAttachmentId, out var removed))
            {
                draft = removed;
            }
        }

        if (draft is not null)
        {
            TryDeleteDraftPath(draft.Path);
        }

        await WriteJsonAsync(response, 200, new { ok = true });
    }

    private async Task HandleSendAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        PublishActivity("Sending message...", "busy");
        if (!_isBridgeConnected())
        {
            PublishActivity("Bridge disconnected", "error");
            await WriteJsonAsync(response, 503, new { error = "bridge disconnected" });
            return;
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync(cancellationToken);
        SendRequestBody? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SendRequestBody>(body, ImsgJson.Options);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null || (string.IsNullOrWhiteSpace(payload.Text) && (payload.AttachmentIds?.Count ?? 0) == 0))
        {
            await WriteJsonAsync(response, 400, new { error = "text or attachments are required" });
            return;
        }

        // New-message composition: no chat yet, only recipients.
        if (string.IsNullOrWhiteSpace(payload.StableId))
        {
            if ((payload.AttachmentIds?.Count ?? 0) > 0)
            {
                await WriteJsonAsync(response, 400, new { error = "attachments require an existing conversation" });
                return;
            }

            var recipients = (payload.Recipients ?? [])
                .Where(static recipient => !string.IsNullOrWhiteSpace(recipient))
                .Select(static recipient => recipient.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (recipients.Count == 0)
            {
                await WriteJsonAsync(response, 400, new { error = "stableId or recipients are required" });
                return;
            }

            try
            {
                var directResult = recipients.Count == 1
                    ? await _messageSendService.SendDirectTextAsync(recipients[0], payload.Text!, service: "auto", cancellationToken: cancellationToken)
                    : await _messageSendService.CreateChatAsync(recipients, name: null, payload.Text!, cancellationToken);
                _log.Info($"Web companion draft send completed. recipients={recipients.Count}");
                PublishActivity("Send complete", "ready");
                await WriteJsonAsync(response, 200, new { ok = true, guid = directResult.MessageGuid });
            }
            catch (Exception ex)
            {
                _log.Warning($"Web companion draft send failed. {ex.GetType().Name}: {ex.Message}");
                PublishActivity("Send failed", "error");
                await WriteJsonAsync(response, 502, new { error = ex.Message });
            }

            return;
        }

        var chats = await _cache.GetChatsAsync(cancellationToken);
        var chat = chats.FirstOrDefault(candidate =>
            string.Equals(candidate.StableId, payload.StableId, StringComparison.OrdinalIgnoreCase));
        if (chat is null)
        {
            await WriteJsonAsync(response, 404, new { error = "unknown chat" });
            return;
        }

        var draftIds = (payload.AttachmentIds ?? [])
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var drafts = TakeDraftAttachments(draftIds, chat.StableId);
        if (drafts.Count != draftIds.Count)
        {
            await WriteJsonAsync(response, 400, new { error = "one or more attachments are no longer available; attach them again" });
            return;
        }

        try
        {
            string? guid = null;
            if (drafts.Count > 0)
            {
                var capabilities = _capabilitiesProvider();
                var attachmentResult = await _attachmentSendWorkflow.SendAsync(
                    _settingsProvider(),
                    ChatListItem.From(chat),
                    drafts.Select(static draft => draft.Path).ToList(),
                    payload.Text,
                    capabilities.SupportsAdvanced("send.attachment"),
                    progress: null,
                    cancellationToken);
                var failed = attachmentResult.Files.FirstOrDefault(file => !file.Sent && !file.IsUnconfirmed);
                if (failed is not null)
                {
                    await WriteJsonAsync(response, 502, new { error = failed.ErrorMessage ?? "attachment send failed" });
                    return;
                }

                RemoveDraftAttachments(drafts);
            }
            else
            {
                // Same target-validated send path the native composer uses;
                // raw client params fail upstream validation for some services.
                var target = ConversationTarget.FromChat(chat, chat.DisplayName);
                var outgoingService = MessageSendWorkflowService.OutgoingServiceForChat(chat.Service);
                var workflowResult = await _messageSendService.SendTextWithStatusAsync(
                    target,
                    payload.Text!,
                    outgoingService,
                    pollSendStatus: false,
                    cancellationToken);
                guid = workflowResult.SendResult.MessageGuid;
            }

            _log.Info($"Web companion send completed. chat={chat.StableId}; attachments={drafts.Count}; guidReturned={!string.IsNullOrWhiteSpace(guid)}");
            PublishActivity("Send complete; refreshing...", "ready");
            await WriteJsonAsync(response, 200, new { ok = true, guid });
        }
        catch (Exception ex)
        {
            _log.Warning($"Web companion send failed. chat={chat.StableId}; {ex.GetType().Name}: {ex.Message}");
            PublishActivity("Send failed", "error");
            await WriteJsonAsync(response, 502, new { error = ex.Message });
        }
    }

    private Task HandleEventStreamAsync(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.SendChunked = true;
        lock (_clientsLock)
        {
            _eventClients.Add(response);
        }

        // The response is intentionally left open; Broadcast writes to it and
        // prunes it when the client goes away.
        return WriteRawAsync(response, ": connected\n\n");
    }

    private List<DraftAttachment> TakeDraftAttachments(IReadOnlyList<string> ids, string stableId)
    {
        lock (_draftAttachmentsLock)
        {
            CleanupDraftAttachmentsLocked();
            return ids
                .Select(id => _draftAttachments.TryGetValue(id, out var draft) &&
                    string.Equals(draft.StableId, stableId, StringComparison.OrdinalIgnoreCase)
                    ? draft
                    : null)
                .Where(static draft => draft is not null)
                .Cast<DraftAttachment>()
                .ToList();
        }
    }

    private void RemoveDraftAttachments(IEnumerable<DraftAttachment> drafts)
    {
        lock (_draftAttachmentsLock)
        {
            foreach (var draft in drafts)
            {
                _draftAttachments.Remove(draft.Id);
                TryDeleteDraftPath(draft.Path);
            }
        }
    }

    private void CleanupDraftAttachmentsLocked()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        foreach (var draft in _draftAttachments.Values.Where(draft => draft.CreatedAt < cutoff).ToList())
        {
            _draftAttachments.Remove(draft.Id);
            TryDeleteDraftPath(draft.Path);
        }
    }

    private static void TryDeleteDraftPath(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Broadcast(string eventName, object payload)
    {
        var frame = $"event: {eventName}\ndata: {JsonSerializer.Serialize(payload, ImsgJson.Options)}\n\n";
        List<HttpListenerResponse> clients;
        lock (_clientsLock)
        {
            clients = [.. _eventClients];
        }

        foreach (var client in clients)
        {
            _ = WriteRawAsync(client, frame).ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        DropClient(client);
                    }
                },
                TaskScheduler.Default);
        }
    }

    private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(25), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Broadcast("ping", new { at = DateTimeOffset.UtcNow.ToString("O") });
        }
    }

    private void DropClient(HttpListenerResponse client)
    {
        lock (_clientsLock)
        {
            _eventClients.Remove(client);
        }

        TryClose(client);
    }

    private static async Task WriteRawAsync(HttpListenerResponse response, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await response.OutputStream.WriteAsync(bytes);
        await response.OutputStream.FlushAsync();
    }

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        int statusCode,
        object payload,
        CancellationToken cancellationToken = default)
    {
        await WriteTextAsync(
            response,
            statusCode,
            "application/json; charset=utf-8",
            JsonSerializer.Serialize(payload, ImsgJson.Options),
            cancellationToken);
    }

    private static async Task WriteTextAsync(
        HttpListenerResponse response,
        int statusCode,
        string contentType,
        string body,
        CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = statusCode;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.Close();
    }

    private static void TryClose(HttpListenerResponse response)
    {
        try
        {
            response.Close();
        }
        catch
        {
        }
    }

    private static DateTimeOffset? ReadDate(string? value)
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

    private sealed record SendRequestBody(
        string? StableId,
        string? Text,
        IReadOnlyList<string>? Recipients,
        IReadOnlyList<string>? AttachmentIds);

    private sealed record AttachmentDiscardBody(string? DraftAttachmentId);

    private sealed record DraftAttachment(
        string Id,
        string StableId,
        string Name,
        string Path,
        DateTimeOffset CreatedAt);

    private sealed record MessageActionBody(
        string? StableId,
        string? MessageGuid,
        string? Reaction,
        bool Remove,
        string? Text);
}
