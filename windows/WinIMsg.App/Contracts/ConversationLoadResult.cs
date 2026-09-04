using WinIMsg.Core.Models;

namespace WinIMsg.App.Contracts;

public enum ConversationLoadSource
{
    Cache,
    Remote,
    CacheFallback
}

public sealed record ConversationLoadResult(
    ConversationTarget Target,
    IReadOnlyList<ImsgMessage> Messages,
    ConversationLoadSource Source,
    bool IsLoading,
    string? Error,
    bool MayHaveOlderHistory);
