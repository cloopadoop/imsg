using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace WinIMsg.App.Services;

internal sealed class WindowMinimizeHook : IDisposable
{
    private const uint WmSize = 0x0005;
    private const nuint SizeMinimized = 1;

    private readonly Window _window;
    private readonly IWindowShellService _windowShell;
    private readonly Func<bool> _shouldHide;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly SubclassProc _subclassProc;
    private readonly nuint _subclassId;
    private readonly nint _handle;

    public WindowMinimizeHook(Window window, IWindowShellService windowShell, Func<bool> shouldHide)
    {
        _window = window;
        _windowShell = windowShell;
        _shouldHide = shouldHide;
        _dispatcherQueue = window.DispatcherQueue;
        _subclassProc = WindowProc;
        _subclassId = (nuint)GetHashCode();
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        SetWindowSubclass(_handle, _subclassProc, _subclassId, 0);
    }

    public void Dispose()
    {
        RemoveWindowSubclass(_handle, _subclassProc, _subclassId);
    }

    private nint WindowProc(nint hWnd, uint message, nuint wParam, nint lParam, nuint subclassId, nint referenceData)
    {
        if (message == WmSize && wParam == SizeMinimized && _shouldHide())
        {
            _dispatcherQueue.TryEnqueue(() => _windowShell.Hide(_window));
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private delegate nint SubclassProc(nint hWnd, uint message, nuint wParam, nint lParam, nuint subclassId, nint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc subclassProc, nuint subclassId, nint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc subclassProc, nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint message, nuint wParam, nint lParam);
}
