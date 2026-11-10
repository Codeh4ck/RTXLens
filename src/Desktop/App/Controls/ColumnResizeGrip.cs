using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace RTXLens.App.Controls;

public sealed class ColumnResizeGrip : Grid
{
    public ColumnResizeGrip()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
