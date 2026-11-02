using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinRT.Interop;

namespace VitroHUD.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, BrokerClient> brokers = [];
    private readonly Dictionary<string, BridgeReply> latest = [];
    private readonly HashSet<string> readingInactive = [];
    private readonly DraftStore drafts = new();
    private readonly SubmissionReceipts receipts = new();
    private readonly Dictionary<string, (string Digest, StackPanel View)> rendered = [];
    private readonly Dictionary<string, bool> reasoningExpanded = [];
    private SourceSnapshot? snapshot;
    private readonly AppearanceSettings settings;
    private readonly GlassBackdrop glass = new();
    private readonly NativeWindow.RoundedRegion roundedRegion = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly nint hwnd;
    private readonly NativeWindow.SubclassProc subclass;
    private readonly System.Windows.Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon applicationIcon;
    private bool ready, busy, sending, following = true, pinned, changingDraft, scrollingToLatest, movingWindow;
    private string lastDigest = "", pinnedKey = "";
    private string hotkey = "Ctrl+Alt+G";
    private Flyout? modelFlyout;
    private InputNonClientPointerSource? inputRegions;
    private (int Width, int Height, int Edge, int CutY, int Side) inputGeometry;
    private PointInt32 dragOriginWindow, dragOriginPointer;
    private uint dragPointerId;
    private static readonly string SettingsPath = AppPaths.FilePath("settings.json");

    public MainWindow()
    {
        settings = LoadSettings(); InitializeComponent(); InitializeClientHandle(); InitializeCompactWidget();
        Composer.GotFocus += (_, _) => ComposerShell.BorderBrush = (Brush)Root.Resources["AccentTextBrush"];
        Composer.LostFocus += (_, _) => ComposerShell.BorderBrush = (Brush)Root.Resources["PanelStrokeBrush"];
        Title = "VitroHUD · 官方会话投射";
        ExtendsContentIntoTitleBar = true;
        hwnd = WindowNative.GetWindowHandle(this);
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "VitroHUD.ico");
        applicationIcon = new System.Drawing.Icon(iconPath, new System.Drawing.Size(32, 32));
        AppWindow.SetIcon(iconPath);
        var presenter = OverlappedPresenter.Create(); presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = true;
        AppWindow.SetPresenter(presenter);
        inputRegions = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        subclass = OnWindowMessage; NativeWindow.SetWindowSubclass(hwnd, subclass, 1, 0);
        NativeWindow.RemoveNonClientFrame(hwnd);
        var dpi = NativeWindow.GetDpiForWindow(hwnd) / 96d;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var gutter = (int)Math.Round(ClientHandlePlacement.Gutter * dpi);
        var w = (int)(Math.Clamp(settings.Width, 370, 600) * dpi) + 2 * gutter;
        var h = Math.Min((int)(Math.Clamp(settings.Height, 540, 1200) * dpi), area.Height - 48);
        var x = Math.Clamp(settings.X is { } sx ? sx - gutter : area.X + area.Width - w - 24, area.X, Math.Max(area.X, area.X + area.Width - w));
        var y = Math.Clamp(settings.Y ?? area.Y + 24, area.Y, Math.Max(area.Y, area.Y + area.Height - h));
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        var corner = 1; NativeWindow.DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));
        roundedRegion.Update(hwnd);
        var noSystemBorder = -2; NativeWindow.DwmSetWindowAttribute(hwnd, 34, ref noSystemBorder, sizeof(int));
        var dark = 1; NativeWindow.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        var host = 1; NativeWindow.DwmSetWindowAttribute(hwnd, 17, ref host, sizeof(int));
        glass.Attach(hwnd); SystemBackdrop = glass;
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) { CancelClientGesture(); CancelCompactDrag(); } DispatcherQueue.TryEnqueue(SyncWindowGeometry); };
        TransparencySlider.Value = settings.Transparency; FrostingSlider.Value = settings.Frosting; EdgeHighlightSlider.Value = settings.EdgeHighlight; CornerRadiusSlider.Value = settings.CornerRadius; TextSizeSlider.Value = settings.TextSize; TopmostSwitch.IsOn = settings.AlwaysOnTop; ForegroundInputSwitch.IsOn = settings.AllowForegroundInput;
        NativeWindow.Topmost(hwnd, settings.AlwaysOnTop);
        if (!NativeWindow.RegisterHotKey(hwnd, 72, 0x4003, 0x47))
        {
            if (NativeWindow.RegisterHotKey(hwnd, 72, 0x4007, 0x47)) hotkey = "Ctrl+Alt+Shift+G";
            else { hotkey = "系统托盘"; ShowNotice("唤出快捷键已被占用，可从系统托盘打开侧栏。", InfoBarSeverity.Warning); }
        }
        tray = new() { Icon = applicationIcon, Text = $"VitroHUD · {hotkey}", Visible = true };
        HotkeyHelp.Text = $"{hotkey} 唤出并恢复交互";
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示会话侧栏", null, (_, _) => DispatcherQueue.TryEnqueue(Restore));
        menu.Items.Add("恢复鼠标交互", null, (_, _) => DispatcherQueue.TryEnqueue(Restore));
        menu.Items.Add("退出 VitroHUD", null, (_, _) => DispatcherQueue.TryEnqueue(Close));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => DispatcherQueue.TryEnqueue(Restore);
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveSettings(); };
        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); Notice.IsOpen = false; };
        timer.Tick += async (_, _) => await Task.WhenAll(PollAsync(), PollInactiveAsync());
        AppWindow.Changed += (_, change) =>
        {
            if (!ready || collapsed || compactAnimating || NativeWindow.IsIconic(hwnd)) return;
            if (change.DidSizeChange) { var scale = NativeWindow.GetDpiForWindow(hwnd) / 96d; settings.Width = (int)Math.Round(AppWindow.Size.Width / scale - 2 * ClientHandlePlacement.Gutter); settings.Height = (int)Math.Round(AppWindow.Size.Height / scale); }
            if (change.DidPositionChange) { var scale = NativeWindow.GetDpiForWindow(hwnd) / 96d; settings.X = AppWindow.Position.X + (int)Math.Round(ClientHandlePlacement.Gutter * scale); settings.Y = AppWindow.Position.Y; }
            if (change.DidSizeChange || change.DidPositionChange) ScheduleSave();
        };
        Root.PreviewKeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { if (compactDrag.Pressed) CancelCompactDrag(); else if (clientPlacement.Pressed) CancelClientGesture(); else if (clientMenuOpen) clientFlyout?.Hide(); else Collapse(); e.Handled = true; } };
        Root.SizeChanged += (_, _) => SyncInputRegions();
        Root.Loaded += async (_, _) => { ready = true; LayoutClientHandle(); SyncWindowGeometry(); UpdateClientHandle(); ApplyAppearance(); ApplyTextSize(); ApplyPureMode(); timer.Start(); _ = Task.Run(BackgroundLauncher.DiscoverInstalledClients); await PollAsync(); };
        Closed += (_, _) => { StopCompactAnimation(); StopClientHandle(); ready = false; timer.Stop(); saveTimer.Stop(); noticeTimer.Stop(); SaveSettings(); SystemBackdrop = null; tray.Dispose(); applicationIcon.Dispose(); NativeWindow.UnregisterHotKey(hwnd, 72); NativeWindow.RemoveWindowSubclass(hwnd, subclass, 1); foreach (var b in brokers.Values) b.Dispose(); };
    }
    private BrokerClient Broker => brokers.TryGetValue(settings.Provider, out var b) ? b : brokers[settings.Provider] = new(settings.Provider);
    private async Task PollInactiveAsync()
    {
        var providers = brokers.Keys.Where(p => p != settings.Provider && !readingInactive.Contains(p)).ToArray();
        await Task.WhenAll(providers.Select(async provider =>
        {
            readingInactive.Add(provider);
            try
            {
                var reply = await brokers[provider].RequestAsync(new("read"));
                if (!ready) return;
                latest[provider] = reply;
            }
            finally { readingInactive.Remove(provider); }
        }));
    }
    private async Task PollAsync(BridgeReply? cached = null)
    {
        if (cached == null && (busy || sending)) return; if (cached == null) busy = true;
        var provider = settings.Provider;
        try
        {
            var reply = cached ?? await Broker.RequestAsync(new("read", pinned ? pinnedKey : ""));
            if (!ready) return;
            latest[provider] = reply;
            if (provider != settings.Provider) return;
            if (reply.Ok && reply.Snapshot != null)
            {
                var next = reply.Snapshot;
                if (modelFlyout != null && next.SourceKey == snapshot?.SourceKey && next.Model == "由客户端控制")
                    next = next with { Model = snapshot.Model, Capabilities = next.Capabilities with { CanSelectModel = snapshot.Capabilities.CanSelectModel } };
                if (next.Status == "disconnected" && snapshot?.Provider == next.Provider && snapshot.Messages.Count > 0) next = next with { SourceKey = snapshot.SourceKey, WindowHandle = snapshot.WindowHandle, Title = snapshot.Title, Model = snapshot.Model, Messages = snapshot.Messages, Detail = "客户端不可见 · 显示上次内容", Capabilities = new() };
                if (next.Status == "paused" && snapshot?.Provider == next.Provider && snapshot.WindowHandle == next.WindowHandle) next = next with { SourceKey = snapshot.SourceKey, Title = snapshot.Title, Model = snapshot.Model, Messages = snapshot.Messages };
                if (pinned && pinnedKey.Length > 0 && next.SourceKey != pinnedKey) { if (snapshot != null) snapshot = snapshot with { Status = "source-changed", Capabilities = new() }; SourceStatus.Text = "已固定会话 · 等待原客户端返回该会话"; SendButton.IsEnabled = false; ModelButton.IsEnabled = false; return; }
                var newSource = snapshot?.SourceKey != next.SourceKey;
                if (newSource)
                {
                    var previousKey = snapshot?.SourceKey;
                    var draft = drafts.Bind(settings.Provider, previousKey, next.SourceKey, Composer.Text);
                    snapshot = next;
                    changingDraft = true;
                    Composer.Text = draft;
                    changingDraft = false;
                }
                else snapshot = next;
                ConversationTitle.Text = string.IsNullOrWhiteSpace(next.Title) ? "连接官方会话" : next.Title;
                SourceStatus.Text = settings.Provider + " · " + next.Detail;
                RenderSubmissionReceipt();
                RenderModel(next.Model);
                ModelButton.IsEnabled = next.Capabilities.CanSelectModel;
                SendButton.IsEnabled = next.IsGenerating ? next.Capabilities.CanStop : next.Capabilities.CanSend && !string.IsNullOrWhiteSpace(Composer.Text);
                RenderSendIcon(next.IsGenerating);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SendButton, next.IsGenerating ? "停止生成" : "发送到原会话");
                EmptyState.Visibility = !settings.PureMode && next.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                EmptyTitle.Text = next.Status == "unrecognized" ? "等待打开会话" : "打开一段官方会话";
                EmptyDetail.Text = next.Detail;
                if (newSource) following = true;
                RenderMessages(next);
            }
            else
            {
                SourceStatus.Text = settings.Provider + " · " + reply.Error; SendButton.IsEnabled = false; ModelButton.IsEnabled = false;
                if (snapshot != null) snapshot = snapshot with { Status = "read-error", Capabilities = new() };
            }
        }
        finally { if (cached == null) busy = false; }
    }
    private void RenderSendIcon(bool generating)
    {
        SendIcon.Visibility = generating ? Visibility.Collapsed : Visibility.Visible;
        SendStopIcon.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RenderModel(string label)
    {
        var sourceLabel = label;
        var claude = Regex.Match(label, @"^claude-(sonnet|opus|haiku)-(\d+)-(\d+)(?:\[1m\])?(?= |$)", RegexOptions.IgnoreCase);
        if (claude.Success)
        {
            var family = claude.Groups[1].Value;
            label = char.ToUpperInvariant(family[0]) + family[1..] + " " + claude.Groups[2].Value + "." + claude.Groups[3].Value + label[claude.Length..];
        }
        ModelText.Inlines.Clear();
        var parts = Regex.Match(label, @"^(.*?)[ ·]+(Extra high|Very high|Low|Medium|High|Max|Ultra|Default|Auto|低|中|高|极高|最大|自动|默认)$", RegexOptions.IgnoreCase);
        ModelText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = parts.Success ? parts.Groups[1].Value : label });
        if (parts.Success) ModelText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " " + parts.Groups[2].Value, Foreground = (Brush)Root.Resources["SecondaryTextBrush"] });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ModelButton, label);
        ToolTipService.SetToolTip(ModelButton, sourceLabel);
    }
    private void RenderMessages(SourceSnapshot source)
    {
        var digest = Wire.Hash(source.SourceKey + settings.TextSize + settings.PureMode + JsonSerializer.Serialize(source.Messages, Wire.Json));
        if (digest == lastDigest) return; lastDigest = digest;
        var shouldFollow = following;
        var visibleKeys = new HashSet<string>();
        var messages = settings.PureMode ? source.Messages.Where(m => m.Parts is not { Count: > 0 } || m.Parts.Any(IsContentPart)).ToList() : source.Messages;
        for (var messageIndex = 0; messageIndex < messages.Count; messageIndex++)
        {
            var message = messages[messageIndex];
            var messageKey = source.SourceKey + ":" + message.Id;
            var messageDigest = Wire.Hash(settings.TextSize + settings.PureMode.ToString() + JsonSerializer.Serialize(message, Wire.Json));
            visibleKeys.Add(messageKey);
            if (rendered.TryGetValue(messageKey, out var existing) && existing.Digest == messageDigest)
            {
                PlaceMessage(existing.View, messageIndex); continue;
            }
            var ownMessage = message.Role == "user";
            var assistantMessage = message.Role == "assistant";
            var block = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            var contents = new StackPanel { Spacing = 9, HorizontalAlignment = HorizontalAlignment.Stretch };
            var surface = new Border
            {
                Child = contents,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = ownMessage ? new Thickness(0) : new Thickness(0, 0, 6, 0),
                Padding = ownMessage ? new Thickness(14, 11, 14, 12) : new Thickness(0),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(ownMessage ? Color.FromArgb(28, 196, 220, 183) : Colors.Transparent),
                BorderBrush = new SolidColorBrush(ownMessage ? Color.FromArgb(42, 196, 220, 183) : Colors.Transparent),
                BorderThickness = ownMessage ? new Thickness(1) : new Thickness(0)
            };
            if (ownMessage)
            {
                var alignment = new Grid();
                alignment.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12, GridUnitType.Star) });
                alignment.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88, GridUnitType.Star) });
                Grid.SetColumn(surface, 1); alignment.Children.Add(surface); block.Children.Add(alignment);
            }
            else block.Children.Add(surface);
            if (!settings.PureMode)
            {
                var heading = new Grid(); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var label = new TextBlock { Text = ownMessage ? "我" : assistantMessage ? source.Provider : "官方会话 · 已加载内容", FontSize = 15, HorizontalAlignment = ownMessage ? HorizontalAlignment.Right : HorizontalAlignment.Left, Foreground = (Brush)Root.Resources[assistantMessage ? "AccentTextBrush" : "SecondaryTextBrush"] };
                heading.Children.Add(label);
                var copy = new Button { Content = new SymbolIcon(Symbol.Copy), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(5), MinHeight = 20, Opacity = 0 };
                var hovering = false;
                block.PointerEntered += (_, _) => { hovering = true; copy.Opacity = 1; };
                block.PointerExited += (_, _) => { hovering = false; if (copy.FocusState == FocusState.Unfocused) copy.Opacity = 0; };
                copy.GotFocus += (_, _) => copy.Opacity = 1;
                copy.LostFocus += (_, _) => { if (!hovering) copy.Opacity = 0; };
                Grid.SetColumn(copy, 1); heading.Children.Add(copy); ToolTipService.SetToolTip(copy, "复制这段内容");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, "复制这段内容");
                copy.Click += (_, _) => { var package = new DataPackage(); package.SetText(message.Text); Clipboard.SetContent(package); };
                contents.Children.Add(heading);
            }
            var parts = message.Parts is { Count: > 0 } ? message.Parts : [new MessagePart("body", message.Text)];
            for (var partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                var part = parts[partIndex];
                if (settings.PureMode && !IsContentPart(part)) continue;
                var text = TranscriptRenderer.RenderPart(part, Root.Resources, settings.TextSize, settings.PureMode);
                if (part.Kind == "reasoning" && !settings.PureMode)
                {
                    var key = messageKey + ":" + partIndex;
                    var expander = new Expander { Header = "思考过程", Content = new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(90, 196, 220, 183)), BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2), Child = text }, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsExpanded = reasoningExpanded.GetValueOrDefault(key) };
                    expander.Expanding += (_, _) => reasoningExpanded[key] = true;
                    expander.Collapsed += (_, _) => reasoningExpanded[key] = false;
                    contents.Children.Add(expander);
                }
                else contents.Children.Add(text);
            }
            rendered[messageKey] = (messageDigest, block);
            PlaceMessage(block, messageIndex);
        }
        while (MessagesPanel.Children.Count > messages.Count) MessagesPanel.Children.RemoveAt(MessagesPanel.Children.Count - 1);
        foreach (var key in rendered.Keys.Where(k => !visibleKeys.Contains(k)).ToArray()) rendered.Remove(key);
        if (shouldFollow) DispatcherQueue.TryEnqueue(ScrollToLatest);
        else NewContentButton.Visibility = settings.PureMode ? Visibility.Collapsed : Visibility.Visible;
    }
    private void PlaceMessage(StackPanel block, int index)
    {
        if (index < MessagesPanel.Children.Count && ReferenceEquals(MessagesPanel.Children[index], block)) return;
        var oldIndex = MessagesPanel.Children.IndexOf(block);
        if (oldIndex >= 0) MessagesPanel.Children.RemoveAt(oldIndex);
        if (index < MessagesPanel.Children.Count) MessagesPanel.Children[index] = block;
        else MessagesPanel.Children.Add(block);
    }
    private async void Provider_Click(object sender, RoutedEventArgs e)
    {
        var provider = (string)((Button)sender).Tag;
        if (provider == settings.Provider) return;
        modelFlyout?.Hide();
        drafts.Put(settings.Provider, snapshot?.SourceKey, Composer.Text); settings.Provider = provider; pinned = false; PinButton.IsChecked = false; snapshot = null; lastDigest = "";
        ModelText.Text = "正在读取模型…"; ModelButton.IsEnabled = false; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ModelButton, "正在读取模型"); SendButton.IsEnabled = false; RenderSendIcon(false);
        changingDraft = true; Composer.Text = drafts.Get(provider, null); changingDraft = false;
        MessagesPanel.Children.Clear(); EmptyState.Visibility = settings.PureMode ? Visibility.Collapsed : Visibility.Visible; ConversationTitle.Text = "连接官方会话"; SourceStatus.Text = settings.Provider + " · 正在寻找客户端…"; RenderSubmissionReceipt(); UpdateClientHandle(); ScheduleSave();
        if (latest.TryGetValue(provider, out var cached)) await PollAsync(cached);
        await PollAsync();
    }
    private async void Send_Click(object sender, RoutedEventArgs e) => await SubmitAsync();
    private async Task SubmitAsync()
    {
        if (sending || snapshot == null || pinned && snapshot.SourceKey != pinnedKey || snapshot.Status is "read-error" or "disconnected" or "paused" or "source-changed") return;
        var source = snapshot; var text = Composer.Text;
        if (source.IsGenerating ? !source.Capabilities.CanStop : !source.Capabilities.CanSend) return;
        if (!source.IsGenerating && string.IsNullOrWhiteSpace(text)) return;
        sending = true; SendButton.IsEnabled = false;
        var broker = Broker;
        var trace = source.IsGenerating ? Guid.NewGuid().ToString("N") : receipts.Begin(source.Provider, source.SourceKey);
        RenderSubmissionReceipt();
        try
        {
            var reply = await broker.RequestAsync(new(source.IsGenerating ? "stop" : "send", source.SourceKey, text, AllowForegroundInput: settings.AllowForegroundInput, OriginWindow: hwnd, TraceId: trace));
            if (!source.IsGenerating) receipts.Complete(source.Provider, source.SourceKey, trace, reply);
            if (reply.Ok && !source.IsGenerating)
            {
                drafts.Acknowledge(source.Provider, source.SourceKey, text);
                if (settings.Provider == source.Provider && snapshot?.SourceKey == source.SourceKey && Composer.Text == text) Composer.Text = "";
            }
            if (!reply.Ok || source.IsGenerating) ShowNotice(reply.Ok ? "已请求停止原客户端生成" : reply.Error, reply.Ok ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
            else Notice.IsOpen = false;
            RenderSubmissionReceipt();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(LogEvent.AppError, source.Provider, source.IsGenerating ? "stop" : "send", ResultCode.SubmissionUnknown, trace, source.SourceKey, exception: ex);
            if (!source.IsGenerating) receipts.Complete(source.Provider, source.SourceKey, trace, new(false, Code: nameof(ResultCode.SubmissionUnknown)));
            RenderSubmissionReceipt(); ShowNotice("提交结果待确认，请检查客户端与运行日志；不会自动重发。", InfoBarSeverity.Warning);
        }
        finally { sending = false; await PollAsync(); }
    }
    private async void Composer_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Enter && !shift) { e.Handled = true; if (snapshot?.IsGenerating != true) await SubmitAsync(); }
    }
    private void Composer_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready || changingDraft) return;
        drafts.Put(settings.Provider, snapshot?.SourceKey, Composer.Text);
        SendButton.IsEnabled = !sending && snapshot?.Status is not ("source-changed" or "read-error" or "disconnected" or "paused") && (!pinned || snapshot?.SourceKey == pinnedKey) && (snapshot?.IsGenerating == true ? snapshot.Capabilities.CanStop : snapshot?.Capabilities.CanSend == true && !string.IsNullOrWhiteSpace(Composer.Text));
    }
    private async void Model_Click(object sender, RoutedEventArgs e)
    {
        if (snapshot == null || !snapshot.Capabilities.CanSelectModel) { ShowNotice("请在官方客户端选择模型，侧栏会同步实际选择。", InfoBarSeverity.Informational); return; }
        var source = snapshot; var broker = Broker;
        NativeWindow.Topmost(hwnd, true);
        var reply = await broker.RequestAsync(new("model-menu", source.SourceKey));
        if (reply.Choices?.Count > 0) ShowModelChoices(reply.Choices, source, broker);
        else { NativeWindow.Topmost(hwnd, settings.AlwaysOnTop); ShowNotice(reply.Error, InfoBarSeverity.Informational); }
    }
    private async void ShowModelChoices(List<string> choices, SourceSnapshot source, BrokerClient broker, List<string>? path = null)
    {
        await Task.Delay(150);
        if (snapshot?.SourceKey != source.SourceKey || settings.Provider != source.Provider) { NativeWindow.Topmost(hwnd, settings.AlwaysOnTop); return; }
        {
            var panel = new StackPanel { Spacing = 3, MinWidth = 240, MaxWidth = 330 };
            modelFlyout?.Hide();
            var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.TopEdgeAlignedLeft, ShouldConstrainToRootBounds = true, SystemBackdrop = new DesktopAcrylicBackdrop() };
            modelFlyout = flyout;
            flyout.Closed += (_, _) => { if (modelFlyout != flyout) return; modelFlyout = null; NativeWindow.Topmost(hwnd, settings.AlwaysOnTop); glass.Sync(); };
            foreach (var value in choices)
            {
                var item = new Button { Content = value, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Style = (Style)Root.Resources["QuietButton"], Padding = new Thickness(12, 10, 12, 10) };
                item.Click += async (_, _) =>
                {
                    flyout.Hide(); NativeWindow.Topmost(hwnd, true);
                    var result = await broker.RequestAsync(new("choose-model", source.SourceKey, Value: value, MenuPath: path));
                    if (!result.Ok) { NativeWindow.Topmost(hwnd, settings.AlwaysOnTop); ShowNotice(result.Error, InfoBarSeverity.Warning); }
                    else if (result.Choices?.Count > 0) DispatcherQueue.TryEnqueue(() => ShowModelChoices(result.Choices, source, broker, [.. path ?? [], value]));
                    else { Activate(); NativeWindow.Topmost(hwnd, settings.AlwaysOnTop); }
                    await PollAsync();
                };
                panel.Children.Add(item);
            }
            NativeWindow.Topmost(hwnd, true); Activate(); NativeWindow.SetForegroundWindow(hwnd); glass.Sync(); flyout.ShowAt(ModelButton);
        }
    }
    private void Pin_Click(object sender, RoutedEventArgs e) { pinned = PinButton.IsChecked == true; pinnedKey = pinned ? snapshot?.SourceKey ?? "" : ""; }
    private void ConversationScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) { if (scrollingToLatest) return; following = ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset < 55; if (following) NewContentButton.Visibility = Visibility.Collapsed; }
    private void ScrollToLatest()
    {
        if (!ready) return;
        scrollingToLatest = true;
        try { ConversationScroll.UpdateLayout(); ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null, true); following = true; NewContentButton.Visibility = Visibility.Collapsed; }
        finally { scrollingToLatest = false; }
    }
    private void ScrollToBottom_Click(object sender, RoutedEventArgs e) => ScrollToLatest();
    private void Appearance_Click(object sender, RoutedEventArgs e) { ApplyAppearance(); Root.ContextFlyout?.ShowAt(Root); }
    private void Appearance_Changed(object sender, RangeBaseValueChangedEventArgs e) { if (!ready) return; settings.Transparency = TransparencySlider.Value; settings.Frosting = FrostingSlider.Value; settings.EdgeHighlight = EdgeHighlightSlider.Value; settings.CornerRadius = CornerRadiusSlider.Value; ApplyAppearance(); ScheduleSave(); }
    private void ApplyAppearance()
    {
        glass.Apply(settings.Transparency, settings.Frosting);
        TintLayer.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * (1 - settings.Transparency / 100)), 255, 255, 255));
        ApplyTextContrast();
        PaintClientHandle();
        SyncWindowGeometry();
        EdgeHighlightLayer.Opacity = settings.EdgeHighlight / 100;
        TransparencyValue.Text = $"{settings.Transparency:0}%"; FrostingValue.Text = $"{settings.Frosting:0}%"; GlassStatus.Text = glass.Mode;
        EdgeHighlightValue.Text = $"{settings.EdgeHighlight:0}%";
        CornerRadiusValue.Text = $"{settings.CornerRadius:0} px";
        FrostingSlider.IsEnabled = glass.CustomEffects;
    }
    private void ApplyTextContrast()
    {
        var light = settings.Transparency <= 50;
        Root.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
        SetBrush("MainTextBrush", light ? Color.FromArgb(255, 34, 42, 37) : Color.FromArgb(255, 245, 245, 240));
        SetBrush("SecondaryTextBrush", light ? Color.FromArgb(220, 64, 76, 68) : Color.FromArgb(190, 201, 210, 205));
        SetBrush("AccentTextBrush", light ? Color.FromArgb(255, 62, 88, 52) : Color.FromArgb(255, 196, 220, 183));
        SetBrush("InlineCodeBrush", light ? Color.FromArgb(255, 47, 80, 43) : Color.FromArgb(255, 213, 231, 205));
        SetBrush("CodeSurfaceBrush", light ? Color.FromArgb(18, 32, 53, 39) : Color.FromArgb(45, 21, 30, 25));
        SetBrush("PanelStrokeBrush", light ? Color.FromArgb(90, 65, 79, 68) : Color.FromArgb(115, 255, 255, 255));
        SetBrush("TableRuleBrush", light ? Color.FromArgb(65, 65, 79, 68) : Color.FromArgb(45, 255, 255, 255));
        SetBrush("FloatingSurfaceBrush", light ? Color.FromArgb(235, 255, 255, 255) : Color.FromArgb(80, 37, 44, 47));
    }
    private void SetBrush(string name, Color color) => ((SolidColorBrush)Root.Resources[name]).Color = color;
    private void Reset_Click(object sender, RoutedEventArgs e) { TransparencySlider.Value = 70; FrostingSlider.Value = 45; EdgeHighlightSlider.Value = 60; CornerRadiusSlider.Value = AppearanceSettings.DefaultCornerRadius; TextSizeSlider.Value = AppearanceSettings.DefaultTextSize; }
    private void Topmost_Toggled(object sender, RoutedEventArgs e) { if (!ready) return; settings.AlwaysOnTop = TopmostSwitch.IsOn; NativeWindow.Topmost(hwnd, collapsed || settings.AlwaysOnTop); ScheduleSave(); }
    private void ForegroundInput_Toggled(object sender, RoutedEventArgs e) { if (!ready) return; settings.AllowForegroundInput = ForegroundInputSwitch.IsOn; ScheduleSave(); }
    private async void BackgroundConnection_Click(object sender, RoutedEventArgs e)
    {
        Root.ContextFlyout?.Hide();
        var provider = settings.Provider;
        if (provider == "ChatGPT")
        {
            var link = new TextBox { PlaceholderText = "粘贴现有会话链接或完整会话 ID", TextWrapping = TextWrapping.Wrap, MinWidth = 310 };
            var content = new StackPanel { Spacing = 14 };
            content.Children.Add(new TextBlock { Text = "在原客户端打开目标 Codex 会话，复制其会话链接并粘贴到下方。连接后会持续投射这段原会话，原客户端切换页面不会改变绑定。", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(link);
            var connect = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "连接 ChatGPT / Codex 原会话", Content = content, PrimaryButtonText = "连接", SecondaryButtonText = "取消现有绑定", CloseButtonText = "返回" };
            var choice = await connect.ShowAsync();
            if (choice == ContentDialogResult.None) return;
            var response = await Broker.RequestAsync(choice == ContentDialogResult.Primary ? new("connect-thread", Value: link.Text) : new("disconnect-thread"));
            if (!response.Ok) { ShowNotice(response.Error, InfoBarSeverity.Warning); return; }
            pinned = false; pinnedKey = ""; PinButton.IsChecked = false;
            latest.Remove(provider); lastDigest = "";
            ShowNotice(choice == ContentDialogResult.Primary ? "已连接原会话，可在后台继续收发及选择模型。" : "已取消后台会话绑定。", InfoBarSeverity.Informational);
            await PollAsync(); return;
        }
        if (provider == "Claude")
        {
            var workspace = new TextBox { PlaceholderText = @"原会话的工作目录，例如 E:\Projects\MyApp", MinWidth = 310 };
            try
            {
                var config = AppPaths.FilePath("claude-connection.json");
                if (new System.IO.FileInfo(config).Length < 16000)
                {
                    using var saved = JsonDocument.Parse(System.IO.File.ReadAllText(config));
                    workspace.Text = saved.RootElement.GetProperty("workspace").GetString() ?? "";
                }
            }
            catch { }
            var content = new StackPanel { Spacing = 14 };
            content.Children.Add(new TextBlock { Text = "在 Claude 中恢复并打开目标 Code 会话，填写这段会话的实际工作目录。连接脚本通过原客户端收发和选择模型，关闭开发者工具后继续有效；退出或重新加载 Claude 后需重新连接。", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(workspace);
            var prepare = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "连接 Claude Code 原会话", Content = content, PrimaryButtonText = "生成连接脚本", SecondaryButtonText = "断开现有连接", CloseButtonText = "返回" };
            var choice = await prepare.ShowAsync();
            if (choice == ContentDialogResult.None) return;
            if (choice == ContentDialogResult.Secondary)
            {
                await Broker.RequestAsync(new("disconnect-claude")); latest.Remove(provider); lastDigest = ""; await PollAsync(); return;
            }
            var response = await Broker.RequestAsync(new("prepare-claude", Value: workspace.Text));
            if (!response.Ok || response.Choices?.Count != 1) { ShowNotice(response.Error, InfoBarSeverity.Warning); return; }
            var script = response.Choices[0];
            var instructions = new TextBlock { Text = "在这段 Claude 原会话按 Ctrl+Alt+I，进入开发者工具 Console，粘贴并执行下面的脚本。若出现防粘贴提示，请按原客户端提示手动处理。看到连接就绪后关闭开发者工具，再点击验证。", TextWrapping = TextWrapping.Wrap };
            var scriptView = new TextBox { Text = script, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 160, MinWidth = 310, FontSize = 11 };
            var panel = new StackPanel { Spacing = 14 }; panel.Children.Add(instructions); panel.Children.Add(scriptView);
            var connect = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "在原会话执行连接脚本", Content = panel, PrimaryButtonText = "已执行，验证连接", SecondaryButtonText = "复制脚本", CloseButtonText = "稍后连接" };
            connect.SecondaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                try { var package = new DataPackage(); package.SetText(script); Clipboard.SetContent(package); instructions.Text = "已复制。在目标 Claude Code 会话的 Console 中粘贴执行，看到连接就绪后点击验证。"; }
                catch { instructions.Text = "复制未完成，请在下方选中脚本并复制。"; }
            };
            if (await connect.ShowAsync() != ContentDialogResult.Primary) return;
            var connected = await Broker.RequestAsync(new("read"));
            if (!connected.Ok || connected.Snapshot?.BackgroundConnected != true) { ShowNotice(connected.Error, InfoBarSeverity.Warning); return; }
            pinned = false; pinnedKey = ""; PinButton.IsChecked = false; latest.Remove(provider); lastDigest = "";
            ShowNotice("Claude 原会话已连接，可最小化后继续收发及选择模型。", InfoBarSeverity.Informational);
            await PollAsync(); return;
        }
        var existing = await Broker.RequestAsync(new("read"));
        if (existing.Ok && existing.Snapshot?.BackgroundConnected == true)
        {
            ShowNotice("当前客户端已连接后台模式，无需重复启动。", InfoBarSeverity.Informational);
            await PollAsync(existing); return;
        }
        var path = await Task.Run(() => BackgroundLauncher.FindExecutable(provider));
        if (path == null) { ShowNotice("尚未自动定位客户端。请先正常打开客户端一次，侧栏会自动识别并记住路径；无需手动选择程序。", InfoBarSeverity.Warning); return; }
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "连接 " + provider + " 后台会话", PrimaryButtonText = "启动后台模式", CloseButtonText = "取消",
            Content = "已自动识别客户端。先保存工作并从原客户端菜单退出，再启动后台模式。它会使用原账号和会话重新打开，并启用仅本机可访问的页面连接；打开目标会话后按实际能力启用功能，不限制客户端版本号。"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        path = await Task.Run(() => BackgroundLauncher.FindExecutable(provider));
        if (path == null) { ShowNotice("客户端主程序已移动或不可访问。请正常打开一次客户端，侧栏会重新识别路径。", InfoBarSeverity.Warning); return; }
        if (BackgroundLauncher.IsRunning(path)) { ShowNotice("原客户端仍在运行，请先从其菜单退出，再连接后台会话。", InfoBarSeverity.Warning); return; }
        try { BackgroundLauncher.Start(path); ShowNotice("正在连接后台会话，请在原客户端打开目标会话。", InfoBarSeverity.Informational); }
        catch (Exception ex) { DiagnosticLog.Write(LogEvent.ClientLaunchFailed, provider: settings.Provider, exception: ex); ShowNotice("已识别客户端，但未能启动后台模式。请确认原客户端已完全退出且主程序可以运行。", InfoBarSeverity.Warning); }
    }
    private void ClickThrough_Click(object sender, RoutedEventArgs e) { Root.ContextFlyout?.Hide(); NativeWindow.PassThrough(hwnd, true); }
    private void Collapse_Click(object sender, RoutedEventArgs e) => Collapse();
    private void SyncWindowGeometry()
    {
        if (hwnd == 0) return;
        var inset = ClientHandlePlacement.Gutter * (1 - compactProgress);
        var radius = GlassMotion.Lerp(settings.CornerRadius, 22, compactProgress);
        TintLayer.CornerRadius = ShellRim.CornerRadius = OuterHighlight.CornerRadius = new CornerRadius(radius);
        InnerHighlight.CornerRadius = new CornerRadius(Math.Max(0, radius - 1.5));
        var points = collapsed || compactAnimating || settings.PureMode || clientUsesPreview ? null : clientOutline;
        var scale = NativeWindow.GetDpiForWindow(hwnd);
        roundedRegion.Update(hwnd, AppWindow.Size.Width, AppWindow.Size.Height, scale, inset, radius, points);
        glass.SetContour(inset, radius, points); SyncInputRegions(); glass.Sync();
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void WindowDrag_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ready || movingWindow || compactAnimating || !e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;
        var point = e.GetCurrentPoint(Root).Position;
        var scale = NativeWindow.GetDpiForWindow(hwnd) / 96d;
        if (!NativeWindow.TryScreenPoint(hwnd, (int)Math.Round(point.X * scale), (int)Math.Round(point.Y * scale), out dragOriginPointer)) return;
        dragOriginWindow = AppWindow.Position;
        movingWindow = WindowDragHandle.CapturePointer(e.Pointer);
        dragPointerId = e.Pointer.PointerId;
        if (movingWindow) WindowDragHandle.SetDragging(true);
        e.Handled = movingWindow;
    }
    private void WindowDrag_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!ready || !movingWindow || e.Pointer.PointerId != dragPointerId) return;
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) { WindowDrag_PointerReleased(sender, e); return; }
        // Use screen coordinates directly: moving the window must not rebase a
        // queued XAML-relative position and cause a second, artificial movement.
        if (!NativeWindow.TryCursorPoint(out var screen)) return;
        var position = new PointInt32(dragOriginWindow.X + screen.X - dragOriginPointer.X, dragOriginWindow.Y + screen.Y - dragOriginPointer.Y);
        if (AppWindow.Position != position) { AppWindow.Move(position); glass.Sync(); }
        e.Handled = true;
    }
    private void WindowDrag_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!movingWindow || e.Pointer.PointerId != dragPointerId) return;
        movingWindow = false;
        WindowDragHandle.SetDragging(false);
        WindowDragHandle.ReleasePointerCaptures();
        if (ready) { SyncWindowGeometry(); ScheduleSave(); }
        e.Handled = true;
    }
    private void SyncInputRegions()
    {
        if (inputRegions == null || Root.ActualWidth <= 0) return;
        var scale = NativeWindow.GetDpiForWindow(hwnd) / 96d;
        var width = AppWindow.Size.Width; var height = AppWindow.Size.Height;
        var disabled = collapsed || compactAnimating;
        var edge = disabled ? 0 : Math.Max(5, (int)Math.Round(6 * scale));
        var gutter = (int)Math.Round(ClientHandlePlacement.Gutter * scale);
        var cutY = (int)Math.Round(clientY * scale);
        var side = !settings.PureMode && clientConnection > .01 ? (clientX < Root.ActualWidth / 2 ? -1 : 1) : 0;
        var next = (width, height, edge, cutY, side);
        if (inputGeometry == next) return; inputGeometry = next;
        // Only the top handle moves the window; the heading is ordinary content.
        // Keep the SDK's border routing for resize, and leave controls selectable.
        inputRegions.SetRegionRects(NonClientRegionKind.Caption, []);
        inputRegions.SetRegionRects(NonClientRegionKind.TopBorder, disabled ? [] : [new(gutter, 0, width - 2 * gutter, edge)]);
        inputRegions.SetRegionRects(NonClientRegionKind.BottomBorder, disabled ? [] : [new(gutter, height - edge, width - 2 * gutter, edge)]);
        RectInt32[] VerticalEdge(int x, int targetSide)
        {
            if (disabled) return [];
            if (side != targetSide) return [new(x, 0, edge, height)];
            var start = Math.Clamp(cutY - (int)(48 * scale), 0, height);
            var end = Math.Clamp(cutY + (int)(48 * scale), 0, height);
            return [new(x, 0, edge, start), new(x, end, edge, height - end)];
        }
        inputRegions.SetRegionRects(NonClientRegionKind.LeftBorder, VerticalEdge(gutter, -1));
        inputRegions.SetRegionRects(NonClientRegionKind.RightBorder, VerticalEdge(width - gutter - edge, 1));
    }
    private nint OnWindowMessage(nint h, uint message, nint w, nint l, nuint id, nuint data)
    {
        if (message == 0x7C && unchecked((int)w.ToInt64()) == -16)
        {
            var handled = NativeWindow.DefSubclassProc(h, message, w, l);
            NativeWindow.SuppressFrameStyle(l); return handled;
        }
        if (message == 0x83) return 0; // The complete window is client content, including the old resize margins.
        if (message == 0x24) { NativeWindow.SetMinimumSize(h, l, collapsed || compactAnimating ? CompactWidth : 370 + (int)(2 * ClientHandlePlacement.Gutter), collapsed || compactAnimating ? CompactHeight : 540); return 0; }
        if (message == 0x312 && w == 72) { Restore(); return 0; }
        if (glass.CustomEffects && message == 0x14 && NativeWindow.ClearBackground(h, w)) return 1;
        if (glass.CustomEffects && message == 0x31E) NativeWindow.EnableAlpha(h);
        var result = NativeWindow.DefSubclassProc(h, message, w, l);
        if (message is 0x47 or 0x18 or 5 or 0x2E0) DispatcherQueue.TryEnqueue(SyncWindowGeometry); return result;
    }
    private void ShowNotice(string text, InfoBarSeverity severity)
    {
        noticeTimer.Stop(); Notice.Message = text; Notice.Severity = severity; Notice.IsOpen = !string.IsNullOrWhiteSpace(text);
        if (Notice.IsOpen && severity == InfoBarSeverity.Informational) noticeTimer.Start();
    }
    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        Root.ContextFlyout?.Hide();
        var s = snapshot; var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "连接状态", CloseButtonText = "关闭", Content = $"来源：{settings.Provider}\n状态：{s?.Detail ?? "等待连接"}\n会话：{s?.Title ?? "尚未绑定"}\n模型：{s?.Model ?? "未知"}\n读取：{s?.Capabilities.CanRead == true}\n发送：{s?.Capabilities.CanSend == true}\n停止：{s?.Capabilities.CanStop == true}\n模型菜单：{s?.Capabilities.CanSelectModel == true}\n\n内容范围：官方客户端已加载的会话内容\n玻璃：{glass.Mode}\n唤出快捷键：{hotkey}" };
        await dialog.ShowAsync();
    }
    private void ScheduleSave() { saveTimer.Stop(); saveTimer.Start(); }
    private static AppearanceSettings LoadSettings()
    {
        try
        {
            var value = JsonSerializer.Deserialize<AppearanceSettings>(System.IO.File.ReadAllText(SettingsPath), Wire.Json) ?? new();
            if (value.Provider is not ("Claude" or "ChatGPT" or "DeepSeek")) value.Provider = "ChatGPT";
            value.Transparency = Math.Clamp(value.Transparency, 0, 100); value.Frosting = Math.Clamp(value.Frosting, 0, 100);
            value.EdgeHighlight = Math.Clamp(value.EdgeHighlight, 0, 100);
            value.ClientHandleX = ClientHandlePlacement.Unit(value.ClientHandleX, 1); value.ClientHandleY = ClientHandlePlacement.Unit(value.ClientHandleY, .46);
            value.ClientHandleDock = Math.Clamp(value.ClientHandleDock, -1, 1);
            return value;
        }
        catch { return new(); }
    }
    private void SaveSettings()
    {
        try { var directory = System.IO.Path.GetDirectoryName(SettingsPath)!; System.IO.Directory.CreateDirectory(directory); var text = JsonSerializer.Serialize(settings, Wire.Json); System.IO.File.WriteAllText(SettingsPath + ".tmp", text); System.IO.File.Move(SettingsPath + ".tmp", SettingsPath, true); } catch (System.IO.IOException) { }
    }
}
