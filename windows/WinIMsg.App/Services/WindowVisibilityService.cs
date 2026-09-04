using System.Runtime.InteropServices;
using H.NotifyIcon;

namespace WinIMsg.App.Services;

public interface IWindowShellService
{
    void Hide(Window window);

    void Show(Window window);

    void FlashTaskbar(nint windowHandle);

    void SetUnreadBadge(nint windowHandle, int unreadCount, nint overlayIconHandle);

    void DisposeTrayIcon(TaskbarIcon trayIcon);
}

public sealed class WinUiWindowShellService : IWindowShellService
{
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint FlashWindowTray = 0x00000002;
    private const uint FlashWindowTimerNoForeground = 0x0000000C;

    public void Hide(Window window)
    {
        ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(window), SwHide);
    }

    public void Show(Window window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        ShowWindow(handle, SwRestore);
        ShowWindow(handle, SwShow);
        SetForegroundWindow(handle);
    }

    public void FlashTaskbar(nint windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == 0 || GetForegroundWindow() == windowHandle)
        {
            return;
        }

        try
        {
            var info = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                WindowHandle = windowHandle,
                Flags = FlashWindowTray | FlashWindowTimerNoForeground,
                Count = 4,
                Timeout = 0
            };
            FlashWindowEx(ref info);
        }
        catch
        {
            // Taskbar flash is opportunistic; notification delivery is the primary signal.
        }
    }

    public void SetUnreadBadge(nint windowHandle, int unreadCount, nint overlayIconHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == 0)
        {
            return;
        }

        try
        {
            var taskbar = (ITaskbarList3)Activator.CreateInstance(Type.GetTypeFromCLSID(TaskbarListClassId)!)!;
            taskbar.HrInit();
            taskbar.SetOverlayIcon(
                windowHandle,
                unreadCount > 0 ? overlayIconHandle : 0,
                unreadCount > 0 ? $"{unreadCount} unread message{(unreadCount == 1 ? string.Empty : "s")}" : null);
        }
        catch
        {
            // Taskbar overlays are shell polish; unread state remains visible in the app and tray.
        }
    }

    public void DisposeTrayIcon(TaskbarIcon trayIcon)
    {
        try
        {
            if (!trayIcon.IsDisposed)
            {
                trayIcon.Dispose();
            }
        }
        catch
        {
            // Tray cleanup should not block app shutdown.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint WindowHandle;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo flashWindowInfo);

    private static readonly Guid TaskbarListClassId = new("56FDF344-FD6D-11D0-958A-006097C9A090");

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    private interface ITaskbarList3
    {
        [PreserveSig]
        int HrInit();

        [PreserveSig]
        int AddTab(nint hwnd);

        [PreserveSig]
        int DeleteTab(nint hwnd);

        [PreserveSig]
        int ActivateTab(nint hwnd);

        [PreserveSig]
        int SetActiveAlt(nint hwnd);

        [PreserveSig]
        int MarkFullscreenWindow(nint hwnd, bool fullscreen);

        [PreserveSig]
        int SetProgressValue(nint hwnd, ulong completed, ulong total);

        [PreserveSig]
        int SetProgressState(nint hwnd, int flags);

        [PreserveSig]
        int RegisterTab(nint tabHwnd, nint mdiHwnd);

        [PreserveSig]
        int UnregisterTab(nint tabHwnd);

        [PreserveSig]
        int SetTabOrder(nint tabHwnd, nint insertBeforeHwnd);

        [PreserveSig]
        int SetTabActive(nint tabHwnd, nint mdiHwnd, uint reserved);

        [PreserveSig]
        int ThumbBarAddButtons(nint hwnd, uint buttonCount, nint buttons);

        [PreserveSig]
        int ThumbBarUpdateButtons(nint hwnd, uint buttonCount, nint buttons);

        [PreserveSig]
        int ThumbBarSetImageList(nint hwnd, nint imageList);

        [PreserveSig]
        int SetOverlayIcon(nint hwnd, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);

        [PreserveSig]
        int SetThumbnailTooltip(nint hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);

        [PreserveSig]
        int SetThumbnailClip(nint hwnd, nint clip);
    }
}
