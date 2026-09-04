using WinIMsg.App.Contracts;
using WinIMsg.Core.Bridge;
using WinIMsg.Core.Models;

namespace WinIMsg.App.Services;

public sealed class BridgeConnectionService(
    IImsgClient client,
    Func<ImsgBridgeSettings, CancellationToken, Task<MacIMessageHealthIssue?>>? diagnoseMacHealthAsync = null)
{
    public bool IsConnected => client.IsConnected;

    public async Task<BridgeConnectionResult> ConnectAsync(
        ImsgBridgeSettings settings,
        string profileName,
        IProgress<BridgeConnectionProgress>? progress = null,
        Func<ImsgBridgeSettings, CancellationToken, Task>? beforeConnectAsync = null,
        CancellationToken cancellationToken = default,
        long? watchSinceRowId = null)
    {
        var normalizedSettings = settings.Normalize();
        var candidates = normalizedSettings.CandidateAddresses;
        if (candidates.Count == 0)
        {
            var message = "Enter at least one Mac target address before connecting.";
            progress?.Report(new BridgeConnectionProgress(ConnectionState.Failed, message));
            return BridgeConnectionResult.Failed(message, new ImsgCapabilities());
        }

        var failures = new List<string>();
        var lastCapabilities = new ImsgCapabilities();
        foreach (var candidate in candidates)
        {
            var candidateSettings = normalizedSettings.ForTargetAddress(candidate);
            progress?.Report(new BridgeConnectionProgress(ConnectionState.Probing, $"Checking imsg on {candidate}..."));

            ImsgProbeResult probe;
            try
            {
                probe = await client.ProbeAsync(candidateSettings, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(FormatTargetFailure(candidates.Count, candidate, ex.Message));
                continue;
            }

            if (!probe.IsSuccess)
            {
                lastCapabilities = probe.Capabilities;
                failures.Add(FormatTargetFailure(candidates.Count, candidate, probe.Message));
                continue;
            }

            if (probe.BaselineChatListRead is { IsSuccess: false } baselineRead)
            {
                lastCapabilities = probe.Capabilities;
                failures.Add(FormatTargetFailure(
                    candidates.Count,
                    candidate,
                    $"{baselineRead.Command} failed before RPC connect: {baselineRead.Detail}"));
                continue;
            }

            try
            {
                if (beforeConnectAsync is not null)
                {
                    progress?.Report(new BridgeConnectionProgress(ConnectionState.Probing, $"Checking Contacts access on {candidate}..."));
                    await beforeConnectAsync(candidateSettings, cancellationToken);
                }

                progress?.Report(new BridgeConnectionProgress(ConnectionState.Connecting, $"Connecting to {profileName} at {candidate}..."));
                await client.ConnectAsync(candidateSettings, cancellationToken);

                var subscriptionStartedAtUtc = DateTimeOffset.UtcNow;
                progress?.Report(new BridgeConnectionProgress(ConnectionState.Connecting, "Starting live updates..."));
                await client.SubscribeAsync(
                    includeAttachments: true,
                    includeReactions: true,
                    sinceRowId: watchSinceRowId,
                    cancellationToken: cancellationToken);

                var connectedMessage = $"Connected to {profileName} at {candidate}.";
                progress?.Report(new BridgeConnectionProgress(ConnectionState.Connected, connectedMessage));
                return BridgeConnectionResult.Success(
                    connectedMessage,
                    probe.Capabilities,
                    subscriptionStartedAtUtc,
                    candidateSettings,
                    watchSinceRowId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(FormatTargetFailure(candidates.Count, candidate, ex.Message));
                await client.DisconnectAsync();
            }
        }

        var failureSummary = failures.Count == 0
            ? "No target address could be reached."
            : candidates.Count == 1
                ? failures[0]
            : $"Unable to connect to any target address: {string.Join("; ", failures)}";
        MacIMessageHealthIssue? healthIssue = null;
        if (diagnoseMacHealthAsync is not null)
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    healthIssue = await diagnoseMacHealthAsync(
                        normalizedSettings.ForTargetAddress(candidate),
                        cancellationToken);
                    if (healthIssue is not null)
                    {
                        failureSummary = healthIssue.UserMessage;
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // This optional diagnosis must not replace the original failure.
                }
            }
        }

        progress?.Report(new BridgeConnectionProgress(ConnectionState.Failed, failureSummary));
        return BridgeConnectionResult.Failed(failureSummary, lastCapabilities, healthIssue);
    }

    public async Task DisconnectAsync(IProgress<BridgeConnectionProgress>? progress = null)
    {
        if (!client.IsConnected)
        {
            await client.DisconnectAsync();
            progress?.Report(new BridgeConnectionProgress(ConnectionState.Disconnected, "Disconnected"));
            return;
        }

        progress?.Report(new BridgeConnectionProgress(ConnectionState.Reconnecting, "Disconnecting..."));
        await client.DisconnectAsync();
        progress?.Report(new BridgeConnectionProgress(ConnectionState.Disconnected, "Disconnected"));
    }

    private static string FormatTargetFailure(int candidateCount, string candidate, string message)
    {
        return candidateCount == 1 ? message : $"{candidate}: {message}";
    }
}

public sealed record BridgeConnectionProgress(ConnectionState State, string Message);

public sealed record BridgeConnectionResult(
    bool IsSuccess,
    string Message,
    ImsgCapabilities Capabilities,
    DateTimeOffset? SubscriptionStartedAtUtc,
    ImsgBridgeSettings? ConnectedSettings = null,
    long? WatchSinceRowId = null,
    MacIMessageHealthIssue? MacHealthIssue = null)
{
    public static BridgeConnectionResult Success(
        string message,
        ImsgCapabilities capabilities,
        DateTimeOffset subscriptionStartedAtUtc,
        ImsgBridgeSettings connectedSettings,
        long? watchSinceRowId)
    {
        return new BridgeConnectionResult(true, message, capabilities, subscriptionStartedAtUtc, connectedSettings, watchSinceRowId);
    }

    public static BridgeConnectionResult Failed(
        string message,
        ImsgCapabilities capabilities,
        MacIMessageHealthIssue? macHealthIssue = null)
    {
        return new BridgeConnectionResult(false, message, capabilities, null, MacHealthIssue: macHealthIssue);
    }
}
