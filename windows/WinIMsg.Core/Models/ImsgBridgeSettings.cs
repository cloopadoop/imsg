using System.Text.Json.Serialization;

namespace WinIMsg.Core.Models;

public sealed record ImsgBridgeSettings
{
    public string TargetAddress { get; init; } = string.Empty;

    public List<string> TargetAddresses { get; init; } = [];

    public string MacUser { get; init; } = string.Empty;

    public int SshPort { get; init; } = 22;

    public string? IdentityFile { get; init; }

    public string RemoteAccessMode { get; init; } = RemoteAccessModes.LocalNetworkSsh;

    public string ImsgPath { get; init; } = "imsg";

    public string RemoteAttachmentRoot { get; init; } = "~/.win-imsg/attachments";

    public int RequestTimeoutSeconds { get; init; } = 30;

    public bool StartWithWindows { get; init; }

    public bool MinimizeToTray { get; init; } = true;

    [JsonIgnore]
    public IReadOnlyList<string> CandidateAddresses => BridgeTargetAddressList.Normalize(TargetAddresses);

    [JsonIgnore]
    public bool IsConfigured => CandidateAddresses.Count > 0;

    [JsonIgnore]
    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(Math.Clamp(RequestTimeoutSeconds, 5, 300));

    [JsonIgnore]
    public string SshTarget => string.IsNullOrWhiteSpace(MacUser) ? TargetAddress.Trim() : $"{MacUser.Trim()}@{TargetAddress.Trim()}";

    public ImsgBridgeSettings Normalize()
    {
        var candidates = BridgeTargetAddressList.Normalize(TargetAddresses);
        var targetAddress = string.IsNullOrWhiteSpace(TargetAddress)
            ? candidates.FirstOrDefault() ?? string.Empty
            : TargetAddress.Trim();
        return this with
        {
            TargetAddress = targetAddress,
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

    public ImsgBridgeSettings ForTargetAddress(string targetAddress)
    {
        var normalized = Normalize();
        return normalized with { TargetAddress = targetAddress.Trim() };
    }
}
