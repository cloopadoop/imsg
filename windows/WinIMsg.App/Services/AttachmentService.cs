using System.Security.Cryptography;
using System.Text;
using WinIMsg.Core;
using WinIMsg.Core.Models;
using WinIMsg.Core.Ssh;

namespace WinIMsg.App.Services;

public interface IAttachmentUploadService
{
    Task<string> UploadAsync(
        ImsgBridgeSettings settings,
        string localPath,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task DeleteUploadedAsync(
        ImsgBridgeSettings settings,
        string remotePath,
        CancellationToken cancellationToken = default);
}

public sealed class AttachmentService(
    AppDataPaths paths,
    SshFileTransferService fileTransfer,
    MacHostActionService? macHostActions = null) : IAttachmentUploadService
{
    private readonly HashSet<string> _failedDownloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _queuedDownloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeOrFailedTranscodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly object _stateLock = new();

    public event EventHandler<string>? DownloadStateChanged;

    public Task<string> UploadAsync(
        ImsgBridgeSettings settings,
        string localPath,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        fileTransfer.UploadAttachmentAsync(settings, localPath, progress, cancellationToken);

    public Task DeleteUploadedAsync(
        ImsgBridgeSettings settings,
        string remotePath,
        CancellationToken cancellationToken = default) =>
        fileTransfer.DeleteUploadedAttachmentAsync(settings, remotePath, cancellationToken);

    public async Task<string> DownloadAsync(
        ImsgBridgeSettings settings,
        ImsgMessage message,
        ImsgAttachment attachment,
        bool notifySuccess = true,
        bool notifyFailure = true,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message.Guid))
        {
            throw new InvalidOperationException("Attachment download requires a message GUID.");
        }

        if (string.IsNullOrWhiteSpace(attachment.RemotePath))
        {
            throw new InvalidOperationException("Attachment does not expose a remote path.");
        }

        try
        {
            var localPath = await fileTransfer.DownloadAttachmentAsync(
                settings,
                attachment.RemotePath,
                GetLocalRoot(message),
                GetCacheFileName(attachment),
                progress,
                cancellationToken);
            localPath = await TranscodeHeicIfNeededAsync(localPath);
            MarkDownloadSucceeded(message.Guid, attachment.RemotePath, notify: notifySuccess, forceNotify: notifySuccess);
            return localPath;
        }
        catch (Exception directDownloadError) when (ShouldAskMacToMaterialize(attachment, directDownloadError))
        {
            try
            {
                var materializer = macHostActions ?? new MacHostActionService();
                await materializer.MaterializeAttachmentAsync(settings, message, attachment, cancellationToken);
                var localPath = await fileTransfer.DownloadAttachmentAsync(
                    settings,
                    attachment.RemotePath,
                    GetLocalRoot(message),
                    GetCacheFileName(attachment),
                    progress,
                    cancellationToken);
                localPath = await TranscodeHeicIfNeededAsync(localPath);
                MarkDownloadSucceeded(message.Guid, attachment.RemotePath, notify: notifySuccess, forceNotify: notifySuccess);
                return localPath;
            }
            catch (Exception materializeError)
            {
                MarkDownloadFailed(message.Guid, attachment.RemotePath, notify: notifyFailure, forceNotify: notifyFailure);
                throw new InvalidOperationException(
                    "Attachment is not available locally on the Mac yet. win-imsg asked Messages to open the conversation and waited for the file, but the file still could not be downloaded.",
                    materializeError);
            }
        }
        catch
        {
            MarkDownloadFailed(message.Guid, attachment.RemotePath, notify: notifyFailure, forceNotify: notifyFailure);
            throw;
        }
    }

    public string GetLocalPath(ImsgMessage message, ImsgAttachment attachment)
    {
        var localPath = Path.Combine(GetLocalRoot(message), SanitizeFileName(GetCacheFileName(attachment)));
        if (HeicImageTranscoder.IsHeicPath(localPath))
        {
            var transcodedPath = HeicImageTranscoder.GetTranscodedPath(localPath);
            if (File.Exists(transcodedPath))
            {
                return transcodedPath;
            }
        }

        return localPath;
    }

    public void QueueAutoDownload(
        ImsgBridgeSettings settings,
        IEnumerable<ImsgMessage> messages,
        bool enabled,
        bool isConnected,
        Action<Exception>? onFailure = null)
    {
        if (!enabled || !isConnected)
        {
            return;
        }

        var candidates = new List<AttachmentDownloadCandidate>();
        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.Guid))
            {
                continue;
            }

            foreach (var attachment in message.Attachments.Where(IsAutoDownloadCandidate))
            {
                var localPath = GetLocalPath(message, attachment);
                if (File.Exists(localPath))
                {
                    QueueHeicBackfillTranscode(message.Guid, localPath);
                    MarkDownloadSucceeded(message.Guid, attachment.RemotePath!);
                    continue;
                }

                var key = DownloadKey(message.Guid, attachment.RemotePath!);
                lock (_stateLock)
                {
                    if (_failedDownloads.Contains(key))
                    {
                        continue;
                    }

                    if (!_queuedDownloads.Add(key))
                    {
                        continue;
                    }
                }

                candidates.Add(new AttachmentDownloadCandidate(message, attachment, key));
            }
        }

        foreach (var candidate in candidates)
        {
            _ = Task.Run(() => DownloadQueuedAttachmentAsync(settings, candidate, onFailure));
        }
    }

    public bool HasFailedDownload(ImsgMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Guid))
        {
            return false;
        }

        lock (_stateLock)
        {
            return message.Attachments.Any(attachment =>
                IsAutoDownloadCandidate(attachment) &&
                _failedDownloads.Contains(DownloadKey(message.Guid, attachment.RemotePath!)));
        }
    }

    public bool MarkDownloadFailed(string messageGuid, string remotePath, bool notify = true, bool forceNotify = false)
    {
        var changed = false;
        lock (_stateLock)
        {
            changed = _failedDownloads.Add(DownloadKey(messageGuid, remotePath));
        }

        if (notify && (changed || forceNotify))
        {
            DownloadStateChanged?.Invoke(this, messageGuid);
        }

        return changed;
    }

    public bool MarkDownloadSucceeded(string messageGuid, string remotePath, bool notify = true, bool forceNotify = false)
    {
        var changed = false;
        lock (_stateLock)
        {
            changed = _failedDownloads.Remove(DownloadKey(messageGuid, remotePath));
        }

        if (notify && (changed || forceNotify))
        {
            DownloadStateChanged?.Invoke(this, messageGuid);
        }

        return changed;
    }

    public static string DownloadKey(string messageGuid, string remotePath) => $"{messageGuid}|{remotePath}";

    private async Task<string> TranscodeHeicIfNeededAsync(string localPath)
    {
        if (!HeicImageTranscoder.IsHeicPath(localPath))
        {
            return localPath;
        }

        var transcodedPath = await HeicImageTranscoder.TryTranscodeToJpegAsync(localPath);
        return transcodedPath ?? localPath;
    }

    // HEICs downloaded before transcoding existed only have the original file
    // on disk; convert them the first time they scroll into view. Failures
    // (e.g. missing HEIF codec) are remembered so the file-chip fallback does
    // not retry on every refresh.
    private void QueueHeicBackfillTranscode(string messageGuid, string localPath)
    {
        if (!HeicImageTranscoder.IsHeicPath(localPath))
        {
            return;
        }

        lock (_stateLock)
        {
            if (!_activeOrFailedTranscodes.Add(localPath))
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            var transcodedPath = await HeicImageTranscoder.TryTranscodeToJpegAsync(localPath);
            if (transcodedPath is not null)
            {
                lock (_stateLock)
                {
                    _activeOrFailedTranscodes.Remove(localPath);
                }

                DownloadStateChanged?.Invoke(this, messageGuid);
            }
        });
    }

    private string GetLocalRoot(ImsgMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Guid))
        {
            throw new InvalidOperationException("Attachment cache path requires a message GUID.");
        }

        return Path.Combine(paths.Attachments, SanitizeFileName(message.Guid));
    }

    private static string SanitizeFileName(string? value)
    {
        var fallback = string.IsNullOrWhiteSpace(value) ? "attachment" : value;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fallback = fallback.Replace(invalid, '_');
        }

        return fallback;
    }

    private static bool IsAutoDownloadCandidate(ImsgAttachment attachment)
    {
        return AttachmentPresentationPolicy.IsActionable(attachment) ||
            AttachmentPresentationPolicy.IsPreviewPayload(attachment);
    }

    private static bool ShouldAskMacToMaterialize(ImsgAttachment attachment, Exception error)
    {
        if (string.IsNullOrWhiteSpace(attachment.RemotePath))
        {
            return false;
        }

        if (attachment.Missing)
        {
            return true;
        }

        var message = error.Message;
        return message.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCacheFileName(ImsgAttachment attachment)
    {
        if (!string.IsNullOrWhiteSpace(attachment.ConvertedPath))
        {
            var convertedName = Path.GetFileName(attachment.ConvertedPath);
            if (!string.IsNullOrWhiteSpace(convertedName))
            {
                return convertedName;
            }
        }

        if (!string.Equals(attachment.DisplayName, "Attachment", StringComparison.OrdinalIgnoreCase))
        {
            var displayName = attachment.DisplayName;
            if (!string.IsNullOrWhiteSpace(attachment.ConvertedMimeType))
            {
                var convertedExtension = ExtensionForMime(attachment.ConvertedMimeType);
                if (!string.IsNullOrWhiteSpace(convertedExtension) &&
                    !displayName.EndsWith(convertedExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.ChangeExtension(displayName, convertedExtension);
                }
            }

            return displayName;
        }

        var remoteName = Path.GetFileName(attachment.RemotePath);
        if (!string.IsNullOrWhiteSpace(remoteName) &&
            !remoteName.Equals("pluginPayloadAttachment", StringComparison.OrdinalIgnoreCase) &&
            !remoteName.EndsWith(".pluginPayloadAttachment", StringComparison.OrdinalIgnoreCase))
        {
            return remoteName;
        }

        var extension = ExtensionForMime(attachment.ConvertedMimeType ?? attachment.MimeType);
        var key = attachment.RemotePath ?? attachment.OriginalPath ?? attachment.Path ?? attachment.ConvertedPath ?? "attachment";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
        return $"attachment-{hash}{extension}";
    }

    private static string ExtensionForMime(string? mimeType)
    {
        return mimeType?.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "audio/mpeg" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            _ => string.Empty
        };
    }

    private async Task DownloadQueuedAttachmentAsync(
        ImsgBridgeSettings settings,
        AttachmentDownloadCandidate candidate,
        Action<Exception>? onFailure)
    {
        try
        {
            await _downloadGate.WaitAsync();
            try
            {
                using var timeout = new CancellationTokenSource(settings.RequestTimeout);
                await DownloadAsync(
                    settings,
                    candidate.Message,
                    candidate.Attachment,
                    notifySuccess: true,
                    notifyFailure: true,
                    cancellationToken: timeout.Token);
            }
            finally
            {
                _downloadGate.Release();
            }
        }
        catch (Exception ex)
        {
            onFailure?.Invoke(ex);
        }
        finally
        {
            lock (_stateLock)
            {
                _queuedDownloads.Remove(candidate.QueueKey);
            }
        }
    }

    private sealed record AttachmentDownloadCandidate(ImsgMessage Message, ImsgAttachment Attachment, string QueueKey);
}
