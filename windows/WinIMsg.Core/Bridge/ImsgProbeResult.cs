using WinIMsg.Core.Models;

namespace WinIMsg.Core.Bridge;

public sealed record ImsgProbeCommandResult(
    string Command,
    bool IsSuccess,
    string Detail)
{
    public static ImsgProbeCommandResult Success(string command, string detail) =>
        new(command, true, detail);

    public static ImsgProbeCommandResult Failed(string command, string detail) =>
        new(command, false, detail);
}

public sealed record ImsgProbeResult(
    bool IsSuccess,
    string Message,
    string VersionText,
    ImsgCapabilities Capabilities,
    string RawStatus,
    string StandardError,
    ImsgProbeCommandResult? BaselineChatListRead = null,
    string? FailureCommand = null)
{
    public static ImsgProbeResult Success(
        string versionText,
        ImsgCapabilities capabilities,
        string rawStatus,
        ImsgProbeCommandResult? baselineChatListRead = null)
    {
        return new ImsgProbeResult(
            true,
            "imsg is reachable.",
            versionText,
            capabilities,
            rawStatus,
            string.Empty,
            baselineChatListRead);
    }

    public static ImsgProbeResult Failed(
        string message,
        string standardOutput,
        string standardError,
        string? failureCommand = null)
    {
        return new ImsgProbeResult(
            false,
            message,
            standardOutput.Trim(),
            new ImsgCapabilities(),
            standardOutput,
            standardError,
            FailureCommand: failureCommand);
    }
}
