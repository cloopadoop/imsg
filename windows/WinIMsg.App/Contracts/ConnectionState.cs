namespace WinIMsg.App.Contracts;

public enum ConnectionState
{
    Disconnected,
    Probing,
    Connecting,
    Connected,
    Reconnecting,
    Degraded,
    Failed
}
