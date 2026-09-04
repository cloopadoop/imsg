using System.Text.RegularExpressions;
using WinIMsg.App.ViewModels;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public static class LinkPreviewPolicy
{
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s<>()]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static MessageLinkPreviewItem? Extract(string? text)
    {
        return Extract(text, previewImagePath: null);
    }

    public static MessageLinkPreviewItem? Extract(
        ImsgMessage message,
        Func<ImsgMessage, ImsgAttachment, string>? attachmentLocalPathResolver)
    {
        var previewImagePath = FindPreviewImagePath(message, attachmentLocalPathResolver);
        return Extract(message.Text, previewImagePath);
    }

    private static MessageLinkPreviewItem? Extract(string? text, string? previewImagePath)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = UrlRegex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var rawUrl = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']');
        return Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
            ? MessageLinkPreviewItem.From(uri, previewImagePath)
            : null;
    }

    private static string? FindPreviewImagePath(
        ImsgMessage message,
        Func<ImsgMessage, ImsgAttachment, string>? attachmentLocalPathResolver)
    {
        if (attachmentLocalPathResolver is null)
        {
            return null;
        }

        foreach (var attachment in message.Attachments.Where(AttachmentPresentationPolicy.IsPreviewPayload))
        {
            var localPath = attachmentLocalPathResolver(message, attachment);
            if (!File.Exists(localPath))
            {
                continue;
            }

            if (AttachmentPresentationPolicy.Classify(attachment, localPath) == AttachmentPresentationKind.Image)
            {
                return localPath;
            }
        }

        return null;
    }
}
