using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinIMsg.App.Services;
using WinIMsg.Core;
using WinIMsg.Core.Cache;
using WinIMsg.Core.Models;

namespace WinIMsg.Tests;

public sealed class SupportBundleServiceTests
{
    [Fact]
    public void RedactContentRemovesDirectIdentifiers()
    {
        var text = SupportBundleService.RedactContent(
            @"C:\Users\Test User\.ssh\id_ed25519 testuser@example.com +1 (513) 555-0100 192.0.2.10 test-mac.example.invalid",
            ["Test User", "test-mac.example.invalid"]);

        Assert.DoesNotContain("Test User", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("testuser@example.com", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("513", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("192.0.2.10", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-mac.example.invalid", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<redacted-email>", text);
        Assert.Contains("<redacted-host>", text);
    }

    [Fact]
    public async Task ExportAsyncWritesRedactedDiagnosticsBundle()
    {
        var root = Path.Combine(Path.GetTempPath(), "win-imsg-support-tests", Guid.NewGuid().ToString("N"));
        var privateIp = string.Join('.', "192", "168", "50", "10");
        var mdnsHost = string.Join('.', "test-mac", "local");
        var paths = new AppDataPaths(root);
        paths.EnsureCreated();

        try
        {
            var settings = new WinIMsgSettings
            {
                ActiveProfileId = "profile-secret-id",
                PhoneNumberRegion = "AUTO",
                Profiles =
                [
                    new ImsgBridgeProfile
                    {
                        Id = "profile-secret-id",
                        Name = "Test User Mac",
                        TargetAddresses = [privateIp, mdnsHost],
                        MacUser = "testuser",
                        IdentityFile = Path.Combine(root, "id_ed25519"),
                        RemoteAccessMode = RemoteAccessModes.LocalNetworkSsh,
                        ImsgPath = "/opt/homebrew/bin/imsg",
                        RemoteAttachmentRoot = "~/.win-imsg/attachments"
                    }
                ]
            }.Normalize();
            File.WriteAllText(paths.SettingsPath, JsonSerializer.Serialize(settings));

            var cache = new SqliteMessageCache(paths.DatabasePath);
            await cache.InitializeAsync();
            await cache.UpsertChatsAsync(
                [
                    new ImsgChat
                    {
                        Id = 7,
                        Identifier = "iMessage;-;+15135550100",
                        Guid = "chat-guid",
                        ContactName = "Alice",
                        Participants = ["+15135550100"]
                    }
                ]);
            await cache.UpsertMessagesAsync(
                [
                    new ImsgMessage
                    {
                        Guid = "message-guid",
                        ChatId = 7,
                        ChatIdentifier = "iMessage;-;+15135550100",
                        Text = "hello",
                        IsFromMe = true
                    }
                ]);

            var log = new AppLogService(paths.Logs);
            log.Info($"Connection failed for {mdnsHost} / {privateIp} as testuser. Email testuser@example.com phone +1 513 555 0100.");

            var service = new SupportBundleService(paths, log);
            var result = await service.ExportAsync(new SupportBundleExportRequest(
                settings,
                settings.ToBridgeSettings(),
                new ImsgCapabilities
                {
                    Version = "test",
                    RpcMethods = ["chats.list", "messages.history", "watch.subscribe", "send"]
                },
                $"Setup failed for {mdnsHost}",
                "Contact probe saw testuser@example.com",
                "Cache sync idle",
                "Connected"));

            Assert.True(File.Exists(result.BundlePath));
            Assert.Contains("manifest.json", result.Entries);
            Assert.Contains("settings.redacted.json", result.Entries);
            Assert.Contains("diagnostics/cache.json", result.Entries);
            Assert.Contains("logs/win-imsg.redacted.log", result.Entries);

            using var archive = ZipFile.OpenRead(result.BundlePath);
            var bundleText = string.Join(
                Environment.NewLine,
                archive.Entries.Select(ReadEntryText));

            Assert.Contains("private-ip", bundleText);
            Assert.Contains("mdns-hostname", bundleText);
            Assert.Contains("\"chats\": 1", bundleText);
            Assert.Contains("\"messages\": 1", bundleText);
            Assert.DoesNotContain(mdnsHost, bundleText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(privateIp, bundleText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("testuser@example.com", bundleText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("+1 513 555 0100", bundleText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Test User Mac", bundleText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
