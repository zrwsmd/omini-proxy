using System.IO;
using System.Windows;
using System.Windows.Threading;
using WowProxy.Infrastructure;

namespace WowProxy.App;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try
        {
            Bootstrap.WriteStartupLog("OnStartup", "Entered OnStartup.");
            var systemProxy = new WindowsSystemProxy();
            systemProxy.RestoreFromSnapshotIfAny();

            var settingsStore = new JsonSettingsStore();
            var settings = await settingsStore.LoadAsync();

            _mainViewModel = new MainViewModel(settingsStore, systemProxy, settings);

            var window = new MainWindow
            {
                DataContext = _mainViewModel,
            };

            window.Show();
            Bootstrap.WriteStartupLog("OnStartup", "Window shown.");
        }
        catch (Exception ex)
        {
            ShowFatal(ex);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_mainViewModel is not null)
        {
            await _mainViewModel.DisposeAsync();
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatal(e.Exception);
        e.Handled = true;
        Shutdown(1);
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ShowFatal(ex);
        }
        else
        {
            ShowFatal(new Exception("发生未知的未处理异常。"));
        }

        Shutdown(1);
    }

    private static void ShowFatal(Exception ex)
    {
        Bootstrap.WriteStartupLog("ShowFatal", ex.ToString());
        var crashPath = WriteCrashLog(ex);
        MessageBox.Show(
            $"WowProxy 启动失败：{ex.Message}\n\n已写入崩溃日志：\n{crashPath}",
            "WowProxy",
            MessageBoxButton.OK,
            MessageBoxImage.Error
        );
    }

    private static string WriteCrashLog(Exception ex)
    {
        var logsRoot = AppDataPaths.GetLogsRoot();
        var path = Path.Combine(logsRoot, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var text =
            $"Time: {DateTime.Now:O}{Environment.NewLine}"
            + $"OS: {Environment.OSVersion}{Environment.NewLine}"
            + $"Process: {Environment.ProcessPath}{Environment.NewLine}"
            + $"Message: {ex.Message}{Environment.NewLine}"
            + $"Exception: {ex}{Environment.NewLine}";

        File.WriteAllText(path, text);
        return path;
    }
}

