using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VitroHUD.Desktop;

public sealed partial class MainWindow
{
    private void RenderSubmissionReceipt()
    {
        var receipt = receipts.For(settings.Provider, snapshot?.SourceKey);
        SubmissionStatus.Text = receipt?.Text ?? "";
        SubmissionStatus.Visibility = settings.PureMode || receipt == null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(SubmissionStatus, receipt == null ? null : "请求编号：" + receipt.Request + "；在运行日志中可追溯本次提交。");
    }
    private void TextSize_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!ready) return;
        settings.TextSize = e.NewValue; ApplyTextSize(); ScheduleSave();
    }
    private void ApplyTextSize()
    {
        TextSizeValue.Text = $"{settings.TextSize:0} px";
        Composer.FontSize = settings.TextSize * .85;
        SubmissionStatus.FontSize = Math.Clamp(settings.TextSize * .65, 12, 16);
        if (snapshot != null) RenderMessages(snapshot);
    }
    private async void RuntimeLog_Click(object sender, RoutedEventArgs e)
    {
        Root.ContextFlyout?.Hide();
        var dialogWidth = Math.Clamp(Root.ActualWidth - 80, 260, 560);
        var output = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 250, FontSize = 12,
            Text = DiagnosticLog.Journal.Recent(), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
        if (string.IsNullOrEmpty(output.Text)) output.Text = "暂无运行记录。";
        var panel = new StackPanel { Spacing = 10, MaxWidth = dialogWidth - 48 };
        panel.Children.Add(new TextBlock { Text = "记录连接变化、客户端版本与实例、提交阶段及错误代码。请求编号用于对应同一次操作。日志不保存聊天正文、草稿、凭据或完整安装路径，保留最近 7 天，最多 20 个文件 / 20 MB。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(output);
        var folder = new Button { Content = "打开日志目录" };
        folder.Click += (_, _) =>
        {
            try { System.IO.Directory.CreateDirectory(DiagnosticLog.Journal.Root); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DiagnosticLog.Journal.Root) { UseShellExecute = true }); }
            catch { ShowNotice("无法打开日志目录。", InfoBarSeverity.Warning); }
        };
        panel.Children.Add(folder);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = ElementTheme.Dark, Title = "运行日志", Content = panel,
            PrimaryButtonText = "导出日志…", CloseButtonText = "关闭" };
        dialog.Resources["ContentDialogMinWidth"] = dialogWidth;
        dialog.Resources["ContentDialogMaxWidth"] = dialogWidth;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            using var picker = new System.Windows.Forms.SaveFileDialog { Title = "导出运行日志", FileName = $"VitroHUD-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                Filter = "日志压缩包 (*.zip)|*.zip", DefaultExt = "zip", AddExtension = true, OverwritePrompt = true, RestoreDirectory = true };
            if (picker.ShowDialog(new LogDialogOwner(hwnd)) != System.Windows.Forms.DialogResult.OK) return;
            var destination = picker.FileName;
            await Task.Run(() => DiagnosticLog.Journal.Export(destination));
            ShowNotice("运行日志已导出。", InfoBarSeverity.Informational);
        }
        catch (Exception ex) { DiagnosticLog.Write(LogEvent.AppError, exception: ex); ShowNotice("日志导出未完成，请从日志目录复制文件。", InfoBarSeverity.Warning); }
    }
    private sealed class LogDialogOwner(nint handle) : System.Windows.Forms.IWin32Window
    {
        public nint Handle => handle;
    }
}
