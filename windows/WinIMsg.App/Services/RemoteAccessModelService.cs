using System.Net;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public sealed record RemoteAccessModelOption(
    string Code,
    string Label,
    string Summary,
    string Prerequisites,
    string FailureModes,
    string SecurityNotes)
{
    public string PickerLabel => Label;

    public string Detail =>
        $"{Summary} Prerequisites: {Prerequisites} Failure modes: {FailureModes} Security: {SecurityNotes}";
}

public static class RemoteAccessModelService
{
    public static IReadOnlyList<RemoteAccessModelOption> Options { get; } =
    [
        new(
            RemoteAccessModes.LocalNetworkSsh,
            "Local network or existing VPN",
            "Use normal SSH to a Bonjour, LAN, or already-secured VPN address.",
            "Mac Remote Login enabled, Windows can resolve/reach the target, and the Mac user accepts the configured SSH key or agent.",
            "Fails outside the LAN/VPN, when Bonjour does not cross subnets, or when the Mac changes address.",
            "Recommended default. It does not expose Mac SSH directly to the public internet."),
        new(
            RemoteAccessModes.TailnetSsh,
            "Tailscale tailnet SSH",
            "Use normal SSH over a Tailscale tailnet address or MagicDNS name.",
            "Tailscale installed and signed in on Windows and Mac, a MagicDNS .ts.net or 100.64.0.0/10 target address, tailnet ACLs that allow the connection, and normal SSH authentication.",
            "Fails when either device is signed out, the tailnet is offline, MagicDNS is disabled, ACLs block SSH, or the Mac Remote Login service is off.",
            "Preferred remote option for most users because Mac SSH is reachable only through the private tailnet."),
        new(
            RemoteAccessModes.ManualTunnelSsh,
            "Manual tunnel or local proxy",
            "Connect to a local forwarded endpoint while another trusted tool owns the remote path.",
            "A tunnel/proxy is already running and the profile target points at localhost, 127.0.0.1, or ::1 on the forwarded SSH port.",
            "Fails when the external tunnel is stopped, points at the wrong Mac, uses a stale port, or starts after win-imsg connects.",
            "win-imsg stores no proxy credentials; keep tunnel secrets in the tunnel tool, Windows Credential Manager, or DPAPI-protected storage."),
        new(
            RemoteAccessModes.DirectInternetSsh,
            "Direct internet SSH",
            "Connect directly to a public DNS name or IP that forwards to the Mac SSH service.",
            "Router/firewall forwarding, hardened SSH configuration, key-only auth, account lockout protections, updates, and monitoring.",
            "Fails with ISP/NAT changes, blocked ports, dynamic DNS drift, firewall rules, or credential lockouts.",
            "Not recommended. Internet-exposed SSH is continuously scanned and should be used only by users who intentionally manage that risk.")
    ];

    public static RemoteAccessModelOption OptionFor(string? code)
    {
        var normalized = RemoteAccessModes.Normalize(code);
        return Options.First(option => string.Equals(option.Code, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static SetupCheckResult Check(ImsgBridgeSettings settings)
    {
        var normalized = settings.Normalize();
        var option = OptionFor(normalized.RemoteAccessMode);
        if (!normalized.IsConfigured)
        {
            return new SetupCheckResult(
                "profile.remoteAccess",
                "Remote access model",
                SetupCheckStatus.Skipped,
                $"{option.Label}: {option.Detail}");
        }

        var targets = normalized.CandidateAddresses;
        return normalized.RemoteAccessMode switch
        {
            RemoteAccessModes.TailnetSsh => TailnetCheck(option, targets),
            RemoteAccessModes.ManualTunnelSsh => TunnelCheck(option, targets),
            RemoteAccessModes.DirectInternetSsh => DirectInternetCheck(option, targets),
            _ => LocalNetworkCheck(option, targets)
        };
    }

    public static string BuildSettingsSummary(ImsgBridgeProfile profile)
    {
        var check = Check(profile.ToBridgeSettings(startWithWindows: false, minimizeToTray: true));
        return $"{check.Detail}";
    }

    private static SetupCheckResult LocalNetworkCheck(RemoteAccessModelOption option, IReadOnlyList<string> targets)
    {
        var suspiciousTargets = targets.Where(target => !LooksPrivateOrLocal(target) && !LooksTailnet(target)).ToList();
        if (suspiciousTargets.Count == 0)
        {
            return Passed(option, $"Targets look local/private/VPN-compatible: {string.Join(", ", targets)}.");
        }

        return Warning(
            option,
            $"These targets do not look like Bonjour, loopback, RFC1918/private, or tailnet addresses: {string.Join(", ", suspiciousTargets)}. Use Tailscale, a manual tunnel, or Direct internet SSH if those targets are intentionally remote.");
    }

    private static SetupCheckResult TailnetCheck(RemoteAccessModelOption option, IReadOnlyList<string> targets)
    {
        if (targets.Any(LooksTailnet))
        {
            return Passed(option, $"At least one target looks like a Tailscale MagicDNS or 100.64.0.0/10 address: {string.Join(", ", targets)}.");
        }

        return Warning(
            option,
            $"No target looks like a Tailscale address. Add a MagicDNS .ts.net name or 100.64.0.0/10 tailnet IP before relying on this model. Current targets: {string.Join(", ", targets)}.");
    }

    private static SetupCheckResult TunnelCheck(RemoteAccessModelOption option, IReadOnlyList<string> targets)
    {
        if (targets.Count > 0 && LooksLoopback(targets[0]))
        {
            return Passed(option, $"Primary target is a local forwarded endpoint: {targets[0]}.");
        }

        return Warning(
            option,
            $"Manual tunnel/proxy mode expects the primary target to be localhost, 127.0.0.1, or ::1. Current primary target: {targets.FirstOrDefault() ?? "none"}.");
    }

    private static SetupCheckResult DirectInternetCheck(RemoteAccessModelOption option, IReadOnlyList<string> targets)
    {
        var publicLookingTargets = targets.Where(target => !LooksPrivateOrLocal(target) && !LooksTailnet(target)).ToList();
        var targetDetail = publicLookingTargets.Count == 0
            ? "The selected targets do not look public; if they are LAN/VPN addresses, choose Local network or Tailscale instead."
            : $"Public-looking targets: {string.Join(", ", publicLookingTargets)}.";
        return Warning(option, $"{targetDetail} Direct internet SSH is intentionally marked risky; use key-only authentication and avoid storing passwords in win-imsg.");
    }

    // Status text stays a single decision-relevant sentence; the long-form
    // prerequisites/failure/security guidance renders only in the Diagnostics
    // checklist (BuildCommandPreview), not in the settings pane.
    private static SetupCheckResult Passed(RemoteAccessModelOption option, string detail) =>
        new("profile.remoteAccess", "Remote access model", SetupCheckStatus.Passed, $"{option.Label}: {detail}");

    private static SetupCheckResult Warning(RemoteAccessModelOption option, string detail) =>
        new("profile.remoteAccess", "Remote access model", SetupCheckStatus.Warning, $"{option.Label}: {detail}");

    private static bool LooksPrivateOrLocal(string target) =>
        LooksLoopback(target) ||
        target.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(HostPart(target), out var address) && IsPrivateAddress(address));

    private static bool LooksTailnet(string target)
    {
        var host = HostPart(target);
        if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
            address.GetAddressBytes() is [100, >= 64 and <= 127, _, _];
    }

    private static bool LooksLoopback(string target)
    {
        var host = HostPart(target);
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => bytes[0] == 10 ||
                bytes[0] == 192 && bytes[1] == 168 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31,
            System.Net.Sockets.AddressFamily.InterNetworkV6 => address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
                bytes[0] is 0xfc or 0xfd,
            _ => false
        };
    }

    private static string HostPart(string target)
    {
        var trimmed = target.Trim();
        if (trimmed.StartsWith('['))
        {
            var closing = trimmed.IndexOf(']');
            return closing > 1 ? trimmed[1..closing] : trimmed;
        }

        var colon = trimmed.LastIndexOf(':');
        if (colon > 0 && trimmed.Count(static character => character == ':') == 1)
        {
            return trimmed[..colon];
        }

        return trimmed;
    }
}
