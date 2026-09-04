using System.Globalization;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace WinIMsg.App.Services;

/// <summary>
/// Records microphone audio to an M4A/AAC file that the existing attachment
/// send path can deliver as an iMessage audio attachment.
/// </summary>
public sealed class VoiceRecordingService : IDisposable
{
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromMilliseconds(600);

    private MediaCapture? _capture;
    private LowLagMediaRecording? _recording;
    private string? _currentFilePath;

    public bool IsRecording => _recording is not null;

    public string? CurrentFilePath => _currentFilePath;

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public TimeSpan Elapsed => StartedAtUtc is { } started
        ? DateTimeOffset.UtcNow - started
        : TimeSpan.Zero;

    public static string BuildRecordingFilePath(string directory, DateTimeOffset localTimestamp) =>
        Path.Combine(
            directory,
            $"Voice Message {localTimestamp.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)}.m4a");

    public static bool IsTooShort(TimeSpan elapsed) => elapsed < MinimumDuration;

    public static string FormatElapsed(TimeSpan elapsed)
    {
        var clamped = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        return clamped.TotalHours >= 1
            ? clamped.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : clamped.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    public async Task StartAsync(string filePath)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("A voice recording is already in progress.");
        }

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Recording path must include a directory.", nameof(filePath));
        }

        Directory.CreateDirectory(directory);
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio,
                MediaCategory = MediaCategory.Speech
            });
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            var file = await folder.CreateFileAsync(
                Path.GetFileName(filePath),
                CreationCollisionOption.ReplaceExisting);
            var recording = await capture.PrepareLowLagRecordToStorageFileAsync(
                MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Auto),
                file);
            await recording.StartAsync();

            _capture = capture;
            _recording = recording;
            _currentFilePath = file.Path;
            StartedAtUtc = DateTimeOffset.UtcNow;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Stops the active recording. Returns the finished file path, or null when
    /// there was no active recording or the caller asked to discard it.
    /// </summary>
    public async Task<string?> StopAsync(bool discard)
    {
        var recording = _recording;
        var capture = _capture;
        var filePath = _currentFilePath;
        _recording = null;
        _capture = null;
        _currentFilePath = null;
        StartedAtUtc = null;

        if (recording is null)
        {
            return null;
        }

        try
        {
            await recording.StopAsync();
            await recording.FinishAsync();
        }
        finally
        {
            capture?.Dispose();
        }

        if (discard)
        {
            if (filePath is not null)
            {
                try
                {
                    File.Delete(filePath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return null;
        }

        return filePath;
    }

    public void Dispose()
    {
        if (IsRecording)
        {
            _ = StopAsync(discard: true);
        }
    }
}
