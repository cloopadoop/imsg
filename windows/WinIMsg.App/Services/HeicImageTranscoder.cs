using Windows.Graphics.Imaging;

namespace WinIMsg.App.Services;

/// <summary>
/// Converts downloaded HEIC/HEIF attachments to JPEG so they render inline.
/// Upstream imsg only converts CAF and GIF, and XAML cannot be trusted to
/// decode HEIC directly, so the transcode happens Windows-side after download
/// using the OS imaging stack (requires the HEIF Image Extensions codec;
/// failure falls back to the file-chip presentation).
/// </summary>
public static class HeicImageTranscoder
{
    public static bool IsHeicPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path.Trim().Trim('"', '\''));
        return string.Equals(extension, ".heic", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".heif", StringComparison.OrdinalIgnoreCase);
    }

    // IMG_5127.HEIC -> IMG_5127.HEIC.jpg, so the original stays available and
    // the derived name can never collide with a real sibling attachment.
    public static string GetTranscodedPath(string heicPath) => heicPath + ".jpg";

    public static async Task<string?> TryTranscodeToJpegAsync(string heicPath)
    {
        var outputPath = GetTranscodedPath(heicPath);
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        var temporaryPath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var input = File.OpenRead(heicPath))
            {
                var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
                using var bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);
                using var output = File.Create(temporaryPath);
                var encoder = await BitmapEncoder.CreateAsync(
                    BitmapEncoder.JpegEncoderId,
                    output.AsRandomAccessStream());
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
            return outputPath;
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return null;
        }
    }
}
