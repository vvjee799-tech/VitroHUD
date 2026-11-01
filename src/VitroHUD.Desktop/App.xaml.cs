using Microsoft.UI.Xaml;
namespace VitroHUD.Desktop;
public partial class App : Microsoft.UI.Xaml.Application
{
    private MainWindow? window;
    private Mutex? instance;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? activationWait;
    public App()
    {
        InitializeComponent();
        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        DebugSettings.XamlResourceReferenceFailed += (_, e) => Log(e.Message);
        UnhandledException += (_, e) => Log(e.Message + Environment.NewLine + e.Exception);
    }
    internal static void Log(string text) => System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VitroHUD-error.log"), DateTimeOffset.Now.ToString("O") + " " + text + Environment.NewLine);
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var name = AppPaths.InstancePrefix + Wire.Hash(Environment.UserName);
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
        instance = new Mutex(true, name, out var first);
        if (!first) { activation.Set(); activation.Dispose(); instance.Dispose(); Exit(); return; }
        window = new MainWindow();
        activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => window.DispatcherQueue.TryEnqueue(window.Restore), null, Timeout.Infinite, false);
        window.Closed += (_, _) => { activationWait?.Unregister(null); activation?.Dispose(); instance?.ReleaseMutex(); instance?.Dispose(); };
        window.Activate();
    }
}
