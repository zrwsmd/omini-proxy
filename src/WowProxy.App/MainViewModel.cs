using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using WowProxy.Core.Abstractions;
using WowProxy.Core.SingBox;
using WowProxy.Domain;
using WowProxy.Infrastructure;

namespace WowProxy.App;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly JsonSettingsStore _settingsStore;
    private readonly WindowsSystemProxy _systemProxy;
    private readonly StringBuilder _logs = new();
    private readonly object _gate = new();

    private AppSettings _settings;
    private SingBoxCoreAdapter? _core;

    private string? _singBoxPath;
    private string _mixedPortText;
    private bool _enableClashApi;
    private string _clashApiPortText;
    private string? _clashApiSecret;
    private bool _enableSystemProxy;
    private string _statusText;

    public MainViewModel(JsonSettingsStore settingsStore, WindowsSystemProxy systemProxy, AppSettings settings)
    {
        _settingsStore = settingsStore;
        _systemProxy = systemProxy;
        _settings = settings;

        _singBoxPath = settings.SingBoxPath;
        _mixedPortText = settings.MixedPort.ToString();
        _enableClashApi = settings.EnableClashApi;
        _clashApiPortText = settings.ClashApiPort.ToString();
        _clashApiSecret = settings.ClashApiSecret;
        _enableSystemProxy = settings.EnableSystemProxy;
        _statusText = "未启动";

        BrowseSingBoxCommand = new RelayCommand(_ => BrowseSingBox());
        StartCommand = new AsyncRelayCommand(_ => StartAsync());
        StopCommand = new AsyncRelayCommand(_ => StopAsync());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayCommand BrowseSingBoxCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }

    public string? SingBoxPath
    {
        get => _singBoxPath;
        set
        {
            if (_singBoxPath == value)
            {
                return;
            }

            _singBoxPath = value;
            OnPropertyChanged();
        }
    }

    public string MixedPortText
    {
        get => _mixedPortText;
        set
        {
            if (_mixedPortText == value)
            {
                return;
            }

            _mixedPortText = value;
            OnPropertyChanged();
        }
    }

    public bool EnableClashApi
    {
        get => _enableClashApi;
        set
        {
            if (_enableClashApi == value)
            {
                return;
            }

            _enableClashApi = value;
            OnPropertyChanged();
        }
    }

    public string ClashApiPortText
    {
        get => _clashApiPortText;
        set
        {
            if (_clashApiPortText == value)
            {
                return;
            }

            _clashApiPortText = value;
            OnPropertyChanged();
        }
    }

    public string? ClashApiSecret
    {
        get => _clashApiSecret;
        set
        {
            if (_clashApiSecret == value)
            {
                return;
            }

            _clashApiSecret = value;
            OnPropertyChanged();
        }
    }

    public bool EnableSystemProxy
    {
        get => _enableSystemProxy;
        set
        {
            if (_enableSystemProxy == value)
            {
                return;
            }

            _enableSystemProxy = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public string LogsText
    {
        get
        {
            lock (_gate)
            {
                return _logs.ToString();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _systemProxy.RestoreFromSnapshotIfAny();
    }

    private void BrowseSingBox()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 sing-box.exe",
            Filter = "sing-box.exe|sing-box.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            SingBoxPath = dialog.FileName;
        }
    }

    private async Task StartAsync()
    {
        if (!TryParsePorts(out var mixedPort, out var clashApiPort, out var error))
        {
            StatusText = error;
            return;
        }

        if (string.IsNullOrWhiteSpace(SingBoxPath))
        {
            StatusText = "请先选择 sing-box.exe";
            return;
        }

        var secret = EnableClashApi
            ? string.IsNullOrWhiteSpace(ClashApiSecret) ? Guid.NewGuid().ToString("N") : ClashApiSecret!.Trim()
            : null;

        ClashApiSecret = secret;

        _settings = new AppSettings(
            SingBoxPath: SingBoxPath,
            MixedPort: mixedPort,
            EnableClashApi: EnableClashApi,
            ClashApiPort: clashApiPort,
            ClashApiSecret: secret,
            EnableSystemProxy: EnableSystemProxy
        );

        await _settingsStore.SaveAsync(_settings);

        var workDir = Path.Combine(AppDataPaths.GetCoreRoot(), "sing-box");
        Directory.CreateDirectory(workDir);
        var configPath = Path.Combine(workDir, "config.json");

        var configFactory = new SingBoxConfigFactory();
        await configFactory.WriteAsync(_settings, configPath);

        await StopAsync();

        _core = new SingBoxCoreAdapter(_settings.SingBoxPath!);
        _core.LogReceived += (_, line) => AppendLog(line);
        _core.RuntimeInfoChanged += (_, info) => UpdateStatus(info);

        var check = await _core.CheckConfigAsync(configPath, workDir);
        if (!check.IsOk)
        {
            AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Error, check.Stderr.Trim()));
            StatusText = "配置检查失败";
            return;
        }

        await _core.StartAsync(new CoreStartOptions(workDir, configPath));

        if (EnableSystemProxy)
        {
            _systemProxy.EnableGlobalProxy($"127.0.0.1:{mixedPort}");
        }

        StatusText = "运行中";
        await RunSelfTestAsync(mixedPort);
    }

    private async Task StopAsync()
    {
        if (_enableSystemProxy)
        {
            _systemProxy.DisableAndRestore();
        }

        if (_core is not null)
        {
            await _core.StopAsync();
            await _core.DisposeAsync();
            _core = null;
        }

        StatusText = "已停止";
    }

    private void UpdateStatus(CoreRuntimeInfo info)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusText = info.State switch
            {
                CoreState.Stopped => "已停止",
                CoreState.Starting => "启动中",
                CoreState.Running => $"运行中 (PID {info.ProcessId})",
                CoreState.Stopping => "停止中",
                CoreState.Faulted => $"异常：{info.LastError}",
                _ => info.State.ToString(),
            };
        });
    }

    private void AppendLog(CoreLogLine line)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            lock (_gate)
            {
                _logs.Append('[').Append(line.Level).Append("] ").AppendLine(line.Line);

                const int MaxChars = 200_000;
                if (_logs.Length > MaxChars)
                {
                    _logs.Remove(0, _logs.Length - MaxChars);
                }
            }

            OnPropertyChanged(nameof(LogsText));
        });
    }

    private bool TryParsePorts(out int mixedPort, out int clashApiPort, out string error)
    {
        mixedPort = 0;
        clashApiPort = 0;
        error = string.Empty;

        if (!int.TryParse(MixedPortText, out mixedPort) || mixedPort is < 1 or > 65535)
        {
            error = "Mixed 端口无效";
            return false;
        }

        if (!int.TryParse(ClashApiPortText, out clashApiPort) || clashApiPort is < 1 or > 65535)
        {
            error = "Clash API 端口无效";
            return false;
        }

        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private async Task RunSelfTestAsync(int mixedPort)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var connectTask = tcp.ConnectAsync("127.0.0.1", mixedPort);
            var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(2)));
            if (completed != connectTask)
            {
                AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Error, $"自测失败：本地端口未监听 127.0.0.1:{mixedPort}"));
                return;
            }
        }
        catch (Exception ex)
        {
            AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Error, $"自测失败：无法连接本地端口 127.0.0.1:{mixedPort}（{ex.Message}）"));
            return;
        }

        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy($"http://127.0.0.1:{mixedPort}"),
                UseProxy = true,
            };

            using var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(6),
            };

            using var resp = await http.GetAsync("http://example.com/");
            var ok = resp.IsSuccessStatusCode;
            AppendLog(new CoreLogLine(DateTimeOffset.Now, ok ? CoreLogLevel.Info : CoreLogLevel.Error, $"自测 HTTP 结果：{(int)resp.StatusCode} {resp.ReasonPhrase}"));
        }
        catch (Exception ex)
        {
            AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Warning, $"自测 HTTP 请求失败：{ex.Message}"));
        }
    }
}

public sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand : System.Windows.Input.ICommand
{
    private readonly Func<object?, Task> _executeAsync;
    private readonly Func<object?, bool>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
        => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        try
        {
            _isRunning = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            await _executeAsync(parameter);
        }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
