using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace VitroHUD.Desktop;

public sealed class ClientHandleButton : Button
{
    internal Action<PointerRoutedEventArgs>? Pressed, Moved, Released, Cancelled;
    internal Action<KeyRoutedEventArgs>? Shortcut;
    public ClientHandleButton() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Pressed?.Invoke(e); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerRoutedEventArgs e) => Moved?.Invoke(e);
    protected override void OnPointerReleased(PointerRoutedEventArgs e) { Released?.Invoke(e); e.Handled = true; }
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e) => Cancelled?.Invoke(e);
    protected override void OnPointerCanceled(PointerRoutedEventArgs e) => Cancelled?.Invoke(e);
    protected override void OnKeyDown(KeyRoutedEventArgs e) { Shortcut?.Invoke(e); if (!e.Handled) base.OnKeyDown(e); }
}
