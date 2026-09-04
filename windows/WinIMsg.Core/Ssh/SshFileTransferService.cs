using System.Diagnostics;
using System.Text;
using WinIMsg.Core.Models;

namespace WinIMsg.Core.Ssh;

public sealed record FileTransferProgress(
    string Direction,
    string Source,
    string Destination,
    long? BytesTransferred,
    long? TotalBytes,
    bool IsComplete)
{
    public double? Percent => TotalBytes is > 0 && BytesTransferred is >= 0
        ? Math.Clamp((double)BytesTransferred.Value / TotalBytes.Value * 100, 0, 100)
        : null;
}

public sealed class SshFileTransferService
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly SshCommandRunner _commandRunner;

    public SshFileTransferService(SshCommandRunner? commandRunner = null)
    {
        _commandRunner = commandRunner ?? new SshCommandRunner();
    }

    public Task<string> UploadAttachmentAsync(
        ImsgBridgeSettings settings,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        return UploadAttachmentAsync(settings, localPath, progress: null, cancellationToken);
    }

    public async Task<string> UploadAttachmentAsync(
        ImsgBridgeSettings settings,
        string localPath,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var normalized = settings.Normalize();
        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException("Attachment file was not found.", localPath);
        }

        var remoteDirectory = $"{normalized.RemoteAttachmentRoot.TrimEnd('/')}/{Guid.NewGuid():N}";
        var mkdir = await _commandRunner.RunShellCommandAsync(
            normalized,
            BuildUploadDirectoryCommand(remoteDirectory),
            cancellationToken);
        if (!mkdir.Succeeded)
        {
            throw new InvalidOperationException($"Unable to create remote attachment directory: {mkdir.ErrorSummary}");
        }

        var resolvedRemoteDirectory = mkdir.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(resolvedRemoteDirectory))
        {
            throw new InvalidOperationException("Unable to create remote attachment directory: no remote path was returned.");
        }

        var remotePath = $"{resolvedRemoteDirectory.TrimEnd('/')}/{Path.GetFileName(localPath)}";
        var result = await RunSftpBatchAsync(
            normalized,
            [BuildSftpPutCommand(localPath, remotePath)],
            new TransferProgressProbe(
                Direction: "upload",
                Source: localPath,
                Destination: remotePath,
                TotalBytes: new FileInfo(localPath).Length,
                BytesTransferred: () => null),
            progress,
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Unable to upload attachment: {result.ErrorSummary}");
        }

        return remotePath;
    }

    internal static string BuildUploadDirectoryCommand(string remoteDirectory) =>
        "remote_dir=" + QuoteShellLiteral(remoteDirectory) + "; " +
        "case \"$remote_dir\" in \\~) remote_dir=\"$(cd ~ && pwd -P)\";; \\~/*) home_dir=\"$(cd ~ && pwd -P)\" && remote_dir=\"$home_dir/${remote_dir#~/}\";; esac; " +
        "[ -n \"$remote_dir\" ] && mkdir -p \"$remote_dir\" && cd \"$remote_dir\" && pwd -P";

    public async Task DeleteUploadedAttachmentAsync(
        ImsgBridgeSettings settings,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return;
        }

        var result = await _commandRunner.RunShellCommandAsync(
            settings.Normalize(),
            BuildDeleteUploadedAttachmentCommand(remotePath),
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Unable to clean up remote attachment: {result.ErrorSummary}");
        }
    }

    internal static string BuildDeleteUploadedAttachmentCommand(string remotePath) =>
        "remote_file=" + QuoteShellLiteral(remotePath) + "; " +
        "remote_dir=$(dirname \"$remote_file\"); " +
        "rm -f \"$remote_file\" && rmdir \"$remote_dir\" 2>/dev/null || true";

    public Task<string> DownloadAttachmentAsync(
        ImsgBridgeSettings settings,
        string remotePath,
        string localRoot,
        string? suggestedFileName = null,
        CancellationToken cancellationToken = default)
    {
        return DownloadAttachmentAsync(settings, remotePath, localRoot, suggestedFileName, progress: null, cancellationToken);
    }

    public async Task<string> DownloadAttachmentAsync(
        ImsgBridgeSettings settings,
        string remotePath,
        string localRoot,
        string? suggestedFileName,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(localRoot);
        var fileName = SanitizeFileName(suggestedFileName ?? Path.GetFileName(remotePath));
        var localPath = Path.Combine(localRoot, fileName);

        ProcessResult? lastResult = null;
        foreach (var candidateRemotePath in GetDownloadRemotePathCandidates(remotePath))
        {
            var totalBytes = await TryGetRemoteFileSizeAsync(settings, candidateRemotePath, cancellationToken);
            var result = await RunSftpBatchAsync(
                settings,
                [BuildSftpGetCommand(candidateRemotePath, localPath)],
                new TransferProgressProbe(
                    Direction: "download",
                    Source: candidateRemotePath,
                    Destination: localPath,
                    TotalBytes: totalBytes,
                    BytesTransferred: () => File.Exists(localPath) ? new FileInfo(localPath).Length : 0),
                progress,
                cancellationToken);
            if (result.Succeeded)
            {
                return localPath;
            }

            lastResult = result;
        }

        throw new InvalidOperationException($"Unable to download attachment: {lastResult?.ErrorSummary ?? "sftp failed"}");
    }

    internal static string BuildScpRemoteTarget(ImsgBridgeSettings settings, string remotePath) => $"{settings.SshTarget}:{remotePath}";

    internal static string BuildSftpPutCommand(string localPath, string remotePath) =>
        $"put {QuoteSftpPath(LocalSftpPath(localPath))} {QuoteSftpPath(remotePath)}";

    internal static string BuildSftpGetCommand(string remotePath, string localPath) =>
        $"get {QuoteSftpPath(remotePath)} {QuoteSftpPath(LocalSftpPath(localPath))}";

    internal static IReadOnlyList<string> GetDownloadRemotePathCandidates(string remotePath)
    {
        var candidates = new List<string> { remotePath };
        var repaired = RepairUtf8Mojibake(remotePath.Normalize(NormalizationForm.FormC));
        repaired = ReplaceKnownMojibake(repaired);
        if (!string.Equals(remotePath, repaired, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(repaired))
        {
            candidates.Add(repaired);
        }

        return candidates;
    }

    private async Task<long?> TryGetRemoteFileSizeAsync(
        ImsgBridgeSettings settings,
        string remotePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var quoted = QuoteRemotePath(remotePath);
            var command = $"(stat -f %z {quoted} 2>/dev/null || stat -c %s {quoted} 2>/dev/null) | head -n 1";
            var result = await _commandRunner.RunShellCommandAsync(settings, command, cancellationToken);
            if (!result.Succeeded)
            {
                return null;
            }

            return long.TryParse(result.StandardOutput.Trim(), out var bytes) && bytes >= 0 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ProcessResult> RunSftpBatchAsync(
        ImsgBridgeSettings settings,
        IReadOnlyList<string> commands,
        TransferProgressProbe progressProbe,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var normalized = settings.Normalize();
        var startInfo = new ProcessStartInfo
        {
            FileName = SshProcessFactory.ResolveSftpExecutable(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-b");
        startInfo.ArgumentList.Add("-");
        startInfo.ArgumentList.Add("-P");
        startInfo.ArgumentList.Add(normalized.SshPort.ToString());
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("BatchMode=yes");

        if (!string.IsNullOrWhiteSpace(normalized.IdentityFile))
        {
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(normalized.IdentityFile);
        }

        startInfo.ArgumentList.Add(normalized.SshTarget);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        foreach (var command in commands)
        {
            await process.StandardInput.WriteLineAsync(command);
        }

        await process.StandardInput.WriteLineAsync("bye");
        await process.StandardInput.FlushAsync();
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.RequestTimeout);
        using var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var progressTask = ReportTransferProgressAsync(progressProbe, progress, progressCancellation.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await StopProgressAsync(progressCancellation, progressTask);
            progress?.Report(progressProbe.Snapshot(isComplete: process.ExitCode == 0));
            return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask, false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await StopProgressAsync(progressCancellation, progressTask);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new ProcessResult(null, string.Empty, string.Empty, true);
        }
    }

    private static async Task ReportTransferProgressAsync(
        TransferProgressProbe probe,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            return;
        }

        progress.Report(probe.Snapshot(isComplete: false));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                progress.Report(probe.Snapshot(isComplete: false));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task StopProgressAsync(CancellationTokenSource cancellation, Task progressTask)
    {
        cancellation.Cancel();
        try
        {
            await progressTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Best effort cleanup after process exit or timeout.
        }
    }

    private static string LocalSftpPath(string path) => Path.GetFullPath(path).Replace('\\', '/');

    private static string QuoteSftpPath(string path) => "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string QuoteRemotePath(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return "~/" + QuotePathFragment(path[2..]);
        }

        return QuotePathFragment(path);
    }

    private static string QuotePathFragment(string path) => QuoteShellLiteral(path);

    private static string QuoteShellLiteral(string path) => "'" + path.Replace("'", "'\\''") + "'";

    private static string SanitizeFileName(string? value)
    {
        var fallback = string.IsNullOrWhiteSpace(value) ? "attachment" : value;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fallback = fallback.Replace(invalid, '_');
        }

        return fallback;
    }

    private static string RepairUtf8Mojibake(string text)
    {
        if (!LooksLikeMojibake(text))
        {
            return text;
        }

        var bytes = new byte[text.Length];
        for (var index = 0; index < text.Length; index++)
        {
            if (!TryMapWindows1252Byte(text[index], out bytes[index]))
            {
                return text;
            }
        }

        try
        {
            var repaired = StrictUtf8.GetString(bytes).Normalize(NormalizationForm.FormC);
            return MojibakeScore(repaired) < MojibakeScore(text) ? repaired : text;
        }
        catch (DecoderFallbackException)
        {
            return text;
        }
    }

    private static string ReplaceKnownMojibake(string text)
    {
        return text
            .Replace("\u00C2\u00A0", " ")
            .Replace("\u00E2\u20AC\u2122", "'")
            .Replace("\u00E2\u20AC\u02DC", "'")
            .Replace("\u00E2\u20AC\u0153", "\"")
            .Replace("\u00E2\u20AC\u009D", "\"")
            .Replace("\u00E2\u20AC\u201D", "-")
            .Replace("\u00E2\u20AC\u201C", "-")
            .Replace("\u00E2\u20AC\u00A6", "...")
            .Replace("\u00E2\u20AC\u00A2", "*")
            .Replace("\u00E2\u20AC\u0160", " ")
            .Replace("\uFFFC", string.Empty)
            .Replace("\uFFFD", string.Empty);
    }

    private static bool LooksLikeMojibake(string text)
    {
        return text.Contains('\u00C2') ||
            text.Contains('\u00C3') ||
            text.Contains('\u00E2') ||
            text.Contains('\u00EF') ||
            text.Contains('\u00F0') ||
            text.Contains('\uFFFD');
    }

    private static int MojibakeScore(string text)
    {
        var score = 0;
        foreach (var character in text)
        {
            score += character switch
            {
                '\uFFFD' => 10,
                '\u00C2' or '\u00C3' or '\u00E2' or '\u00EF' or '\u00F0' => 4,
                '\u20AC' or '\u2122' or '\u0153' or '\u009D' => 3,
                _ => char.IsControl(character) && character is not '\r' and not '\n' and not '\t' ? 2 : 0
            };
        }

        return score;
    }

    private static bool TryMapWindows1252Byte(char character, out byte value)
    {
        if (character <= '\u00FF')
        {
            value = (byte)character;
            return true;
        }

        value = character switch
        {
            '\u20AC' => 0x80,
            '\u201A' => 0x82,
            '\u0192' => 0x83,
            '\u201E' => 0x84,
            '\u2026' => 0x85,
            '\u2020' => 0x86,
            '\u2021' => 0x87,
            '\u02C6' => 0x88,
            '\u2030' => 0x89,
            '\u0160' => 0x8A,
            '\u2039' => 0x8B,
            '\u0152' => 0x8C,
            '\u017D' => 0x8E,
            '\u2018' => 0x91,
            '\u2019' => 0x92,
            '\u201C' => 0x93,
            '\u201D' => 0x94,
            '\u2022' => 0x95,
            '\u2013' => 0x96,
            '\u2014' => 0x97,
            '\u02DC' => 0x98,
            '\u2122' => 0x99,
            '\u0161' => 0x9A,
            '\u203A' => 0x9B,
            '\u0153' => 0x9C,
            '\u017E' => 0x9E,
            '\u0178' => 0x9F,
            _ => 0
        };

        return value != 0;
    }

    private sealed record TransferProgressProbe(
        string Direction,
        string Source,
        string Destination,
        long? TotalBytes,
        Func<long?> BytesTransferred)
    {
        public FileTransferProgress Snapshot(bool isComplete)
        {
            var bytesTransferred = BytesTransferred();
            if (isComplete && TotalBytes is > 0)
            {
                bytesTransferred = TotalBytes;
            }
            else if (bytesTransferred is not null && TotalBytes is not null)
            {
                bytesTransferred = Math.Clamp(bytesTransferred.Value, 0, TotalBytes.Value);
            }

            return new FileTransferProgress(Direction, Source, Destination, bytesTransferred, TotalBytes, isComplete);
        }
    }
}
