using WinIMsg.Core.Models;

namespace WinIMsg.Core.Ssh;

public sealed class MacHostActionService(SshCommandRunner? commandRunner = null)
{
    private readonly SshCommandRunner _commandRunner = commandRunner ?? new SshCommandRunner();

    public async Task<string> CreateFaceTimeLinkAsync(
        ImsgBridgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        var result = await _commandRunner.RunShellCommandAsync(
            settings,
            BuildCreateFaceTimeLinkCommand(),
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Unable to create a FaceTime link on the Mac: {result.ErrorSummary}");
        }

        var link = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith("https://facetime.apple.com/join", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(link))
        {
            throw new InvalidOperationException("FaceTime did not return a share link.");
        }

        return link;
    }

    public async Task<bool> CheckFaceTimeLinkAvailabilityAsync(
        ImsgBridgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        var result = await _commandRunner.RunShellCommandAsync(
            settings,
            BuildFaceTimeLinkAvailabilityCommand(),
            cancellationToken);

        return result.Succeeded;
    }

    public async Task<MacContactsAuthorizationResult> CheckContactsAuthorizationAsync(
        ImsgBridgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        var result = await _commandRunner.RunShellCommandAsync(
            settings,
            BuildContactsAuthorizationStatusCommand(),
            cancellationToken);

        if (!result.Succeeded)
        {
            return MacContactsAuthorizationResult.Unknown(result.ErrorSummary);
        }

        var line = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(line))
        {
            return MacContactsAuthorizationResult.Unknown("Contacts authorization probe returned no output.");
        }

        var parts = line.Split('\t', 3, StringSplitOptions.TrimEntries);
        return parts.Length >= 2
            ? new MacContactsAuthorizationResult(parts[1], parts[0], line)
            : MacContactsAuthorizationResult.Unknown(line);
    }

    public async Task RequestContactsAccessAsync(
        ImsgBridgeSettings settings,
        string address,
        string? region = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("A contact address is required to request Contacts access.", nameof(address));
        }

        var result = await _commandRunner.RunImsgCommandAsync(
            settings,
            BuildContactsAccessRequestArguments(address),
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Unable to request Contacts access through imsg nickname --local: {result.ErrorSummary}");
        }
    }

    internal static IReadOnlyList<string> BuildContactsAccessRequestArguments(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("A contact address is required to request Contacts access.", nameof(address));
        }

        return
        [
            "nickname",
            "--address",
            address.Trim(),
            "--local",
            "--json"
        ];
    }

    public async Task PromptPermissionsAsync(
        ImsgBridgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        var result = await _commandRunner.RunShellCommandAsync(
            settings,
            BuildPermissionPromptCommand(settings.Normalize().ImsgPath),
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Unable to run Mac permission prompt: {result.ErrorSummary}");
        }
    }

    public async Task MaterializeAttachmentAsync(
        ImsgBridgeSettings settings,
        ImsgMessage message,
        ImsgAttachment attachment,
        CancellationToken cancellationToken = default)
    {
        var remotePath = attachment.RemotePath;
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            throw new InvalidOperationException("Attachment does not expose a remote path.");
        }

        var result = await _commandRunner.RunShellCommandAsync(
            settings,
            BuildMaterializeAttachmentCommand(remotePath, message.ChatIdentifier, message.Sender),
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Messages did not download the attachment on the Mac: {result.ErrorSummary}");
        }
    }

    internal static string BuildCreateFaceTimeLinkCommand()
    {
        return """
            console_uid=$(/usr/bin/stat -f %u /dev/console 2>/dev/null)
            if [ -z "$console_uid" ] || [ "$console_uid" = "0" ]; then console_uid=$(id -u); fi
            /bin/launchctl asuser "$console_uid" /usr/bin/osascript <<'APPLESCRIPT'
            on textOf(elementReference)
                tell application "System Events"
                    try
                        set elementName to name of elementReference as text
                        if elementName is not "" then return elementName
                    end try
                    try
                        set elementDescription to description of elementReference as text
                        if elementDescription is not "" then return elementDescription
                    end try
                    try
                        set elementValue to value of elementReference as text
                        if elementValue is not "" then return elementValue
                    end try
                end tell
                return ""
            end textOf

            on clickFirstNamed(containerElement, targetText)
                tell application "System Events"
                    repeat with childElement in UI elements of containerElement
                        try
                            set elementText to my textOf(childElement)
                            set elementRole to role of childElement as text
                            if elementText contains targetText and (elementRole contains "button" or elementRole contains "menu item") then
                                click childElement
                                return true
                            end if
                        end try
                        try
                            if my clickFirstNamed(childElement, targetText) then return true
                        end try
                    end repeat
                    repeat with childElement in UI elements of containerElement
                        try
                            if my textOf(childElement) contains targetText then
                                click childElement
                                return true
                            end if
                        end try
                        try
                            if my clickFirstNamed(childElement, targetText) then return true
                        end try
                    end repeat
                end tell
                return false
            end clickFirstNamed

            on clickFaceTimeItem(targetText)
                tell application "System Events"
                    tell application process "FaceTime"
                        try
                            if my clickFirstNamed(menu bar 1, targetText) then return true
                        end try
                        repeat with candidateWindow in windows
                            try
                                if my clickFirstNamed(candidateWindow, targetText) then return true
                            end try
                        end repeat
                        try
                            if my clickFirstNamed(application process "FaceTime", targetText) then return true
                        end try
                    end tell
                end tell
                return false
            end clickFaceTimeItem

            set previousClipboard to the clipboard
            tell application "FaceTime" to activate
            delay 1
            tell application "System Events"
                tell application process "FaceTime"
                    set frontmost to true
                    set didCreate to false
                    repeat 120 times
                        if my clickFaceTimeItem("Create Link") then
                            set didCreate to true
                            exit repeat
                        end if
                        delay 0.1
                    end repeat
                    if didCreate is false then error "Could not find FaceTime Create Link control."
                    delay 0.5
                    set didCopy to false
                    repeat 120 times
                        if my clickFaceTimeItem("Copy Link") then
                            set didCopy to true
                            exit repeat
                        end if
                        delay 0.1
                    end repeat
                    if didCopy is false then error "Could not find FaceTime Copy Link control."
                end tell
            end tell
            delay 0.2
            set linkText to the clipboard as text
            if linkText does not start with "https://facetime.apple.com/join" then
                set the clipboard to previousClipboard
                error "FaceTime copied text was not a share link."
            end if
            return linkText
            APPLESCRIPT
            """;
    }

    internal static string BuildFaceTimeLinkAvailabilityCommand()
    {
        return """
            console_uid=$(/usr/bin/stat -f %u /dev/console 2>/dev/null)
            if [ -z "$console_uid" ] || [ "$console_uid" = "0" ]; then console_uid=$(id -u); fi
            run_gui() { /bin/launchctl asuser "$console_uid" "$@"; }

            if ! /usr/bin/open -Ra FaceTime >/dev/null 2>&1; then
              echo "FaceTime is not available on this Mac." >&2
              exit 2
            fi

            if ! run_gui /usr/bin/osascript -e 'tell application "System Events" to return UI elements enabled' >/dev/null 2>&1; then
              echo "FaceTime link creation needs GUI scripting access on the Mac." >&2
              exit 3
            fi

            echo "available"
            """;
    }

    internal static string BuildContactsAuthorizationStatusCommand()
    {
        return """
            /usr/bin/swift -e 'import Contacts
            let status = CNContactStore.authorizationStatus(for: .contacts)
            let raw = status.rawValue
            let name: String
            switch status {
            case .notDetermined:
              name = "notDetermined"
            case .restricted:
              name = "restricted"
            case .denied:
              name = "denied"
            case .authorized:
              name = "authorized"
            @unknown default:
              name = "unknown"
            }
            print("\(raw)\t\(name)")'
            """;
    }

    internal static string BuildMaterializeAttachmentCommand(string remotePath, string? chatIdentifier, string? sender)
    {
        var target = FirstNonEmpty(chatIdentifier, sender);
        var openTargetCommand = string.IsNullOrWhiteSpace(target)
            ? string.Empty
            : "run_gui /usr/bin/open " + QuoteShell("sms:" + target) + " >/dev/null 2>&1 || true; ";
        return "console_uid=$(/usr/bin/stat -f %u /dev/console 2>/dev/null); " +
            "if [ -z \"$console_uid\" ] || [ \"$console_uid\" = \"0\" ]; then console_uid=$(id -u); fi; " +
            "run_gui() { /bin/launchctl asuser \"$console_uid\" \"$@\"; }; " +
            "target_path=" + QuoteShell(remotePath.Trim()) + "; " +
            "case \"$target_path\" in \"~/\"*) target_path=\"$HOME/${target_path#~/}\";; esac; " +
            "if [ -f \"$target_path\" ]; then echo \"$target_path\"; exit 0; fi; " +
            "run_gui /usr/bin/open -a Messages >/dev/null 2>&1 || /usr/bin/open -a Messages >/dev/null 2>&1 || true; " +
            openTargetCommand +
            "run_gui /usr/bin/osascript -e 'tell application \"Messages\" to activate' >/dev/null 2>&1 || " +
            "/usr/bin/osascript -e 'tell application \"Messages\" to activate' >/dev/null 2>&1 || true; " +
            "i=0; while [ \"$i\" -lt 30 ]; do " +
            "if [ -f \"$target_path\" ]; then echo \"$target_path\"; exit 0; fi; " +
            "sleep 1; i=$((i + 1)); done; " +
            "echo \"Attachment file is still missing on the Mac: $target_path\" >&2; exit 3";
    }

    internal static string BuildPermissionPromptCommand(string imsgPath)
    {
        var imsg = string.IsNullOrWhiteSpace(imsgPath) ? "imsg" : imsgPath.Trim();
        var privacyPanes = new[]
        {
            "x-apple.systempreferences:com.apple.settings.PrivacySecurity.extension?Privacy_Contacts",
            "x-apple.systempreferences:com.apple.preference.security?Privacy_Contacts",
            "x-apple.systempreferences:com.apple.settings.PrivacySecurity.extension?Privacy_AllFiles",
            "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles",
            "x-apple.systempreferences:com.apple.settings.PrivacySecurity.extension?Privacy_Automation",
            "x-apple.systempreferences:com.apple.preference.security?Privacy_Automation"
        };
        var paneOpenCommands = string.Join(" ", privacyPanes.Select(pane => $"open_privacy_pane {QuoteShell(pane)} && opened=1;"));
        var revealScript = """
            try
                tell application "System Settings"
                    activate
                    reveal anchor "Privacy_Contacts" of pane id "com.apple.settings.PrivacySecurity.extension"
                end tell
            end try
            try
                tell application "System Settings"
                    activate
                    reveal anchor "Privacy_AllFiles" of pane id "com.apple.settings.PrivacySecurity.extension"
                end tell
            end try
            try
                tell application "System Preferences"
                    activate
                    reveal anchor "Privacy_Contacts" of pane id "com.apple.preference.security"
                end tell
            end try
            """;
        var noticeScript = """
            try
                display notification "win-imsg is opening Contacts and Full Disk Access settings for the SSH-launched imsg process. Approve Remote Login, OpenSSH, sshd, or sshd-keygen-wrapper if macOS shows those entries." with title "win-imsg permissions"
            end try
            """;
        var auditSql = """
            select service, client, client_type, auth_value, auth_reason, auth_version, hex(csreq), policy_id, flags, last_modified
            from access
            where service in ('kTCCServiceAddressBook','kTCCServiceSystemPolicyAllFiles','kTCCServiceAppleEvents')
              and (
                client like '%imsg%' or
                client like '%ssh%' or
                client like '%sshd%' or
                client like '%OpenSSH%' or
                client like '%Terminal%'
              );
            """;
        var repairScript = """
            #!/bin/sh
            set -eu
            DB="$HOME/Library/Application Support/com.apple.TCC/TCC.db"
            CLIENT="/usr/libexec/sshd-keygen-wrapper"
            SERVICE="kTCCServiceAddressBook"
            BACKUP="$DB.win-imsg-backup-$(date +%Y%m%d-%H%M%S)"
            cp "$DB" "$BACKUP"
            /usr/bin/sqlite3 "$DB" <<'SQL'
            insert or replace into access (
              service,
              client,
              client_type,
              auth_value,
              auth_reason,
              auth_version,
              csreq,
              policy_id,
              indirect_object_identifier_type,
              indirect_object_identifier,
              indirect_object_code_identity,
              flags,
              last_modified,
              pid,
              pid_version,
              boot_uuid,
              last_reminded
            )
            select
              'kTCCServiceAddressBook',
              client,
              client_type,
              2,
              3,
              auth_version,
              csreq,
              null,
              0,
              'UNUSED',
              null,
              0,
              CAST(strftime('%s','now') AS INTEGER),
              null,
              null,
              'UNUSED',
              0
            from access
            where client='/usr/libexec/sshd-keygen-wrapper' and csreq is not null
            limit 1;
            SQL
            /usr/bin/killall tccd 2>/dev/null || true
            echo "Backed up TCC database to: $BACKUP"
            echo "Inserted Contacts grant for /usr/libexec/sshd-keygen-wrapper. Re-run win-imsg setup diagnostics to verify."
            """;
        var repairScriptEscaped = repairScript.Replace("'", "'\\''");
        return "console_uid=$(/usr/bin/stat -f %u /dev/console 2>/dev/null); " +
            "if [ -z \"$console_uid\" ] || [ \"$console_uid\" = \"0\" ]; then console_uid=$(id -u); fi; " +
            "audit_file=/tmp/win-imsg-permission-audit.txt; " +
            "repair_file=/tmp/win-imsg-repair-addressbook-tcc.sh; " +
            "open_privacy_pane() { pane=\"$1\"; " +
            "/bin/launchctl asuser \"$console_uid\" /usr/bin/open \"$pane\" >/dev/null 2>&1 && return 0; " +
            "/usr/bin/open \"$pane\" >/dev/null 2>&1 && return 0; " +
            "return 1; }; " +
            "run_gui() { /bin/launchctl asuser \"$console_uid\" \"$@\" >/dev/null 2>&1 || \"$@\" >/dev/null 2>&1; }; " +
            "{ echo \"win-imsg permission audit\"; date; echo \"user=$(id -un) uid=$(id -u) console_uid=$console_uid\"; " +
            "echo \"imsg_path=" + EscapeDoubleQuoted(imsg) + "\"; " +
            "echo \"Remote Login/OpenSSH hosts may appear in TCC as sshd, sshd-keygen-wrapper, or com.openssh.sshd-session.\"; } >\"$audit_file\" 2>&1; " +
            "printf '%s\n' '" + repairScriptEscaped + "' >\"$repair_file\"; chmod 700 \"$repair_file\"; " +
            "{ echo \"macOS has no supported user CLI to pre-add an arbitrary executable to Contacts privacy; this audit probes state, opens the approval UI, and writes an explicit personal-Mac repair script.\"; " +
            "echo \"Managed Mac PPPC target: service=kTCCServiceAddressBook, IdentifierType=path, Identifier=/usr/libexec/sshd-keygen-wrapper, CodeRequirement=identifier \\\"com.apple.sshd-keygen-wrapper\\\" and anchor apple, Authorization=Allow.\"; " +
            "echo \"Personal Mac repair script: $repair_file\"; " +
            "echo \"Run it only after TCC attribution shows platform-binary prompt denial for sshd-keygen-wrapper and after reviewing the backup path it will create.\"; } >>\"$audit_file\" 2>&1; " +
            $"{QuoteShell(imsg)} status --json >/tmp/win-imsg-permission-status.json 2>&1; " +
            "imsg_status_code=$?; " +
            $"{QuoteShell(imsg)} chats --limit 1 --json >/tmp/win-imsg-permission-chats.json 2>&1; " +
            "imsg_chats_code=$?; " +
            "{ echo \"imsg_status_code=$imsg_status_code\"; echo \"imsg_chats_code=$imsg_chats_code\"; " +
            $"if /usr/bin/strings {QuoteShell(imsg)} 2>/dev/null | /usr/bin/grep -q NSContactsUsageDescription; then echo \"imsg_contacts_usage_description=present\"; else echo \"imsg_contacts_usage_description=missing\"; fi; " +
            "echo \"--- status ---\"; cat /tmp/win-imsg-permission-status.json 2>/dev/null || true; " +
            "echo \"--- chats ---\"; cat /tmp/win-imsg-permission-chats.json 2>/dev/null || true; " +
            "echo \"--- TCC user database ---\"; " +
            "/usr/bin/sqlite3 \"$HOME/Library/Application Support/com.apple.TCC/TCC.db\" " + QuoteShell(auditSql) + " 2>&1 || true; " +
            "echo \"--- TCC system database ---\"; " +
            "/usr/bin/sqlite3 \"/Library/Application Support/com.apple.TCC/TCC.db\" " + QuoteShell(auditSql) + " 2>&1 || true; } >>\"$audit_file\" 2>&1; " +
            "opened=0; " +
            $"run_gui /usr/bin/osascript -e {QuoteShell(noticeScript)} || true; " +
            paneOpenCommands +
            $"/bin/launchctl asuser \"$console_uid\" /usr/bin/osascript -e {QuoteShell(revealScript)} >/dev/null 2>&1 && opened=1 || " +
            $"/usr/bin/osascript -e {QuoteShell(revealScript)} >/dev/null 2>&1 && opened=1 || true; " +
            "echo \"audit_file=$audit_file\"; " +
            "if [ \"$opened\" != \"1\" ]; then echo \"Unable to open macOS privacy panes from SSH. Permission probe details: $audit_file\" >&2; exit 2; fi; " +
            "exit 0";
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string QuoteShell(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string EscapeDoubleQuoted(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"");
}
