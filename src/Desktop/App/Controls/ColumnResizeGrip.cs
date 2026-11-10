using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace RTXLens.App.Controls;

public sealed partial class ColumnResizeGrip : Grid
{
    public ColumnResizeGrip()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
