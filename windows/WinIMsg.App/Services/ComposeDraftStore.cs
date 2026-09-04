namespace WinIMsg.App.Services;

public sealed record ComposeDraftState(
    string Text,
    IReadOnlyList<string> AttachmentPaths,
    string? DraftRecipientText)
{
    public static ComposeDraftState Empty(string? draftRecipientText = null) =>
        new(string.Empty, [], draftRecipientText);

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Text) &&
        AttachmentPaths.Count == 0 &&
        string.IsNullOrWhiteSpace(DraftRecipientText);
}

public sealed class ComposeDraftStore
{
    private readonly Dictionary<string, ComposeDraftState> _drafts = new(StringComparer.OrdinalIgnoreCase);

    public string? ActiveKey { get; private set; }

    public bool IsActive(string key) =>
        string.Equals(ActiveKey, key, StringComparison.OrdinalIgnoreCase);

    public bool Contains(string key) => _drafts.ContainsKey(key);

    public void SaveActive(string? currentKey, ComposeDraftState state)
    {
        var key = ActiveKey ?? currentKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        Save(key, state);
    }

    public ComposeDraftState Activate(string key, string? fallbackDraftRecipientText = null)
    {
        ActiveKey = key;
        return _drafts.TryGetValue(key, out var state)
            ? Copy(state)
            : ComposeDraftState.Empty(fallbackDraftRecipientText);
    }

    public void Clear(string key)
    {
        _drafts.Remove(key);
    }

    private void Save(string key, ComposeDraftState state)
    {
        if (state.IsEmpty)
        {
            _drafts.Remove(key);
            return;
        }

        _drafts[key] = Copy(state);
    }

    private static ComposeDraftState Copy(ComposeDraftState state) =>
        state with { AttachmentPaths = state.AttachmentPaths.ToList() };
}
