using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace WinIMsg.App.Controls;

public sealed class CursorGrid : Grid
{
    public InputCursor? Cursor
    {
        get => ProtectedCursor;
        set => ProtectedCursor = value;
    }
}
