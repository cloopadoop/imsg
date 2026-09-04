using WinIMsg.App.Contracts;
using WinIMsg.App.Mvvm;

namespace WinIMsg.App.ViewModels;

public sealed class SyncStatusViewModel : ObservableObject
{
    private SyncProgressSnapshot _snapshot = SyncProgressSnapshot.Idle;
    private bool _isVisible;
    private bool _isIndeterminate;
    private string _statusText = string.Empty;

    public SyncProgressSnapshot Snapshot
    {
        get => _snapshot;
        private set => SetProperty(ref _snapshot, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public double Percent => Snapshot.Percent;

    public void ShowTransient()
    {
        IsVisible = true;
        IsIndeterminate = true;
        StatusText = string.Empty;
    }

    public void Hide()
    {
        Snapshot = SyncProgressSnapshot.Idle;
        IsVisible = false;
        IsIndeterminate = false;
        StatusText = string.Empty;
        OnPropertyChanged(nameof(Percent));
    }

    public void Update(SyncProgressSnapshot snapshot, string statusText)
    {
        Snapshot = snapshot;
        IsVisible = true;
        IsIndeterminate = false;
        StatusText = statusText;
        OnPropertyChanged(nameof(Percent));
    }
}
