using System.Text;
using WinIMsg.Core.Models;
using WinIMsg.Core.Ssh;

namespace WinIMsg.Tests;

public sealed class SshProcessFactoryTests
{
    [Fact]
    public void ImsgRpcCommandUsesOpenSshAndConfiguredTarget()
    {
        var settings = new ImsgBridgeSettings
        {
            TargetAddress = "test-mac.example.invalid",
            TargetAddresses = ["test-mac.example.invalid", "192.0.2.10"],
            MacUser = "testuser",
            SshPort = 2222,
            IdentityFile = "C:\\keys\\mac",
            ImsgPath = "/opt/homebrew/bin/imsg"
        };

        var startInfo = SshProcessFactory.Create(settings, ["rpc"]);
        var args = startInfo.ArgumentList.ToArray();

        Assert.EndsWith(OperatingSystem.IsWindows() ? "ssh.exe" : "ssh", startInfo.FileName);
        if (OperatingSystem.IsWindows() && Path.IsPathFullyQualified(startInfo.FileName))
        {
            Assert.True(File.Exists(startInfo.FileName));
        }

        Assert.Contains("2222", args);
        Assert.Contains("C:\\keys\\mac", args);
        Assert.Contains("testuser@test-mac.example.invalid", args);
        Assert.Contains("/opt/homebrew/bin/imsg", args);
        Assert.Equal("rpc", args[^1]);
        Assert.Equal(Encoding.UTF8, startInfo.StandardOutputEncoding);
        Assert.Equal(Encoding.UTF8, startInfo.StandardErrorEncoding);
    }

    [Fact]
    public void BridgeSettingsNormalizeKeepsOrderedTargetAddresses()
    {
        var settings = new ImsgBridgeSettings
        {
            TargetAddresses = ["192.0.2.10", "test-mac.example.invalid", "192.0.2.10"],
            MacUser = "testuser"
        }.Normalize();

        Assert.Equal("192.0.2.10", settings.TargetAddress);
        Assert.Equal(["192.0.2.10", "test-mac.example.invalid"], settings.CandidateAddresses);
        Assert.Equal("testuser@192.0.2.10", settings.SshTarget);

        var fallback = settings.ForTargetAddress("test-mac.example.invalid");

        Assert.Equal("test-mac.example.invalid", fallback.TargetAddress);
        Assert.Equal("testuser@test-mac.example.invalid", fallback.SshTarget);
    }

    [Fact]
    public void SftpCommandResolvesOpenSshExecutable()
    {
        var executable = SshProcessFactory.ResolveSftpExecutable();

        Assert.EndsWith(OperatingSystem.IsWindows() ? "sftp.exe" : "sftp", executable);
        if (OperatingSystem.IsWindows() && Path.IsPathFullyQualified(executable))
        {
            Assert.True(File.Exists(executable));
        }
    }

    [Fact]
    public void ScpRemoteTargetUsesRawSftpPathWithoutShellQuotes()
    {
        var settings = new ImsgBridgeSettings
        {
            TargetAddress = "192.0.2.10",
            MacUser = "testuser",
            SshPort = 22
        }.Normalize();
        const string remotePath = "/Users/testuser/Library/Messages/Attachments/d6/06/example/dating for demiromantic and demisexual - Google Search.png";

        var target = SshFileTransferService.BuildScpRemoteTarget(settings, remotePath);

        Assert.Equal($"testuser@192.0.2.10:{remotePath}", target);
        Assert.DoesNotContain("'", target);
    }

    [Fact]
    public void SftpBatchCommandsQuoteLocalAndRemotePaths()
    {
        const string remotePath = "/Users/testuser/Library/Messages/Attachments/10/00/example/franek_pixelart's Thread · Threads.png";
        var localPath = Path.Combine("C:\\Users\\Test User\\AppData\\Local\\WinIMsg\\Attachments", "franek_pixelart's Thread · Threads.png");

        var get = SshFileTransferService.BuildSftpGetCommand(remotePath, localPath);
        var put = SshFileTransferService.BuildSftpPutCommand(localPath, remotePath);

        Assert.StartsWith("get \"", get);
        Assert.Contains("\" \"C:/Users/Test User/AppData/Local/WinIMsg/Attachments/franek_pixelart's Thread · Threads.png\"", get);
        Assert.StartsWith("put \"C:/Users/Test User/AppData/Local/WinIMsg/Attachments/franek_pixelart's Thread · Threads.png\"", put);
        Assert.EndsWith("\"", put);
    }

    [Fact]
    public void AttachmentUploadDirectoryCommandResolvesHomeBeforeSftp()
    {
        var command = SshFileTransferService.BuildUploadDirectoryCommand("~/.win-imsg/attachments/abc123");

        Assert.Contains("cd ~ && pwd -P", command);
        Assert.Contains("\\~/*", command);
        Assert.Contains("mkdir -p \"$remote_dir\"", command);
        Assert.Contains("cd \"$remote_dir\" && pwd -P", command);
        Assert.DoesNotContain("printf '%s\\n' \"$remote_dir\"", command);
    }

    [Fact]
    public void ShellCommandRunnerQuotesCommandAsSingleRemoteShellArgument()
    {
        const string command = "mkdir -p ~/'.win-imsg'/attachments";

        var quoted = SshCommandRunner.QuoteRemoteShellArgument(command);

        Assert.Equal("'mkdir -p ~/'\\''.win-imsg'\\''/attachments'", quoted);
    }

    [Fact]
    public void AttachmentDownloadRemotePathCandidatesRepairUtf8Mojibake()
    {
        const string mojibakePath = "/Users/testuser/Library/Messages/Attachments/10/00/example/franek_pixelart's Thread \u00C2\u00B7 Threads.png";
        const string repairedPath = "/Users/testuser/Library/Messages/Attachments/10/00/example/franek_pixelart's Thread \u00B7 Threads.png";

        var candidates = SshFileTransferService.GetDownloadRemotePathCandidates(mojibakePath);

        Assert.Equal([mojibakePath, repairedPath], candidates);
    }

    [Fact]
    public void AttachmentDownloadRemotePathCandidatesLeaveCleanPathAlone()
    {
        const string remotePath = "/Users/testuser/Library/Messages/Attachments/10/00/example/IMG_3027.png";

        var candidates = SshFileTransferService.GetDownloadRemotePathCandidates(remotePath);

        Assert.Equal([remotePath], candidates);
    }

    [Fact]
    public void MacHostActionBuildsFaceTimeLinkCommand()
    {
        var command = MacHostActionService.BuildCreateFaceTimeLinkCommand();

        Assert.Contains("Create Link", command);
        Assert.Contains("Copy Link", command);
        Assert.Contains("launchctl asuser", command);
        Assert.Contains("clickFirstNamed", command);
        Assert.Contains("clickFaceTimeItem", command);
        Assert.Contains("Could not find FaceTime Create Link control", command);
        Assert.Contains("https://facetime.apple.com/join", command);
        Assert.DoesNotContain("facetime://", command);
    }

    [Fact]
    public void MacHostActionBuildsFaceTimeAvailabilityCommand()
    {
        var command = MacHostActionService.BuildFaceTimeLinkAvailabilityCommand();

        Assert.Contains("open -Ra FaceTime", command);
        Assert.Contains("launchctl asuser", command);
        Assert.Contains("System Events", command);
        Assert.Contains("UI elements enabled", command);
    }

    [Fact]
    public void MacHostActionBuildsAttachmentMaterializationCommand()
    {
        var command = MacHostActionService.BuildMaterializeAttachmentCommand(
            "/Users/testuser/Library/Messages/Attachments/example/video.mov",
            "+15135550123",
            null);

        Assert.Contains("open -a Messages", command);
        Assert.Contains("sms:+15135550123", command);
        Assert.Contains("Attachment file is still missing on the Mac", command);
        Assert.Contains("/Users/testuser/Library/Messages/Attachments/example/video.mov", command);
    }

    [Fact]
    public void MacHostActionPermissionPromptRunsImsgStatusAndOpensPrivacyPanes()
    {
        var command = MacHostActionService.BuildPermissionPromptCommand("/opt/homebrew/bin/imsg");

        Assert.Contains("'/opt/homebrew/bin/imsg' status --json", command);
        Assert.Contains("Privacy_Contacts", command);
        Assert.Contains("Privacy_AllFiles", command);
        Assert.Contains("Privacy_Automation", command);
        Assert.Contains("launchctl asuser", command);
        Assert.Contains("reveal anchor", command);
        Assert.Contains("no supported user CLI to pre-add", command);
        Assert.Contains("win-imsg-repair-addressbook-tcc.sh", command);
        Assert.Contains("kTCCServiceAddressBook", command);
        Assert.Contains("sshd-keygen-wrapper", command);
        Assert.Contains("com.apple.sshd-keygen-wrapper", command);
        Assert.Contains("win-imsg-backup", command);
        Assert.Contains("NSContactsUsageDescription", command);
        Assert.Contains("imsg_contacts_usage_description", command);
        Assert.Contains("imsg_status_code=$?", command);
        Assert.DoesNotContain("status=$?", command);
    }

    [Fact]
    public void MacHostActionBuildsContactsAuthorizationProbe()
    {
        var command = MacHostActionService.BuildContactsAuthorizationStatusCommand();

        Assert.Contains("CNContactStore.authorizationStatus", command);
        Assert.Contains("notDetermined", command);
        Assert.Contains("authorized", command);
        Assert.DoesNotContain("requestAccess", command);
    }

    [Fact]
    public void MacHostActionContactsAccessRequestUsesSupportedNicknameOptions()
    {
        var arguments = MacHostActionService.BuildContactsAccessRequestArguments(" SyntheticContact1 ");

        Assert.Equal(["nickname", "--address", "SyntheticContact1", "--local", "--json"], arguments);
        Assert.DoesNotContain("--region", arguments);
    }
}
