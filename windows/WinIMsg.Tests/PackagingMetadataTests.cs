using System.Xml.Linq;
using WinIMsg.App;
using WinIMsg.App.Services;

namespace WinIMsg.Tests;

public sealed class PackagingMetadataTests
{
    [Fact]
    public void PublishProfilesStaySelfContainedAndUntrimmed()
    {
        var profiles = Directory.GetFiles(
            Path.Combine(RepoRoot(), "windows", "WinIMsg.App", "Properties", "PublishProfiles"),
            "win-*.pubxml");

        Assert.NotEmpty(profiles);
        foreach (var profile in profiles)
        {
            var xml = XDocument.Load(profile);
            XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";

            Assert.True(IsMsBuildTrue(xml.Descendants(ns + "SelfContained").Single().Value));
            Assert.True(IsMsBuildFalse(xml.Descendants(ns + "PublishSingleFile").Single().Value));
            Assert.All(
                xml.Descendants(ns + "PublishTrimmed").Select(element => element.Value.Trim()),
                value => Assert.True(IsMsBuildFalse(value), $"Expected PublishTrimmed to be false, got {value}."));
        }
    }

    [Fact]
    public void PackagingScriptsExistAndWriteLogs()
    {
        foreach (var script in new[] { "publish-win.cmd", "install-published-win.cmd", "uninstall-win.cmd", "smoke-win.cmd" })
        {
            var path = Path.Combine(RepoRoot(), "windows", script);
            var text = File.ReadAllText(path);

            Assert.Contains("LOG=", text);
            Assert.Contains("logs", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SmokeScriptCoversDiagnosticsScenarios()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "windows", "smoke-win.cmd"));

        foreach (var scenario in new[]
        {
            "connect",
            "chat-load",
            "selected-history",
            "send-text",
            "send-attachment",
            "inbound-watch",
            "reaction-event",
            "notification",
            "cache-reconciliation"
        })
        {
            Assert.Contains(scenario, text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"smoke-%SCENARIO%.log", text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("WINIMSG_SMOKE_ALLOW_SEND", text);
    }

    [Fact]
    public void AppManifestAndRuntimeIdentityUseSameDisplayName()
    {
        var manifestPath = Path.Combine(RepoRoot(), "windows", "WinIMsg.App", "Package.appxmanifest");
        var manifest = XDocument.Load(manifestPath);
        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";

        var propertyDisplayName = manifest.Descendants(foundation + "DisplayName").Single().Value;
        var visualDisplayName = manifest.Descendants(uap + "VisualElements").Single().Attribute("DisplayName")?.Value;

        Assert.Equal(AppIdentityService.DisplayName, propertyDisplayName);
        Assert.Equal(AppIdentityService.DisplayName, visualDisplayName);
        Assert.False(string.IsNullOrWhiteSpace(AppIdentityService.AppUserModelId));
    }

    [Fact]
    public void ProjectAssetReferencesExist()
    {
        var appRoot = Path.Combine(RepoRoot(), "windows", "WinIMsg.App");
        var project = XDocument.Load(Path.Combine(appRoot, "WinIMsg.App.csproj"));
        var assetIncludes = project
            .Descendants("Content")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        Assert.NotEmpty(assetIncludes);
        foreach (var include in assetIncludes)
        {
            Assert.True(File.Exists(Path.Combine(appRoot, include!)), $"Missing asset: {include}");
        }
    }

    [Fact]
    public void AppSupportsHeadlessIdentityRegistration()
    {
        var appSource = File.ReadAllText(Path.Combine(RepoRoot(), "windows", "WinIMsg.App", "App.xaml.cs"));

        Assert.Contains("--register-app-identity", appSource);
        Assert.Contains(nameof(AppIdentityService.EnsureConfigured), appSource);
    }

    [Fact]
    public void AppRegistersDedicatedWebCompanionLaunchProtocol()
    {
        var manifestPath = Path.Combine(RepoRoot(), "windows", "WinIMsg.App", "Package.appxmanifest");
        var manifest = XDocument.Load(manifestPath);
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        var protocol = manifest.Descendants(uap + "Protocol").Single();

        Assert.Equal(AppIdentityService.ProtocolScheme, protocol.Attribute("Name")?.Value);
        Assert.Equal("\"C:\\Program Files\\WinIMsg\\WinIMsg.App.exe\" \"%1\"",
            AppIdentityService.BuildProtocolCommand(@"C:\Program Files\WinIMsg\WinIMsg.App.exe"));
        Assert.Equal(
            "0123456789abcdef0123456789abcdef",
            WinIMsg.App.App.TryGetWebCompanionLaunchNonce(
                ["winimsg:WebCompanion?nonce=0123456789abcdef0123456789abcdef"]));
        Assert.Null(WinIMsg.App.App.TryGetWebCompanionLaunchNonce(["https://example.com/?nonce=ignored"]));
    }

    [Fact]
    public void WebCompanionHasLaunchRetryAndWideInlineVideoPresentation()
    {
        var ui = File.ReadAllText(Path.Combine(
            RepoRoot(),
            "windows",
            "WinIMsg.App",
            "Services",
            "WebCompanionUi.cs"));

        Assert.Contains("winimsg:WebCompanion?nonce=", ui);
        Assert.Contains("offlineLaunch", ui);
        Assert.Contains("attempts >= 30", ui);
        Assert.Contains("fetch(`/bootstrap?nonce=", ui);
        Assert.Contains("location.replace(\"/\")", ui);
        Assert.Contains(".msg .attvideo", ui);
        Assert.Contains("width: 300px; max-width: 100%", ui);
        Assert.Contains("class=\"attvideo\" controls playsinline", ui);
    }

    [Fact]
    public void WebCompanionRestartBootstrapKeepsDataApisAuthenticated()
    {
        var service = File.ReadAllText(Path.Combine(
            RepoRoot(),
            "windows",
            "WinIMsg.App",
            "Services",
            "WebCompanionService.cs"));

        Assert.Contains("path == \"/bootstrap\"", service);
        Assert.Contains("TryConsumeBootstrapNonce", service);
        Assert.Contains("invalid or expired launch nonce", service);
        Assert.Contains("SetSessionCookie(response)", service);
        Assert.Contains("if (!IsAuthorized(request))", service);
        Assert.Contains("HttpOnly; SameSite=Strict", service);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".gitmodules")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Unable to locate repository root.");
    }

    private static bool IsMsBuildTrue(string value) =>
        string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsMsBuildFalse(string value) =>
        string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
}
