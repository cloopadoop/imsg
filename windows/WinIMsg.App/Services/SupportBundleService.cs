using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using WinIMsg.Core;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public sealed record SupportBundleExportRequest(
    WinIMsgSettings Settings,
    ImsgBridgeSettings BridgeSettings,
    ImsgCapabilities Capabilities,
    string? SetupChecklistText = null,
    string? ContactDiagnosticsText = null,
    string? CacheSyncStatusText = null,
    string? StatusText = null);

public sealed record SupportBundleResult(
    string BundlePath,
    IReadOnlyList<string> Entries);

public sealed class SupportBundleService
{
    private const int MaxLogBytes = 512 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly Regex EmailRegex = new(
        @"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PhoneRegex = new(
        @"(?<![\w])\+?\d[\d .()\-]{6,}\d(?![\w])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Ipv4Regex = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WindowsUserPathRegex = new(
        @"\b[A-Z]:\\Users\\[^\\\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly AppDataPaths _paths;
    private readonly AppLogService _log;

    public SupportBundleService(AppDataPaths paths, AppLogService log)
    {
        _paths = paths;
        _log = log;
    }

    public async Task<SupportBundleResult> ExportAsync(
        SupportBundleExportRequest request,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        Directory.CreateDirectory(_paths.SupportBundles);

        var normalizedSettings = request.Settings.Normalize();
        var normalizedBridge = request.BridgeSettings.Normalize();
        var sensitiveValues = BuildSensitiveValues(normalizedSettings, normalizedBridge);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var bundlePath = Path.Combine(_paths.SupportBundles, $"win-imsg-support-{timestamp}.zip");

        var entryNames = new List<string>();
        using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Create);
        AddJson(archive, entryNames, "manifest.json", BuildManifest(normalizedSettings, normalizedBridge, request, sensitiveValues));
        AddJson(archive, entryNames, "settings.redacted.json", BuildRedactedSettings(normalizedSettings, normalizedBridge));
        AddJson(archive, entryNames, "diagnostics/cache.json", await BuildCacheDiagnosticsAsync(cancellationToken));
        AddJson(archive, entryNames, "diagnostics/log-files.json", BuildLogFileIndex(sensitiveValues));
        AddJson(archive, entryNames, "diagnostics/capabilities.json", BuildCapabilitiesSummary(request.Capabilities));
        AddText(archive, entryNames, "diagnostics/ui-status.txt", BuildUiStatusText(request, sensitiveValues));
        AddText(archive, entryNames, "logs/win-imsg.redacted.log", await ReadRedactedLogTailAsync(sensitiveValues, cancellationToken));

        _log.Info($"Support bundle exported. path={bundlePath}; entries={entryNames.Count}");
        return new SupportBundleResult(bundlePath, entryNames);
    }

    public static string RedactContent(string? content, IEnumerable<string>? sensitiveValues = null)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var redacted = WindowsUserPathRegex.Replace(content, @"C:\Users\<redacted>");
        redacted = EmailRegex.Replace(redacted, "<redacted-email>");
        redacted = Ipv4Regex.Replace(redacted, "<redacted-ip>");
        redacted = PhoneRegex.Replace(redacted, "<redacted-phone>");
        foreach (var value in (sensitiveValues ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .OrderByDescending(value => value.Length))
        {
            var trimmed = value.Trim();
            if (trimmed.Length < 3 || IsCommonNonSensitiveToken(trimmed))
            {
                continue;
            }

            redacted = Regex.Replace(
                redacted,
                Regex.Escape(trimmed),
                ClassifySensitiveValue(trimmed),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200));
        }

        return redacted;
    }

    private object BuildManifest(
        WinIMsgSettings settings,
        ImsgBridgeSettings bridgeSettings,
        SupportBundleExportRequest request,
        IReadOnlyList<string> sensitiveValues)
    {
        var assembly = typeof(SupportBundleService).Assembly.GetName();
        return new
        {
            generatedAt = DateTimeOffset.Now,
            app = new
            {
                name = AppIdentityService.DisplayName,
                appUserModelId = AppIdentityService.AppUserModelId,
                assemblyVersion = assembly.Version?.ToString(),
                informationalVersion = typeof(SupportBundleService)
                    .Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
            },
            runtime = new
            {
                os = Environment.OSVersion.VersionString,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                is64BitProcess = Environment.Is64BitProcess,
                machineName = "<redacted-machine>",
                userName = "<redacted-user>"
            },
            state = new
            {
                connected = request.StatusText?.Contains("Connected", StringComparison.OrdinalIgnoreCase) == true,
                status = RedactContent(request.StatusText, sensitiveValues),
                activeProfileIndex = ActiveProfileIndex(settings),
                profileCount = settings.Profiles.Count
            },
            paths = new
            {
                root = RedactContent(_paths.Root, sensitiveValues),
                logs = RedactContent(_paths.Logs, sensitiveValues),
                attachments = RedactContent(_paths.Attachments, sensitiveValues),
                supportBundles = RedactContent(_paths.SupportBundles, sensitiveValues),
                settings = RedactContent(_paths.SettingsPath, sensitiveValues),
                database = RedactContent(_paths.DatabasePath, sensitiveValues)
            },
            settings = new
            {
                remoteAccessMode = bridgeSettings.RemoteAccessMode,
                targetCount = bridgeSettings.CandidateAddresses.Count,
                targetKinds = bridgeSettings.CandidateAddresses.Select(ClassifyTarget).ToList(),
                requestTimeoutSeconds = bridgeSettings.RequestTimeoutSeconds,
                startWithWindows = settings.StartWithWindows,
                minimizeToTray = settings.MinimizeToTray,
                enableNotifications = settings.EnableNotifications,
                showMessageContentInNotifications = settings.ShowMessageContentInNotifications,
                syncCacheInBackground = settings.SyncCacheInBackground,
                mergeChatsByParticipants = settings.MergeChatsByParticipants,
                phoneNumberRegion = settings.PhoneNumberRegion,
                pinnedAwayFromLatestCount = settings.ChatsPinnedAwayFromLatest.Count
            },
            files = new
            {
                settings = FileSummary(_paths.SettingsPath, sensitiveValues),
                database = FileSummary(_paths.DatabasePath, sensitiveValues),
                log = FileSummary(_log.LogFilePath, sensitiveValues)
            }
        };
    }

    private static object BuildRedactedSettings(WinIMsgSettings settings, ImsgBridgeSettings bridgeSettings)
    {
        return new
        {
            activeProfileIndex = ActiveProfileIndex(settings),
            profileCount = settings.Profiles.Count,
            autoDownloadAttachments = settings.AutoDownloadAttachments,
            startWithWindows = settings.StartWithWindows,
            minimizeToTray = settings.MinimizeToTray,
            enableNotifications = settings.EnableNotifications,
            showMessageContentInNotifications = settings.ShowMessageContentInNotifications,
            syncCacheInBackground = settings.SyncCacheInBackground,
            mergeChatsByParticipants = settings.MergeChatsByParticipants,
            phoneNumberRegion = settings.PhoneNumberRegion,
            conversationListWidth = settings.ConversationListWidth,
            pinnedAwayFromLatestCount = settings.ChatsPinnedAwayFromLatest.Count,
            activeBridge = new
            {
                remoteAccessMode = bridgeSettings.RemoteAccessMode,
                targetCount = bridgeSettings.CandidateAddresses.Count,
                targetKinds = bridgeSettings.CandidateAddresses.Select(ClassifyTarget).ToList(),
                macUserPresent = !string.IsNullOrWhiteSpace(bridgeSettings.MacUser),
                sshPort = bridgeSettings.SshPort,
                identityFilePresent = !string.IsNullOrWhiteSpace(bridgeSettings.IdentityFile),
                imsgPathKind = ClassifyPathSetting(bridgeSettings.ImsgPath, "imsg"),
                remoteAttachmentRootPresent = !string.IsNullOrWhiteSpace(bridgeSettings.RemoteAttachmentRoot),
                requestTimeoutSeconds = bridgeSettings.RequestTimeoutSeconds,
                startWithWindows = bridgeSettings.StartWithWindows,
                minimizeToTray = bridgeSettings.MinimizeToTray
            },
            profiles = settings.Profiles
                .Select((profile, index) => profile.Normalize())
                .Select((profile, index) => new
                {
                    index,
                    isActive = string.Equals(profile.Id, settings.ActiveProfileId, StringComparison.OrdinalIgnoreCase),
                    name = $"<redacted-profile-name:{index + 1}>",
                    targetCount = profile.CandidateAddresses.Count,
                    targetKinds = profile.CandidateAddresses.Select(ClassifyTarget).ToList(),
                    macUserPresent = !string.IsNullOrWhiteSpace(profile.MacUser),
                    sshPort = profile.SshPort,
                    identityFilePresent = !string.IsNullOrWhiteSpace(profile.IdentityFile),
                    remoteAccessMode = profile.RemoteAccessMode,
                    imsgPathKind = ClassifyPathSetting(profile.ImsgPath, "imsg"),
                    remoteAttachmentRootPresent = !string.IsNullOrWhiteSpace(profile.RemoteAttachmentRoot),
                    requestTimeoutSeconds = profile.RequestTimeoutSeconds
                })
                .ToList()
        };
    }

    private async Task<object> BuildCacheDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var file = FileSummary(_paths.DatabasePath, []);
        if (!File.Exists(_paths.DatabasePath))
        {
            return new
            {
                file,
                tables = new Dictionary<string, long>(),
                error = "Cache database does not exist."
            };
        }

        var tables = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        string? error = null;
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _paths.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly
            };

            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken);
            foreach (var table in new[] { "chats", "messages", "chat_sync_state", "app_state", "message_search_fts", "chat_search_fts" })
            {
                tables[table] = await CountRowsAsync(connection, table, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        return new
        {
            file,
            tables,
            error
        };
    }

    private object BuildLogFileIndex(IReadOnlyList<string> sensitiveValues)
    {
        if (!Directory.Exists(_paths.Logs))
        {
            return new
            {
                directory = RedactContent(_paths.Logs, sensitiveValues),
                files = Array.Empty<object>()
            };
        }

        return new
        {
            directory = RedactContent(_paths.Logs, sensitiveValues),
            files = Directory
                .EnumerateFiles(_paths.Logs, "*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Select(path => FileSummary(path, sensitiveValues))
                .ToList()
        };
    }

    private static object BuildCapabilitiesSummary(ImsgCapabilities capabilities)
    {
        return new
        {
            version = capabilities.Version,
            message = capabilities.Message,
            hasAdvancedBridge = capabilities.HasAdvancedBridge,
            basicFeaturesAdvertised = capabilities.BasicFeaturesAdvertised,
            basicFeaturesEnabled = capabilities.BasicFeaturesEnabled,
            typingIndicators = capabilities.TypingIndicators,
            readReceipts = capabilities.ReadReceipts,
            bridgeVersion = capabilities.BridgeVersion,
            rpcMethodCount = capabilities.RpcMethods.Count,
            rpcMethods = capabilities.RpcMethods
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static string BuildUiStatusText(
        SupportBundleExportRequest request,
        IReadOnlyList<string> sensitiveValues)
    {
        var builder = new StringBuilder();
        AppendSection(builder, "Status", request.StatusText, sensitiveValues);
        AppendSection(builder, "Setup checklist", request.SetupChecklistText, sensitiveValues);
        AppendSection(builder, "Contacts", request.ContactDiagnosticsText, sensitiveValues);
        AppendSection(builder, "Cache sync", request.CacheSyncStatusText, sensitiveValues);
        return builder.ToString().TrimEnd();
    }

    private async Task<string> ReadRedactedLogTailAsync(
        IReadOnlyList<string> sensitiveValues,
        CancellationToken cancellationToken)
    {
        _log.EnsureLogFile();
        if (!File.Exists(_log.LogFilePath))
        {
            return "No win-imsg.log file exists.";
        }

        await using var stream = new FileStream(
            _log.LogFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxLogBytes)
        {
            stream.Seek(-MaxLogBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return RedactContent(text, sensitiveValues);
    }

    private static async Task<long> CountRowsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(1) FROM {table}";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value);
    }

    private static void AddJson(
        ZipArchive archive,
        ICollection<string> entryNames,
        string entryName,
        object value)
    {
        AddText(archive, entryNames, entryName, JsonSerializer.Serialize(value, JsonOptions));
    }

    private static void AddText(
        ZipArchive archive,
        ICollection<string> entryNames,
        string entryName,
        string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
        entryNames.Add(entryName);
    }

    private static void AppendSection(
        StringBuilder builder,
        string title,
        string? content,
        IReadOnlyList<string> sensitiveValues)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine(RedactContent(string.IsNullOrWhiteSpace(content) ? "(empty)" : content, sensitiveValues));
        builder.AppendLine();
    }

    private static object FileSummary(string path, IReadOnlyList<string> sensitiveValues)
    {
        var file = new FileInfo(path);
        return new
        {
            path = RedactContent(path, sensitiveValues),
            exists = file.Exists,
            length = file.Exists ? file.Length : 0,
            lastWriteTimeUtc = file.Exists ? file.LastWriteTimeUtc : (DateTime?)null
        };
    }

    private static List<string> BuildSensitiveValues(
        WinIMsgSettings settings,
        ImsgBridgeSettings bridgeSettings)
    {
        var values = new List<string>();
        AddSensitive(values, Environment.UserName);
        AddSensitive(values, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        AddSensitive(values, bridgeSettings.TargetAddress);
        AddSensitive(values, bridgeSettings.MacUser);
        AddSensitive(values, bridgeSettings.IdentityFile);
        AddSensitive(values, bridgeSettings.RemoteAttachmentRoot);
        AddSensitive(values, bridgeSettings.ImsgPath);
        foreach (var address in bridgeSettings.CandidateAddresses)
        {
            AddSensitive(values, address);
        }

        foreach (var profile in settings.Profiles)
        {
            AddSensitive(values, profile.Id);
            AddSensitive(values, profile.Name);
            AddSensitive(values, profile.MacUser);
            AddSensitive(values, profile.IdentityFile);
            AddSensitive(values, profile.RemoteAttachmentRoot);
            AddSensitive(values, profile.ImsgPath);
            foreach (var address in profile.CandidateAddresses)
            {
                AddSensitive(values, address);
            }
        }

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length)
            .ToList();
    }

    private static void AddSensitive(ICollection<string> values, string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || IsCommonNonSensitiveToken(trimmed))
        {
            return;
        }

        values.Add(trimmed);
    }

    private static bool IsCommonNonSensitiveToken(string value)
    {
        return string.Equals(value, "imsg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "iMessage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "SMS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "default", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "My Mac", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifySensitiveValue(string value)
    {
        if (value.Contains('\\') || value.Contains('/'))
        {
            return "<redacted-path>";
        }

        if (value.Contains('@'))
        {
            return "<redacted-account>";
        }

        if (IPAddress.TryParse(value, out _))
        {
            return "<redacted-address>";
        }

        if (value.Contains('.'))
        {
            return "<redacted-host>";
        }

        return "<redacted-value>";
    }

    private static string ClassifyTarget(string target)
    {
        var trimmed = target.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "empty";
        }

        if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "loopback-host";
        }

        if (IPAddress.TryParse(trimmed, out var address))
        {
            if (IPAddress.IsLoopback(address))
            {
                return "loopback-ip";
            }

            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                if (bytes[0] == 10 ||
                    bytes[0] == 192 && bytes[1] == 168 ||
                    bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                {
                    return "private-ip";
                }

                if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                {
                    return "tailnet-ip";
                }

                return "public-ip";
            }

            return "ip";
        }

        if (trimmed.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return "mdns-hostname";
        }

        if (trimmed.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
        {
            return "tailnet-hostname";
        }

        return trimmed.Contains('.') ? "hostname" : "local-hostname";
    }

    private static string ClassifyPathSetting(string value, string defaultValue)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), defaultValue, StringComparison.OrdinalIgnoreCase))
        {
            return "default";
        }

        return Path.IsPathRooted(value) || value.Contains('/') || value.Contains('\\')
            ? "path"
            : "command";
    }

    private static int ActiveProfileIndex(WinIMsgSettings settings)
    {
        var index = settings.Profiles.FindIndex(profile =>
            string.Equals(profile.Id, settings.ActiveProfileId, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index;
    }
}
