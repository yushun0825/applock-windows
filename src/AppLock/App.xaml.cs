using System.Windows;
using AppLock.Platform;

namespace AppLock;

/// <summary>
/// アプリロック: 選んだアプリを、開くときに Windows Hello で確かめる。
/// 引数: --background(窓を出さずに始める・サインインしたとき)/ --quit(動いているアプリロックに終了を頼む)
/// </summary>
public partial class App : Application
{
    private SingleInstance? instance;
    private AppController? controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            SingleInstance.Signal(quit: true);
            Shutdown();
            return;
        }

        instance = new SingleInstance();
        if (!instance.IsFirst)
        {
            // もう動いているので、そちらの窓を出してもらう
            Native.Win32.AllowSetForegroundWindow(unchecked((uint)-1));
            SingleInstance.Signal(quit: false);
            Shutdown();
            return;
        }

        if (Testing.Theme is { } theme)
        {
            ThemeMode = theme.Equals("dark", StringComparison.OrdinalIgnoreCase) ? ThemeMode.Dark : ThemeMode.Light;
        }

        DispatcherUnhandledException += (_, error) =>
        {
            Log.Write($"画面のスレッドで失敗: {error.Exception}");
            error.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, error) =>
        {
            Log.Write($"失敗して終わる: {error.ExceptionObject}");
            controller?.EmergencyRelease();
        };
        TaskScheduler.UnobservedTaskException += (_, error) =>
        {
            Log.Write($"待たれていない処理で失敗: {error.Exception.Message}");
            error.SetObserved();
        };

        controller = new AppController(this, background: args.Contains("--background", StringComparer.OrdinalIgnoreCase));
        instance.Listen(
            onShow: () => Dispatcher.BeginInvoke(controller.ShowMainWindow),
            onQuit: () => Dispatcher.BeginInvoke(async () => await controller.QuitAsync()));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        controller?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
