namespace WinIMsg.Core.Models;

public static class RemoteAccessModes
{
    public const string LocalNetworkSsh = "local-network-ssh";
    public const string TailnetSsh = "tailnet-ssh";
    public const string ManualTunnelSsh = "manual-tunnel-ssh";
    public const string DirectInternetSsh = "direct-internet-ssh";

    private static readonly HashSet<string> KnownModes = new(StringComparer.OrdinalIgnoreCase)
    {
        LocalNetworkSsh,
        TailnetSsh,
        ManualTunnelSsh,
        DirectInternetSsh
    };

    public static string Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrWhiteSpace(trimmed) && KnownModes.Contains(trimmed)
            ? trimmed.ToLowerInvariant()
            : LocalNetworkSsh;
    }
}
