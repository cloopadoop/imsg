namespace WinIMsg.App.Contracts;

public sealed record SyncProgressSnapshot(
    string? ActiveChatName,
    int CompletedCount,
    int TotalCount,
    int FailedCount,
    int SkippedCount,
    double Percent,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining)
{
    public static SyncProgressSnapshot Idle { get; } = new(
        ActiveChatName: null,
        CompletedCount: 0,
        TotalCount: 0,
        FailedCount: 0,
        SkippedCount: 0,
        Percent: 0,
        Elapsed: TimeSpan.Zero,
        EstimatedRemaining: null);
}
