using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace VitroHUD.Desktop;

public sealed class ProviderTabButton : Button
{
    internal Action<ProviderTabButton, PointerRoutedEventArgs>? Pressed, Moved, Released, Cancelled;
    internal Action<ProviderTabButton, KeyRoutedEventArgs>? Shortcut;
    private bool suppressSpaceRelease;

    // Own pointer activation so Button cannot synthesize a Click after a drag or
    // long press. Base keyboard/automation activation remains available.
    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Pressed?.Invoke(this, e); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerRoutedEventArgs e) => Moved?.Invoke(this, e);
    protected override void OnPointerReleased(PointerRoutedEventArgs e) { Released?.Invoke(this, e); e.Handled = true; }
    protected override void OnPointerCanceled(PointerRoutedEventArgs e) => Cancelled?.Invoke(this, e);
    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e) => Cancelled?.Invoke(this, e);
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down)
            && e.Key is VirtualKey.Space or VirtualKey.Left or VirtualKey.Right)
        {
            if (e.Key == VirtualKey.Space) suppressSpaceRelease = true;
            if (!e.KeyStatus.WasKeyDown) Shortcut?.Invoke(this, e);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Space && suppressSpaceRelease) { suppressSpaceRelease = false; e.Handled = true; return; }
        base.OnKeyUp(e);
    }
}
