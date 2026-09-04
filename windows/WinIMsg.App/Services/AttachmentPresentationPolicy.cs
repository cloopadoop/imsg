using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public enum AttachmentPresentationKind
{
    File,
    Image,
    Audio,
    Video
}

public static class AttachmentPresentationPolicy
{
    private static readonly string[] ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff"
    ];

    private static readonly string[] AudioExtensions =
    [
        ".m4a", ".mp3", ".wav", ".aac", ".flac", ".ogg", ".opus"
    ];

    private static readonly string[] VideoExtensions =
    [
        ".mp4", ".mov", ".m4v", ".webm", ".avi", ".wmv", ".mkv"
    ];

    public static bool IsDisplayable(ImsgAttachment attachment)
    {
        return !IsMessagesPluginPayload(attachment) &&
            (!string.IsNullOrWhiteSpace(attachment.RemotePath) ||
             !string.Equals(attachment.DisplayName, "Attachment", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsActionable(ImsgAttachment attachment)
    {
        return IsDisplayable(attachment) && !string.IsNullOrWhiteSpace(attachment.RemotePath);
    }

    public static bool IsPreviewPayload(ImsgAttachment attachment)
    {
        return !attachment.Missing &&
            IsMessagesPluginPayload(attachment) &&
            !string.IsNullOrWhiteSpace(attachment.RemotePath);
    }

    public static AttachmentPresentationKind Classify(ImsgAttachment attachment)
    {
        return Classify(attachment, localPath: null);
    }

    public static AttachmentPresentationKind Classify(ImsgAttachment attachment, string? localPath)
    {
        var mimeType = attachment.ConvertedMimeType ?? attachment.MimeType;
        if (LooksLikeUnsupportedWindowsMedia(attachment, localPath))
        {
            return AttachmentPresentationKind.File;
        }

        if (StartsWith(mimeType, "image/") ||
            UtiContains(attachment, "image") ||
            ExtensionIn(attachment, ImageExtensions) ||
            LooksLikeMessagesImageName(attachment) ||
            IsImageBySignature(localPath))
        {
            return AttachmentPresentationKind.Image;
        }

        if (StartsWith(mimeType, "audio/") ||
            UtiContains(attachment, "audio") ||
            ExtensionIn(attachment, AudioExtensions) ||
            IsAudioBySignature(localPath))
        {
            return AttachmentPresentationKind.Audio;
        }

        if (StartsWith(mimeType, "video/") ||
            UtiContains(attachment, "movie") ||
            UtiContains(attachment, "video") ||
            ExtensionIn(attachment, VideoExtensions) ||
            IsVideoBySignature(localPath))
        {
            return AttachmentPresentationKind.Video;
        }

        return AttachmentPresentationKind.File;
    }

    public static AttachmentPresentationKind ClassifyLocalFile(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return AttachmentPresentationKind.File;
        }

        if (ExtensionIs(localPath, ".caf"))
        {
            return AttachmentPresentationKind.File;
        }

        var extension = Path.GetExtension(localPath.Trim().Trim('"', '\''));
        if (!string.IsNullOrWhiteSpace(extension))
        {
            if (ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return AttachmentPresentationKind.Image;
            }

            if (AudioExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return AttachmentPresentationKind.Audio;
            }

            if (VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return AttachmentPresentationKind.Video;
            }
        }

        if (IsImageBySignature(localPath))
        {
            return AttachmentPresentationKind.Image;
        }

        if (IsAudioBySignature(localPath))
        {
            return AttachmentPresentationKind.Audio;
        }

        return IsVideoBySignature(localPath)
            ? AttachmentPresentationKind.Video
            : AttachmentPresentationKind.File;
    }

    private static bool LooksLikeUnsupportedWindowsMedia(ImsgAttachment attachment, string? localPath)
    {
        if (HasPlayableConvertedMedia(attachment, localPath))
        {
            return false;
        }

        // A locally transcoded, decodable image (e.g. a HEIC converted to
        // JPEG after download) supersedes unsupported-source metadata.
        if (HasDecodableLocalImage(localPath))
        {
            return false;
        }

        return ExtensionIs(attachment, ".caf") ||
            ExtensionIs(localPath, ".caf") ||
            ExtensionIs(attachment, ".heic") ||
            ExtensionIs(localPath, ".heic") ||
            ExtensionIs(attachment, ".heif") ||
            ExtensionIs(localPath, ".heif") ||
            ContainsToken(attachment.MimeType, "caf") ||
            ContainsToken(attachment.ConvertedMimeType, "caf") ||
            ContainsToken(attachment.MimeType, "heic") ||
            ContainsToken(attachment.ConvertedMimeType, "heic") ||
            ContainsToken(attachment.MimeType, "heif") ||
            ContainsToken(attachment.ConvertedMimeType, "heif") ||
            ContainsToken(attachment.Uti, "coreaudio") ||
            ContainsToken(attachment.Uti, "heic") ||
            ContainsToken(attachment.Uti, "heif");
    }

    private static bool HasDecodableLocalImage(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return false;
        }

        var extension = Path.GetExtension(localPath.Trim().Trim('"', '\''));
        return !string.IsNullOrWhiteSpace(extension) &&
            ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasPlayableConvertedMedia(ImsgAttachment attachment, string? localPath)
    {
        return (!string.IsNullOrWhiteSpace(attachment.ConvertedPath) ||
                (!string.IsNullOrWhiteSpace(localPath) && !ExtensionIs(localPath, ".caf"))) &&
            (StartsWith(attachment.ConvertedMimeType, "audio/") ||
             StartsWith(attachment.ConvertedMimeType, "video/") ||
             ExtensionIs(attachment.ConvertedPath, ".m4a") ||
             ExtensionIs(localPath, ".m4a") ||
             ExtensionIs(attachment.ConvertedPath, ".mp4") ||
             ExtensionIs(localPath, ".mp4"));
    }

    private static bool StartsWith(string? value, string prefix)
    {
        return value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool UtiContains(ImsgAttachment attachment, string value)
    {
        return attachment.Uti?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool ExtensionIn(ImsgAttachment attachment, IReadOnlyCollection<string> extensions)
    {
        var name = attachment.DisplayName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var extension = Path.GetExtension(name);
        return !string.IsNullOrWhiteSpace(extension) &&
            extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool ExtensionIs(ImsgAttachment attachment, string extension)
    {
        return ExtensionIs(attachment.DisplayName, extension) ||
            ExtensionIs(attachment.TransferName, extension) ||
            ExtensionIs(attachment.Filename, extension) ||
            ExtensionIs(attachment.OriginalPath, extension) ||
            ExtensionIs(attachment.Path, extension) ||
            ExtensionIs(attachment.ConvertedPath, extension);
    }

    private static bool ExtensionIs(string? value, string extension)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            string.Equals(Path.GetExtension(value.Trim().Trim('"', '\'')), extension, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsToken(string? value, string token)
    {
        return value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool LooksLikeMessagesImageName(ImsgAttachment attachment)
    {
        return ContainsImageName(attachment.DisplayName) ||
            ContainsImageName(attachment.TransferName) ||
            ContainsImageName(attachment.Filename);
    }

    private static bool ContainsImageName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            (value.Contains("GroupPhotoImage", StringComparison.OrdinalIgnoreCase) ||
             value.Contains("Image", StringComparison.OrdinalIgnoreCase) ||
             value.Contains("Photo", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsImageBySignature(string? localPath)
    {
        var header = ReadHeader(localPath, 16);
        return header.Length >= 12 &&
            ((header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) ||
             (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) ||
             (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46) ||
             (header[0] == 0x42 && header[1] == 0x4D) ||
             IsIsoBaseMedia(header, "mif1") ||
             IsIsoBaseMedia(header, "msf1"));
    }

    private static bool IsAudioBySignature(string? localPath)
    {
        var header = ReadHeader(localPath, 16);
        return header.Length >= 12 &&
            ((header[0] == 0x49 && header[1] == 0x44 && header[2] == 0x33) ||
             (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
              header[8] == 0x57 && header[9] == 0x41 && header[10] == 0x56 && header[11] == 0x45) ||
             IsIsoBaseMedia(header, "M4A "));
    }

    private static bool IsVideoBySignature(string? localPath)
    {
        var header = ReadHeader(localPath, 16);
        return header.Length >= 12 &&
            (IsIsoBaseMedia(header, "mp42") ||
             IsIsoBaseMedia(header, "mp41") ||
             IsIsoBaseMedia(header, "isom") ||
             IsIsoBaseMedia(header, "qt  "));
    }

    private static bool IsIsoBaseMedia(ReadOnlySpan<byte> header, string brand)
    {
        return header.Length >= 12 &&
            header[4] == 0x66 &&
            header[5] == 0x74 &&
            header[6] == 0x79 &&
            header[7] == 0x70 &&
            header[8] == brand[0] &&
            header[9] == brand[1] &&
            header[10] == brand[2] &&
            header[11] == brand[3];
    }

    private static byte[] ReadHeader(string? localPath, int count)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            return [];
        }

        try
        {
            using var stream = File.OpenRead(localPath);
            var buffer = new byte[count];
            var read = stream.Read(buffer, 0, buffer.Length);
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch
        {
            return [];
        }
    }

    public static bool IsMessagesPluginPayload(ImsgAttachment attachment)
    {
        return IsMessagesPluginPayloadName(attachment.Filename) ||
            IsMessagesPluginPayloadName(attachment.TransferName) ||
            IsMessagesPluginPayloadName(attachment.OriginalPath) ||
            IsMessagesPluginPayloadName(attachment.Path) ||
            IsMessagesPluginPayloadName(attachment.ConvertedPath);
    }

    private static bool IsMessagesPluginPayloadName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var name = value.Trim().Trim('"', '\'');
        var separatorIndex = name.LastIndexOfAny(['/', '\\']);
        if (separatorIndex >= 0 && separatorIndex < name.Length - 1)
        {
            name = name[(separatorIndex + 1)..];
        }

        return name.Equals("pluginPayloadAttachment", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".pluginPayloadAttachment", StringComparison.OrdinalIgnoreCase);
    }
}
