using System.Globalization;
using WinIMsg.Core.Models;
using WinIMsg.Core.Ssh;

namespace WinIMsg.App.Services;

public sealed class MacIMessageHealthService(SshCommandRunner? commandRunner = null)
{
    public const string MissingCertificateAttestation =
        "Attestation map does not contain the cert attestation. Skipping device token fetch.";

    private const int MinimumRepeatedFailures = 3;
    private readonly SshCommandRunner _commandRunner = commandRunner ?? new SshCommandRunner();

    public async Task<MacIMessageHealthIssue?> ProbeAfterConnectionFailureAsync(
        ImsgBridgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        const string command =
            "log show --style compact --last 10m --predicate 'process == \"akd\"' 2>/dev/null " +
            "| grep -F 'Attestation map does not contain the cert attestation. Skipping device token fetch.' " +
            "| wc -l";

        var result = await _commandRunner.RunShellCommandAsync(settings, command, cancellationToken);
        return result.Succeeded ? ClassifyAttestationFailureCount(result.StandardOutput) : null;
    }

    public static MacIMessageHealthIssue? ClassifyAttestationFailureCount(string output)
    {
        if (!int.TryParse(output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ||
            count < MinimumRepeatedFailures)
        {
            return null;
        }

        return new MacIMessageHealthIssue(
            "iMessage on the Mac needs attention",
            "The Mac is reachable, but Apple's authentication service could not obtain an iMessage device token. Open Messages on the Mac or restart it, then retry.",
            $"macOS AuthKit logged {count} recent device-attestation failures: {MissingCertificateAttestation}",
            count);
    }
}

public sealed record MacIMessageHealthIssue(
    string Title,
    string UserMessage,
    string DiagnosticDetail,
    int EvidenceCount);
