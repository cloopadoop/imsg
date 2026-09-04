using System.Diagnostics;
using System.Text;
using WinIMsg.App.Contracts;
using WinIMsg.Core;
using WinIMsg.Core.Bridge;
using WinIMsg.Core.Models;
using WinIMsg.Core.Ssh;

namespace WinIMsg.App.Services;

public enum SetupCheckStatus
{
    Passed,
    Warning,
    Failed,
    Skipped
}

public sealed record SetupCheckResult(
    string Key,
    string Label,
    SetupCheckStatus Status,
    string Detail);

public sealed record SetupChecklistReport(IReadOnlyList<SetupCheckResult> Checks)
{
    public int FailedCount => Checks.Count(check => check.Status == SetupCheckStatus.Failed);

    public int WarningCount => Checks.Count(check => check.Status == SetupCheckStatus.Warning);

    public bool HasFailures => FailedCount > 0;

    public string Summary => HasFailures
        ? $"{FailedCount} required setup check(s) failed; {WarningCount} warning(s)."
        : WarningCount > 0
            ? $"Required setup checks passed with {WarningCount} warning(s)."
            : "All required setup checks passed.";
}

public interface ILocalToolProbe
{
    Task<LocalToolProbeResult> ProbeAsync(string executableName, CancellationToken cancellationToken = default);
}

public sealed record LocalToolProbeResult(
    bool IsAvailable,
    string Detail)
{
    public static LocalToolProbeResult Available(string detail) => new(true, detail);

    public static LocalToolProbeResult Missing(string detail) => new(false, detail);
}

public sealed class WindowsOpenSshToolProbe : ILocalToolProbe
{
    public async Task<LocalToolProbeResult> ProbeAsync(
        string executableName,
        CancellationToken cancellationToken = default)
    {
        var resolved = SshProcessFactory.ResolveOpenSshExecutable(executableName);
        if (Path.IsPathRooted(resolved) && File.Exists(resolved))
        {
            return LocalToolProbeResult.Available(resolved);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "where.exe" : "which",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(OperatingSystem.IsWindows()
            ? executableName
            : Path.GetFileNameWithoutExtension(executableName));

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                var firstPath = (await outputTask)
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstPath))
                {
                    return LocalToolProbeResult.Available(firstPath);
                }
            }

            var error = (await errorTask).Trim();
            return LocalToolProbeResult.Missing(string.IsNullOrWhiteSpace(error)
                ? $"{executableName} was not found in Windows OpenSSH or PATH."
                : error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LocalToolProbeResult.Missing($"{executableName} probe failed: {ex.Message}");
        }
    }
}

public sealed class SetupChecklistService
{
    private readonly IImsgClient _client;
    private readonly AppDataPaths _paths;
    private readonly ILocalToolProbe _toolProbe;
    private readonly Func<ImsgBridgeSettings, CancellationToken, Task<MacContactsAuthorizationResult>>? _contactsProbe;

    public SetupChecklistService(
        IImsgClient client,
        AppDataPaths paths,
        MacHostActionService? macHostActions = null,
        ILocalToolProbe? toolProbe = null,
        Func<ImsgBridgeSettings, CancellationToken, Task<MacContactsAuthorizationResult>>? contactsProbe = null)
    {
        _client = client;
        _paths = paths;
        _toolProbe = toolProbe ?? new WindowsOpenSshToolProbe();
        _contactsProbe = contactsProbe ??
            (macHostActions is null ? null : macHostActions.CheckContactsAuthorizationAsync);
    }

    public async Task<SetupChecklistReport> RunAsync(
        ImsgBridgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        var normalized = settings.Normalize();
        var checks = new List<SetupCheckResult>();

        checks.Add(await CheckToolAsync(
            "windows.ssh",
            "Windows SSH client",
            "ssh.exe",
            "SSH is required for every Mac command.",
            cancellationToken));
        checks.Add(await CheckToolAsync(
            "windows.sftp",
            "Windows SFTP client",
            "sftp.exe",
            "SFTP is required for attachment upload and download.",
            cancellationToken));
        checks.Add(await CheckOptionalToolAsync(
            "windows.scp",
            "Windows SCP client",
            "scp.exe",
            "SCP is optional; the current transfer path uses SFTP.",
            cancellationToken));

        checks.Add(CheckWindowsRuntime());
        checks.Add(CheckNotificationActivation());
        checks.Add(CheckAppDataWritable());
        checks.Add(CheckProfile(normalized));
        checks.Add(RemoteAccessModelService.Check(normalized));

        if (!normalized.IsConfigured)
        {
            checks.Add(new SetupCheckResult(
                "mac.probe",
                "Mac SSH and imsg",
                SetupCheckStatus.Skipped,
                "Save a Mac profile target address before probing Remote Login, imsg --version, or imsg status --json."));
            checks.Add(new SetupCheckResult(
                "mac.contacts",
                "Mac Contacts authorization",
                SetupCheckStatus.Skipped,
                "Save a Mac profile target address before probing the SSH-launched Contacts permission context."));
            return new SetupChecklistReport(checks);
        }

        var probe = await ProbeMacAsync(normalized, cancellationToken);
        checks.Add(probe.Check);
        checks.Add(CheckBasicFeatures(probe.Result));
        checks.Add(CheckMessagesDatabaseAccess(probe.Result));
        checks.Add(CheckAutomationReadiness(probe.Result));
        checks.Add(CheckAdvancedBridge(probe.Result));
        checks.Add(await CheckContactsAsync(probe.Settings, probe.Result?.IsSuccess == true, cancellationToken));

        return new SetupChecklistReport(checks);
    }

    public static string FormatForSettings(SetupChecklistReport report, ImsgBridgeSettings? settings = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine(report.Summary);
        foreach (var check in report.Checks)
        {
            builder
                .Append(StatusToken(check.Status))
                .Append(' ')
                .Append(check.Label)
                .Append(": ")
                .AppendLine(check.Detail);
        }

        if (settings is not null)
        {
            builder
                .AppendLine()
                .AppendLine(BuildFirstRunGuide())
                .AppendLine()
                .Append(BuildCommandPreview(settings));
        }

        return builder.ToString().TrimEnd();
    }

    public static string BuildFirstRunGuide()
    {
        return string.Join(
            Environment.NewLine,
            "First-run path:",
            "1. In Profiles, choose the remote access model, enter the Mac host/address, Mac user, SSH key if needed, and imsg path.",
            "2. Enable macOS Remote Login for that user, then run this checklist from Windows.",
            "3. On the Mac, grant Full Disk Access, Contacts, and Automation to the Remote Login/OpenSSH/sshd process that receives SSH sessions.",
            "4. Connect after SSH, imsg --version, imsg status --json, and the baseline chat-list read pass. Baseline chat list/history/send does not require the optional private bridge.",
            "5. For tapbacks, rich replies/effects, polls, read receipts, typing, and group mutations, run imsg launch on the Mac and verify imsg status --json advertises the matching RPC methods.");
    }

    public static string BuildCommandPreview(ImsgBridgeSettings settings)
    {
        var normalized = settings.Normalize();
        if (!normalized.IsConfigured)
        {
            return "No Mac command will run until the active profile has a target address.";
        }

        var builder = new StringBuilder();
        builder.AppendLine("Remote access model:");
        builder.AppendLine(RemoteAccessModelService.Check(normalized).Detail);
        builder.AppendLine(RemoteAccessModelService.OptionFor(normalized.RemoteAccessMode).Detail);
        builder.AppendLine("win-imsg stores profile metadata and SSH key paths in settings JSON; connection secrets belong in SSH agent, Windows Credential Manager, DPAPI-protected storage, or the selected tunnel/VPN tool.");
        builder.AppendLine();
        builder.AppendLine("Mac commands this checklist probes:");
        foreach (var target in normalized.CandidateAddresses)
        {
            var targetSettings = normalized.ForTargetAddress(target);
            var sshPrefix = BuildSshPreviewPrefix(targetSettings);
            builder.AppendLine($"{sshPrefix} {targetSettings.ImsgPath} --version");
            builder.AppendLine($"{sshPrefix} {targetSettings.ImsgPath} status --json");
            builder.AppendLine($"{sshPrefix} {targetSettings.ImsgPath} chats --limit 1 --json");
        }

        var firstTarget = normalized.ForTargetAddress(normalized.CandidateAddresses[0]);
        builder.AppendLine($"{BuildSshPreviewPrefix(firstTarget)} sh -lc '/usr/bin/swift -e <CNContactStore authorizationStatus probe>'");
        builder.Append("Manual permission prompting is separate and may open System Settings on the Mac for Remote Login/OpenSSH/sshd, Contacts, Full Disk Access, and Automation. Contacts over SSH can be attributed to Apple's sshd-keygen-wrapper/sshd-session platform binary; macOS may refuse to show a Contacts prompt for that responsible process. Managed Macs should use a PPPC profile for kTCCServiceAddressBook targeting /usr/libexec/sshd-keygen-wrapper with code requirement 'identifier \"com.apple.sshd-keygen-wrapper\" and anchor apple'. Personal Macs can use the generated TCC repair script from Prompt Mac permissions after reviewing the backup path and attribution evidence. The installed imsg binary must also embed NSContactsUsageDescription in its Info.plist for Contacts prompts to be eligible.");
        return builder.ToString();
    }

    private async Task<SetupCheckResult> CheckToolAsync(
        string key,
        string label,
        string executableName,
        string purpose,
        CancellationToken cancellationToken)
    {
        var result = await _toolProbe.ProbeAsync(executableName, cancellationToken);
        return result.IsAvailable
            ? new SetupCheckResult(key, label, SetupCheckStatus.Passed, $"{purpose} Found {result.Detail}.")
            : new SetupCheckResult(key, label, SetupCheckStatus.Failed, $"{purpose} Missing {executableName}: {result.Detail}");
    }

    private async Task<SetupCheckResult> CheckOptionalToolAsync(
        string key,
        string label,
        string executableName,
        string purpose,
        CancellationToken cancellationToken)
    {
        var result = await _toolProbe.ProbeAsync(executableName, cancellationToken);
        return result.IsAvailable
            ? new SetupCheckResult(key, label, SetupCheckStatus.Passed, $"{purpose} Found {result.Detail}.")
            : new SetupCheckResult(key, label, SetupCheckStatus.Warning, $"{purpose} {executableName} was not found: {result.Detail}");
    }

    private static SetupCheckResult CheckWindowsRuntime()
    {
        return new SetupCheckResult(
            "windows.runtime",
            "Windows App SDK runtime",
            SetupCheckStatus.Passed,
            "The WinUI app process launched; this build is configured for self-contained Windows App SDK deployment.");
    }

    private static SetupCheckResult CheckNotificationActivation()
    {
        var shortcutPath = AppIdentityService.StartMenuShortcutPath();
        return File.Exists(shortcutPath)
            ? new SetupCheckResult(
                "windows.notifications",
                "Notification activation",
                SetupCheckStatus.Passed,
                $"Start menu activation shortcut exists at {shortcutPath}.")
            : new SetupCheckResult(
                "windows.notifications",
                "Notification activation",
                SetupCheckStatus.Warning,
                $"The app can run, but notification attribution/activation may be degraded until the Start menu shortcut is created at {shortcutPath}.");
    }

    private SetupCheckResult CheckAppDataWritable()
    {
        try
        {
            _paths.EnsureCreated();
            var probePath = Path.Combine(_paths.Root, $"setup-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, "win-imsg setup probe");
            File.Delete(probePath);
            var logProbePath = Path.Combine(_paths.Logs, $"setup-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(logProbePath, "win-imsg log probe");
            File.Delete(logProbePath);
            var attachmentProbePath = Path.Combine(_paths.Attachments, $"setup-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(attachmentProbePath, "win-imsg attachment probe");
            File.Delete(attachmentProbePath);

            return new SetupCheckResult(
                "windows.storage",
                "Log, cache, and attachment storage",
                SetupCheckStatus.Passed,
                $"Writable app data root: {_paths.Root}.");
        }
        catch (Exception ex)
        {
            return new SetupCheckResult(
                "windows.storage",
                "Log, cache, and attachment storage",
                SetupCheckStatus.Failed,
                $"Unable to write app data under {_paths.Root}: {ex.Message}");
        }
    }

    private static SetupCheckResult CheckProfile(ImsgBridgeSettings settings)
    {
        if (!settings.IsConfigured)
        {
            return new SetupCheckResult(
                "profile",
                "Active Mac profile",
                SetupCheckStatus.Failed,
                "Enter at least one target address in Settings > Profiles.");
        }

        if (!string.IsNullOrWhiteSpace(settings.IdentityFile) && !File.Exists(settings.IdentityFile))
        {
            return new SetupCheckResult(
                "profile",
                "Active Mac profile",
                SetupCheckStatus.Failed,
                $"SSH key file does not exist: {settings.IdentityFile}.");
        }

        var userDetail = string.IsNullOrWhiteSpace(settings.MacUser)
            ? "Mac user is blank, so ssh will use the current Windows account name."
            : $"Mac user is {settings.MacUser}.";
        return new SetupCheckResult(
            "profile",
            "Active Mac profile",
            SetupCheckStatus.Passed,
            $"Targets: {string.Join(", ", settings.CandidateAddresses)}. {userDetail} imsg path: {settings.ImsgPath}.");
    }

    private async Task<MacProbeChecklistResult> ProbeMacAsync(
        ImsgBridgeSettings settings,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        foreach (var target in settings.CandidateAddresses)
        {
            var targetSettings = settings.ForTargetAddress(target);
            try
            {
                var result = await _client.ProbeAsync(targetSettings, cancellationToken);
                if (result.IsSuccess)
                {
                    return new MacProbeChecklistResult(
                        new SetupCheckResult(
                            "mac.probe",
                            "Mac SSH and imsg",
                            SetupCheckStatus.Passed,
                            BuildProbeSuccessDetail(target, result, failures)),
                        result,
                        targetSettings);
                }

                failures.Add($"{target}: {DescribeMacProbeFailure(result)}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{target}: {ex.Message}");
            }
        }

        return new MacProbeChecklistResult(
            new SetupCheckResult(
            "mac.probe",
            "Mac SSH and imsg",
            SetupCheckStatus.Failed,
            failures.Count == 0
                ? "No target address could be reached."
                : $"Every target failed: {string.Join("; ", failures)}"),
            null,
            settings);
    }

    private static string DescribeMacProbeFailure(ImsgProbeResult result)
    {
        return string.IsNullOrWhiteSpace(result.FailureCommand)
            ? result.Message
            : $"{result.FailureCommand} failed: {result.Message}";
    }

    private static SetupCheckResult CheckBasicFeatures(ImsgProbeResult? probe)
    {
        if (probe is null || !probe.IsSuccess)
        {
            return new SetupCheckResult(
                "mac.basic",
                "Baseline imsg feature status",
                SetupCheckStatus.Skipped,
                "Skipped because imsg --version or imsg status --json did not succeed.");
        }

        if (!probe.Capabilities.BasicFeaturesAdvertised)
        {
            return new SetupCheckResult(
                "mac.basic",
                "Baseline imsg feature status",
                SetupCheckStatus.Warning,
                "imsg status --json did not advertise basic_features. Update imsg if chat list/history/send behavior is inconsistent.");
        }

        return probe.Capabilities.BasicFeaturesEnabled
            ? new SetupCheckResult(
                "mac.basic",
                "Baseline imsg feature status",
                SetupCheckStatus.Passed,
                "imsg status --json advertises basic chat list, history, watch, and send support.")
            : new SetupCheckResult(
                "mac.basic",
                "Baseline imsg feature status",
                SetupCheckStatus.Failed,
                "imsg status --json reports basic_features=false; update imsg and verify Messages.app is usable on the Mac.");
    }

    private static SetupCheckResult CheckMessagesDatabaseAccess(ImsgProbeResult? probe)
    {
        if (probe is null || !probe.IsSuccess)
        {
            return new SetupCheckResult(
                "mac.messages",
                "Mac Full Disk Access and Messages account",
                SetupCheckStatus.Skipped,
                "Skipped because imsg --version or imsg status --json did not succeed.");
        }

        if (probe.BaselineChatListRead is null)
        {
            return new SetupCheckResult(
                "mac.messages",
                "Mac Full Disk Access and Messages account",
                SetupCheckStatus.Warning,
                "No imsg chats --limit 1 --json probe result was reported; update win-imsg/imsg before trusting first-run readiness.");
        }

        if (!probe.BaselineChatListRead.IsSuccess)
        {
            return new SetupCheckResult(
                "mac.messages",
                "Mac Full Disk Access and Messages account",
                SetupCheckStatus.Failed,
                $"{probe.BaselineChatListRead.Command} failed. Grant Full Disk Access to Remote Login/OpenSSH/sshd for the SSH-launched context, verify ~/Library/Messages/chat.db exists, and confirm Messages.app is signed in. Detail: {probe.BaselineChatListRead.Detail}");
        }

        var zeroRows = probe.BaselineChatListRead.Detail.Contains("zero chats", StringComparison.OrdinalIgnoreCase);
        return new SetupCheckResult(
            "mac.messages",
            "Mac Full Disk Access and Messages account",
            zeroRows ? SetupCheckStatus.Warning : SetupCheckStatus.Passed,
            $"{probe.BaselineChatListRead.Command} succeeded. {probe.BaselineChatListRead.Detail}");
    }

    private static SetupCheckResult CheckAutomationReadiness(ImsgProbeResult? probe)
    {
        if (probe is null || !probe.IsSuccess)
        {
            return new SetupCheckResult(
                "mac.automation",
                "Mac Automation permission for sends",
                SetupCheckStatus.Skipped,
                "Skipped because imsg --version or imsg status --json did not succeed.");
        }

        return new SetupCheckResult(
            "mac.automation",
            "Mac Automation permission for sends",
            SetupCheckStatus.Warning,
            "This checklist does not send a message, so it cannot prove Automation. The first send may prompt on the Mac; approve Remote Login/OpenSSH/sshd controlling Messages.app, not just Terminal.");
    }

    private static SetupCheckResult CheckAdvancedBridge(ImsgProbeResult? probe)
    {
        if (probe is null || !probe.IsSuccess)
        {
            return new SetupCheckResult(
                "mac.advanced",
                "Advanced iMessage bridge",
                SetupCheckStatus.Skipped,
                "Skipped because imsg status --json did not succeed.");
        }

        if (probe.Capabilities.HasAdvancedBridge)
        {
            return new SetupCheckResult(
                "mac.advanced",
                "Advanced iMessage bridge",
                SetupCheckStatus.Passed,
                "imsg status --json reports advanced bridge support for private actions.");
        }

        return new SetupCheckResult(
            "mac.advanced",
            "Advanced iMessage bridge",
            SetupCheckStatus.Warning,
            "Baseline chat list, history, and send can work; tapback/edit/read/group private actions stay disabled until imsg status --json reports advanced bridge support.");
    }

    private async Task<SetupCheckResult> CheckContactsAsync(
        ImsgBridgeSettings settings,
        bool macProbeSucceeded,
        CancellationToken cancellationToken)
    {
        if (!macProbeSucceeded)
        {
            return new SetupCheckResult(
                "mac.contacts",
                "Mac Contacts authorization",
                SetupCheckStatus.Skipped,
                "Skipped because SSH/imsg probing did not succeed.");
        }

        if (_contactsProbe is null)
        {
            return new SetupCheckResult(
                "mac.contacts",
                "Mac Contacts authorization",
                SetupCheckStatus.Warning,
                "No Contacts authorization probe is configured.");
        }

        try
        {
            var authorization = await _contactsProbe(settings, cancellationToken);
            if (authorization.IsAuthorized)
            {
                return new SetupCheckResult(
                    "mac.contacts",
                    "Mac Contacts authorization",
                    SetupCheckStatus.Passed,
                    $"SSH-launched Contacts authorization is {authorization.State}. Raw: {authorization.Detail}");
            }

            if (authorization.IsDeniedOrRestricted)
            {
                return new SetupCheckResult(
                    "mac.contacts",
                    "Mac Contacts authorization",
                    SetupCheckStatus.Failed,
                    $"SSH-launched Contacts authorization is {authorization.State}. If macOS lists Remote Login/OpenSSH/sshd/sshd-keygen-wrapper under Contacts, approve that entry, not Terminal. If no entry appears, run Prompt Mac permissions and inspect the TCC audit: Apple platform-binary attribution can prevent a GUI Contacts prompt. Managed Mac option: deploy PPPC for kTCCServiceAddressBook to /usr/libexec/sshd-keygen-wrapper. Personal Mac option: review and run the generated TCC repair script, which backs up TCC.db before inserting the Contacts grant. Raw: {authorization.Detail}");
            }

            return new SetupCheckResult(
                "mac.contacts",
                "Mac Contacts authorization",
                SetupCheckStatus.Warning,
                $"SSH-launched Contacts authorization is {authorization.State}. Use Prompt Mac permissions or imsg nickname --local to trigger a prompt if macOS allows one. If no Contacts prompt appears, inspect the generated TCC audit for sshd-keygen-wrapper/sshd-session attribution and platform-binary prompt denial. Managed Mac option: deploy PPPC for kTCCServiceAddressBook to /usr/libexec/sshd-keygen-wrapper. Personal Mac option: review and run the generated TCC repair script, which backs up TCC.db before inserting the Contacts grant. Also verify the installed imsg binary embeds NSContactsUsageDescription in its Info.plist. Raw: {authorization.Detail}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SetupCheckResult(
                "mac.contacts",
                "Mac Contacts authorization",
                SetupCheckStatus.Warning,
                $"Contacts authorization probe failed: {ex.Message}");
        }
    }

    private static string BuildProbeSuccessDetail(
        string target,
        ImsgProbeResult probe,
        IReadOnlyList<string> earlierFailures)
    {
        var detail = new StringBuilder();
        detail.Append($"Target {target} reached. {probe.VersionText.Trim()} responded and imsg status --json parsed.");
        if (!string.IsNullOrWhiteSpace(probe.Capabilities.Version))
        {
            detail.Append($" Status version: {probe.Capabilities.Version}.");
        }

        if (!string.IsNullOrWhiteSpace(probe.Capabilities.Message))
        {
            detail.Append($" Status: {probe.Capabilities.Message}.");
        }

        if (earlierFailures.Count > 0)
        {
            detail.Append($" Earlier target failures: {string.Join("; ", earlierFailures)}");
        }

        return detail.ToString();
    }

    private static string StatusToken(SetupCheckStatus status) => status switch
    {
        SetupCheckStatus.Passed => "[pass]",
        SetupCheckStatus.Warning => "[warn]",
        SetupCheckStatus.Failed => "[fail]",
        SetupCheckStatus.Skipped => "[skip]",
        _ => "[info]"
    };

    private static string BuildSshPreviewPrefix(ImsgBridgeSettings settings)
    {
        var builder = new StringBuilder();
        builder.Append("ssh -p ");
        builder.Append(settings.SshPort);
        builder.Append(" -o BatchMode=yes");
        if (!string.IsNullOrWhiteSpace(settings.IdentityFile))
        {
            builder.Append(" -i ");
            builder.Append(QuotePreview(settings.IdentityFile));
        }

        builder.Append(' ');
        builder.Append(QuotePreview(settings.SshTarget));
        return builder.ToString();
    }

    private static string QuotePreview(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    private sealed record MacProbeChecklistResult(
        SetupCheckResult Check,
        ImsgProbeResult? Result,
        ImsgBridgeSettings Settings);
}
