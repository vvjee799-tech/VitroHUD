using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace VitroHUD.Desktop;

public sealed class WindowDragSurface : Grid
{
    private bool pointerInside, dragging;
    public WindowDragSurface()
    {
        // Match the window's default cursor so moving/re-hit-testing cannot
        // alternate between the default arrow and a custom move cursor.
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        PointerEntered += (_, _) => { pointerInside = true; UpdateFeedback(); };
        PointerExited += (_, _) => { pointerInside = false; UpdateFeedback(); };
    }

    internal void SetDragging(bool value) { dragging = value; UpdateFeedback(); }
    private void UpdateFeedback() => Opacity = dragging || pointerInside ? 1 : .6;
}
