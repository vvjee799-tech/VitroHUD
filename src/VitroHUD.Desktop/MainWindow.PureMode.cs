using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace VitroHUD.Desktop;

public sealed partial class MainWindow
{
    private Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase? appearanceContextFlyout;
    private static bool IsContentPart(MessagePart part) => part.Kind is not ("timing" or "reasoning-status" or "activity" or "context");
    private void PureMode_Click(object sender, RoutedEventArgs e)
    {
        settings.PureMode = PureModeButton.IsChecked == true;
        modelFlyout?.Hide(); clientFlyout?.Hide(); Root.ContextFlyout?.Hide();
        CancelClientGesture(); clientPreview?.HidePreview();
        ApplyPureMode(); ScheduleSave();
    }

    private void ApplyPureMode()
    {
        var pure = settings.PureMode;
        appearanceContextFlyout ??= Root.ContextFlyout;
        Root.ContextFlyout = pure ? null : appearanceContextFlyout;
        PureModeButton.IsChecked = pure;
        PureModeDot.Visibility = pure ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(PureModeButton, pure ? "退出纯净模式" : "开启纯净模式");
        ToolTipService.SetToolTip(PureModeButton, pure ? "退出纯净模式" : "开启纯净模式");
        HeaderSection.Visibility = ComposerToolbar.Visibility = Notice.Visibility = pure ? Visibility.Collapsed : Visibility.Visible;
        SendButton.Visibility = pure ? Visibility.Collapsed : Visibility.Visible;
        SendColumn.Width = pure ? new GridLength(0) : new GridLength(38);
        ComposerGrid.ColumnSpacing = pure ? 0 : 8;
        ComposerShell.Padding = pure ? new Thickness(15, 7, 15, 7) : new Thickness(15, 7, 6, 7);
        Composer.PlaceholderText = pure ? "" : "继续这段对话…";
        ConversationScroll.VerticalScrollBarVisibility = pure ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        ConversationScroll.Padding = pure ? new Thickness(3, 10, 3, 16) : new Thickness(3, 23, 3, 16);
        // Keep the same input control and draft; only presentation changes.
        if (pure) NewContentButton.Visibility = EmptyState.Visibility = Visibility.Collapsed;
        else EmptyState.Visibility = snapshot?.Messages.Count is > 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateModeChrome(); RenderSubmissionReceipt();
        if (!pure) LayoutClientHandle();
        if (snapshot != null) RenderMessages(snapshot);
        SyncWindowGeometry();
    }

    private void UpdateModeChrome()
    {
        var expanded = !collapsed && !compactAnimating;
        PureModeButton.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ClientHandleLayer.Visibility = WindowDragHandle.Visibility = expanded && !settings.PureMode ? Visibility.Visible : Visibility.Collapsed;
        if (settings.PureMode) clientPreview?.HidePreview();
    }
}
