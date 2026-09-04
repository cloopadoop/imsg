using System.Text.Json.Serialization;

namespace WinIMsg.Core.Models;

public sealed record WinIMsgSettings
{
    public string ActiveProfileId { get; init; } = "default";

    public List<ImsgBridgeProfile> Profiles { get; init; } = [ImsgBridgeProfile.Default()];

    public bool AutoDownloadAttachments { get; init; } = true;

    public bool StartWithWindows { get; init; }

    public bool MinimizeToTray { get; init; } = true;

    public bool EnableNotifications { get; init; } = true;

    public bool ShowMessageContentInNotifications { get; init; } = true;

    // Off by default: outbound typing signals are invisible to this user, the
    // app does not render inbound typing, and typing RPCs queue ahead of
    // sends on the bridge when the Mac is slow.
    public bool SendTypingIndicators { get; init; }

    // Loopback-only web companion for Ferdium/browser use; token-protected.
    public bool EnableWebCompanion { get; init; }

    public int WebCompanionPort { get; init; } = 8321;

    public string? WebCompanionToken { get; init; }

    public bool SyncCacheInBackground { get; init; }

    public bool MergeChatsByParticipants { get; init; } = true;

    public string PhoneNumberRegion { get; init; } = "AUTO";

    public double ConversationListWidth { get; init; } = 330;

    public List<string> ChatsPinnedAwayFromLatest { get; init; } = [];

    [JsonIgnore]
    public ImsgBridgeProfile ActiveProfile =>
        Profiles.FirstOrDefault(profile => string.Equals(profile.Id, ActiveProfileId, StringComparison.OrdinalIgnoreCase))
        ?? Profiles.FirstOrDefault()
        ?? ImsgBridgeProfile.Default();

    public ImsgBridgeSettings ToBridgeSettings()
    {
        return ActiveProfile.ToBridgeSettings(StartWithWindows, MinimizeToTray);
    }

    public WinIMsgSettings Normalize()
    {
        var normalizedProfiles = new List<ImsgBridgeProfile>();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in Profiles.Count == 0 ? [ImsgBridgeProfile.Default()] : Profiles)
        {
            var normalized = profile.Normalize();
            if (!usedIds.Add(normalized.Id))
            {
                normalized = normalized with { Id = Guid.NewGuid().ToString("N") };
                usedIds.Add(normalized.Id);
            }

            normalizedProfiles.Add(normalized);
        }

        var activeId = normalizedProfiles.Any(profile => string.Equals(profile.Id, ActiveProfileId, StringComparison.OrdinalIgnoreCase))
            ? ActiveProfileId
            : normalizedProfiles[0].Id;

        return this with
        {
            ActiveProfileId = activeId,
            Profiles = normalizedProfiles,
            AutoDownloadAttachments = AutoDownloadAttachments,
            StartWithWindows = StartWithWindows,
            MinimizeToTray = MinimizeToTray,
            EnableNotifications = EnableNotifications,
            ShowMessageContentInNotifications = ShowMessageContentInNotifications,
            SendTypingIndicators = SendTypingIndicators,
            EnableWebCompanion = EnableWebCompanion,
            WebCompanionPort = Math.Clamp(WebCompanionPort <= 0 ? 8321 : WebCompanionPort, 1024, 65535),
            WebCompanionToken = string.IsNullOrWhiteSpace(WebCompanionToken) ? null : WebCompanionToken.Trim(),
            SyncCacheInBackground = SyncCacheInBackground,
            MergeChatsByParticipants = MergeChatsByParticipants,
            PhoneNumberRegion = NormalizePhoneNumberRegion(PhoneNumberRegion),
            ConversationListWidth = Math.Clamp(ConversationListWidth, 240, 560),
            ChatsPinnedAwayFromLatest = (ChatsPinnedAwayFromLatest ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static string NormalizePhoneNumberRegion(string? region)
    {
        var trimmed = region?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            string.Equals(trimmed, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            return "AUTO";
        }

        return trimmed.Length == 2 && trimmed.All(char.IsLetter)
            ? trimmed.ToUpperInvariant()
            : "AUTO";
    }

}

public sealed record ImsgBridgeProfile
{
    public string Id { get; init; } = "default";

    public string Name { get; init; } = "My Mac";

    public List<string> TargetAddresses { get; init; } = [];

    public string MacUser { get; init; } = string.Empty;

    public int SshPort { get; init; } = 22;

    public string? IdentityFile { get; init; }

    public string RemoteAccessMode { get; init; } = RemoteAccessModes.LocalNetworkSsh;

    public string ImsgPath { get; init; } = "imsg";

    public string RemoteAttachmentRoot { get; init; } = "~/.win-imsg/attachments";

    public int RequestTimeoutSeconds { get; init; } = 30;

    [JsonIgnore]
    public IReadOnlyList<string> CandidateAddresses => BridgeTargetAddressList.Normalize(TargetAddresses);

    [JsonIgnore]
    public string PrimaryTargetAddress => CandidateAddresses.FirstOrDefault() ?? string.Empty;

    [JsonIgnore]
    public string TargetSummary => CandidateAddresses.Count == 0
        ? "No target configured"
        : string.Join(", ", CandidateAddresses);

    public static ImsgBridgeProfile Default() => new();

    public ImsgBridgeSettings ToBridgeSettings(bool startWithWindows, bool minimizeToTray)
    {
        var candidates = CandidateAddresses;
        return new ImsgBridgeSettings
        {
            TargetAddress = candidates.FirstOrDefault() ?? string.Empty,
            TargetAddresses = candidates.ToList(),
            MacUser = MacUser,
            SshPort = SshPort,
            IdentityFile = IdentityFile,
            RemoteAccessMode = RemoteAccessModes.Normalize(RemoteAccessMode),
            ImsgPath = ImsgPath,
            RemoteAttachmentRoot = RemoteAttachmentRoot,
            RequestTimeoutSeconds = RequestTimeoutSeconds,
            StartWithWindows = startWithWindows,
            MinimizeToTray = minimizeToTray
        }.Normalize();
    }

    public ImsgBridgeProfile Normalize()
    {
        var candidates = BridgeTargetAddressList.Normalize(TargetAddresses);
        var primaryAddress = candidates.FirstOrDefault() ?? string.Empty;
        var name = string.IsNullOrWhiteSpace(Name) ? primaryAddress : Name;
        return this with
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim(),
            Name = string.IsNullOrWhiteSpace(name) ? "My Mac" : name.Trim(),
            TargetAddresses = candidates.ToList(),
            MacUser = MacUser.Trim(),
            SshPort = SshPort <= 0 ? 22 : SshPort,
            IdentityFile = string.IsNullOrWhiteSpace(IdentityFile) ? null : IdentityFile.Trim(),
            RemoteAccessMode = RemoteAccessModes.Normalize(RemoteAccessMode),
            ImsgPath = string.IsNullOrWhiteSpace(ImsgPath) ? "imsg" : ImsgPath.Trim(),
            RemoteAttachmentRoot = string.IsNullOrWhiteSpace(RemoteAttachmentRoot)
                ? "~/.win-imsg/attachments"
                : RemoteAttachmentRoot.Trim(),
            RequestTimeoutSeconds = Math.Clamp(RequestTimeoutSeconds, 5, 300)
        };
    }
}
