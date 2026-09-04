using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinIMsg.App.ViewModels;

public sealed record MessageLinkPreviewItem(
    Uri Uri,
    string Title,
    string Subtitle,
    string DisplayUrl,
    string? PreviewImagePath = null)
{
    public static MessageLinkPreviewItem From(Uri uri)
    {
        return From(uri, previewImagePath: null);
    }

    public static MessageLinkPreviewItem From(Uri uri, string? previewImagePath)
    {
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;

        var title = string.IsNullOrWhiteSpace(host)
            ? uri.ToString()
            : host;

        var path = uri.AbsolutePath.Trim('/');
        var subtitle = string.IsNullOrWhiteSpace(path)
            ? uri.Scheme
            : path.Replace('-', ' ').Replace('_', ' ');

        return new MessageLinkPreviewItem(uri, title, subtitle, uri.ToString(), previewImagePath);
    }

    public Visibility PreviewImageVisibility =>
        !string.IsNullOrWhiteSpace(PreviewImagePath) && File.Exists(PreviewImagePath)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility FallbackIconVisibility => PreviewImageVisibility == Visibility.Visible
        ? Visibility.Collapsed
        : Visibility.Visible;

    public BitmapImage? PreviewImageSource =>
        PreviewImageVisibility == Visibility.Visible && PreviewImagePath is { Length: > 0 } path
            ? new BitmapImage(new Uri(path))
            : null;
}
