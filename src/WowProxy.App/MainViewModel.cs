using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using WowProxy.App.Models;
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
    private int _logsUpdateScheduled;

    private AppSettings _settings;
    private SingBoxCoreAdapter? _core;

    private string? _singBoxPath;
    private string _mixedPortText;
    private bool _enableClashApi;
    private string _clashApiPortText;
    private string? _clashApiSecret;
    private bool _enableSystemProxy;
    private string _statusText;
    private string? _subscriptionUrl;
    private string _nodeImportText;
    private readonly ObservableCollection<ProxyNodeModel> _nodes;
    private ProxyNodeModel? _selectedNode;
    private string _connectButtonText;
    private string _logLevel;
    private bool _enableDirectCn;
    private bool _enableTun;

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
        _subscriptionUrl = settings.SubscriptionUrl;
        _nodeImportText = string.Empty;
        _nodes = new ObservableCollection<ProxyNodeModel>((settings.Nodes ?? new List<ProxyNode>())
            .Select(n => new ProxyNodeModel(n)));
        _selectedNode = !string.IsNullOrWhiteSpace(settings.SelectedNodeId)
            ? _nodes.FirstOrDefault(n => string.Equals(n.Id, settings.SelectedNodeId, StringComparison.OrdinalIgnoreCase))
            : _nodes.FirstOrDefault();
        _connectButtonText = "连接";
        _logLevel = string.IsNullOrWhiteSpace(settings.LogLevel) ? "info" : settings.LogLevel;
        _enableDirectCn = settings.EnableDirectCn;
        _enableTun = settings.EnableTun;
        _statusText = "未启动";

        if (_enableTun)
        {
            _enableSystemProxy = false;
        }

        BrowseSingBoxCommand = new RelayCommand(_ => BrowseSingBox());
        ConnectCommand = new AsyncRelayCommand(_ => ToggleConnectAsync());
        UpdateSubscriptionCommand = new AsyncRelayCommand(_ => UpdateSubscriptionAsync());
        ImportLinksCommand = new AsyncRelayCommand(_ => ImportLinksAsync());
        ClearNodesCommand = new RelayCommand(_ => ClearNodes());
        TestLatencyCommand = new AsyncRelayCommand(_ => TestLatencyAsync());
        TestSpeedCommand = new AsyncRelayCommand(_ => TestSpeedAsync());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayCommand BrowseSingBoxCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand UpdateSubscriptionCommand { get; }
    public AsyncRelayCommand ImportLinksCommand { get; }
    public RelayCommand ClearNodesCommand { get; }
    public AsyncRelayCommand TestLatencyCommand { get; }
    public AsyncRelayCommand TestSpeedCommand { get; }

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

            // 如果启用了系统代理，且当前是 TUN 模式，则自动关闭 TUN 模式
            if (_enableSystemProxy && _enableTun)
            {
                _enableTun = false;
                OnPropertyChanged(nameof(EnableTun));
            }

            OnPropertyChanged();
            _ = PersistSelectionAsync();
        }
    }

    public string LogLevel
    {
        get => _logLevel;
        set
        {
            if (_logLevel == value)
            {
                return;
            }

            _logLevel = value;
            OnPropertyChanged();
            _ = PersistSelectionAsync();
        }
    }

    public bool EnableDirectCn
    {
        get => _enableDirectCn;
        set
        {
            if (_enableDirectCn == value)
            {
                return;
            }

            _enableDirectCn = value;
            OnPropertyChanged();
            _ = PersistSelectionAsync();
        }
    }

    public bool EnableTun
    {
        get => _enableTun;
        set
        {
            if (_enableTun == value)
            {
                return;
            }

            _enableTun = value;

            // 如果启用了 TUN 模式，强制关闭系统代理
            if (_enableTun && _enableSystemProxy)
            {
                _enableSystemProxy = false;
                OnPropertyChanged(nameof(EnableSystemProxy));
            }

            OnPropertyChanged();
            _ = PersistSelectionAsync();
        }
    }

    public string? SubscriptionUrl
    {
        get => _subscriptionUrl;
        set
        {
            if (_subscriptionUrl == value)
            {
                return;
            }

            _subscriptionUrl = value;
            OnPropertyChanged();
        }
    }

    public string NodeImportText
    {
        get => _nodeImportText;
        set
        {
            if (_nodeImportText == value)
            {
                return;
            }

            _nodeImportText = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<ProxyNodeModel> Nodes => _nodes;

    public ProxyNodeModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (ReferenceEquals(_selectedNode, value) || (_selectedNode is not null && value is not null && _selectedNode.Id == value.Id))
            {
                return;
            }

            _selectedNode = value;
            OnPropertyChanged();
            _ = PersistSelectionAsync();
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

    public string ConnectButtonText
    {
        get => _connectButtonText;
        private set
        {
            if (_connectButtonText == value)
            {
                return;
            }

            _connectButtonText = value;
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
        if (EnableTun && !IsRunningAsAdmin())
        {
            AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Error, "TUN 模式需要管理员权限（请右键以管理员身份运行）。"));
            StatusText = "请以管理员身份运行";
            return;
        }

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

        if (_nodes.Count > 0 && SelectedNode is null)
        {
            StatusText = "请先选择节点";
            return;
        }

        if (!IsLocalPortAvailable(mixedPort))
        {
            AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Error, $"端口被占用：127.0.0.1:{mixedPort}（请改端口或关闭占用进程）"));
            StatusText = "端口被占用";
            return;
        }

        if (SelectedNode is not null)
        {
            AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Info, BuildSelectedNodeSummary(SelectedNode.Node)));
        }

        var secret = EnableClashApi
            ? string.IsNullOrWhiteSpace(ClashApiSecret) ? Guid.NewGuid().ToString("N") : ClashApiSecret!.Trim()
            : null;

        ClashApiSecret = secret;

        var enableSystemProxy = !EnableTun && EnableSystemProxy;

        _settings = new AppSettings(
            SingBoxPath: SingBoxPath,
            MixedPort: mixedPort,
            EnableClashApi: EnableClashApi,
            ClashApiPort: clashApiPort,
            ClashApiSecret: secret,
            EnableSystemProxy: enableSystemProxy,
            SubscriptionUrl: SubscriptionUrl,
            Nodes: _nodes.Select(n => n.Node).ToList(),
            SelectedNodeId: SelectedNode?.Id,
            LogLevel: LogLevel,
            EnableDirectCn: EnableDirectCn,
            EnableTun: EnableTun
        );

        await _settingsStore.SaveAsync(_settings);

        var workDir = Path.Combine(AppDataPaths.GetCoreRoot(), "sing-box");
        Directory.CreateDirectory(workDir);
        var configPath = Path.Combine(workDir, "config.json");

        var configFactory = new SingBoxConfigFactoryV2();
        await StopAsync();

        var maxAttempts = EnableTun ? 3 : 1;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var tunInitIssue = false;

            var runtimeSettings = EnableTun
                ? _settings with { TunInterfaceName = BuildTunInterfaceName(attempt) }
                : _settings with { TunInterfaceName = null };

            // 确保每次重试都用新的 interface_name 重新生成 config.json
            WowProxy.Domain.AppRuntime.TunInterfaceName = runtimeSettings.TunInterfaceName;
            await configFactory.WriteAsync(runtimeSettings, configPath);
            TryAppendTunSummary(configPath);

            _core = new SingBoxCoreAdapter(_settings.SingBoxPath!);
            _core.LogReceived += (_, line) =>
            {
                if (EnableTun)
                {
                    var text = line.Line;
                    if (text.Contains("open tun interface take too much time", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Cannot create a file when that file already exists", StringComparison.OrdinalIgnoreCase))
                    {
                        tunInitIssue = true;
                    }
                }

                AppendLog(line);
            };
            _core.RuntimeInfoChanged += (_, info) => UpdateStatus(info);

            var check = await _core.CheckConfigAsync(configPath, workDir);
            if (!check.IsOk)
            {
                AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Error, check.Stderr.Trim()));
                StatusText = "配置检查失败";
                return;
            }

            await _core.StartAsync(new CoreStartOptions(workDir, configPath));
            await Task.Delay(2500);

            if (_core.RuntimeInfo.State == CoreState.Running)
            {
                if (enableSystemProxy)
                {
                    _systemProxy.EnableGlobalProxy($"127.0.0.1:{mixedPort}");
                }

                StatusText = "运行中";
                await RunSelfTestAsync(mixedPort);
                return;
            }

            if (!EnableTun || !tunInitIssue || attempt == maxAttempts - 1)
            {
                StatusText = "启动失败";
                return;
            }

            AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Warning, "TUN 初始化可能被系统阻塞，自动重试..."));
            await StopAsync();
            await Task.Delay(1200);
        }
    }

    private static bool IsRunningAsAdmin()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string? BuildTunInterfaceName(int attempt)
    {
        // 强制每次都用随机名，彻底规避 already exists
        return "wowproxy-tun-" + Guid.NewGuid().ToString("N").Substring(0, 6);
    }

    private void TryAppendTunSummary(string configPath)
    {
        if (!EnableTun)
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(configPath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("inbounds", out var inbounds) || inbounds.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var inbound in inbounds.EnumerateArray())
            {
                if (!inbound.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (!string.Equals(typeEl.GetString(), "tun", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var iface = inbound.TryGetProperty("interface_name", out var ifaceEl) && ifaceEl.ValueKind == JsonValueKind.String
                    ? ifaceEl.GetString()
                    : null;

                var addr = inbound.TryGetProperty("address", out var addrEl) && addrEl.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", addrEl.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)))
                    : string.Empty;

                AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Info, $"TUN 配置：interface_name={iface ?? "(auto)"} address=[{addr}] auto_route=true"));
                return;
            }
        }
        catch
        {
        }
    }

    private static string BuildSelectedNodeSummary(ProxyNode node)
    {
        var sb = new StringBuilder();
        sb.Append("使用节点：")
            .Append(node.Type)
            .Append("  ")
            .Append(node.Name)
            .Append("  server=")
            .Append(node.Server)
            .Append(':')
            .Append(node.Port);

        var isWs = string.Equals(node.TransportType, "ws", StringComparison.OrdinalIgnoreCase);
        if (node.TlsEnabled || !string.IsNullOrWhiteSpace(node.TlsServerName) || string.Equals(node.Security, "reality", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("  tls=")
                .Append(string.Equals(node.Security, "reality", StringComparison.OrdinalIgnoreCase) ? "reality" : "on");
            if (!string.IsNullOrWhiteSpace(node.TlsServerName))
            {
                sb.Append("  sni=").Append(node.TlsServerName);
            }
            if (!string.IsNullOrWhiteSpace(node.UtlsFingerprint))
            {
                sb.Append("  fp=").Append(node.UtlsFingerprint);
            }
            if (!string.IsNullOrWhiteSpace(node.TlsAlpn))
            {
                sb.Append("  alpn=").Append(node.TlsAlpn);
            }
            else if (isWs)
            {
                sb.Append("  alpn=http/1.1(auto)");
            }
            if (node.TlsInsecure)
            {
                sb.Append("  insecure=true");
            }
        }

        if (!string.IsNullOrWhiteSpace(node.TransportType))
        {
            sb.Append("  transport=").Append(node.TransportType);
        }
        if (!string.IsNullOrWhiteSpace(node.TransportHost))
        {
            sb.Append("  host=").Append(node.TransportHost);
        }
        if (!string.IsNullOrWhiteSpace(node.TransportPath))
        {
            var path = node.TransportPath;
            var queryIndex = path.IndexOf('?');
            if (queryIndex >= 0)
            {
                var query = path[(queryIndex + 1)..];
                foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (part.StartsWith("ed=", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append("  ed=").Append(part["ed=".Length..]);
                        break;
                    }
                }

                path = path[..queryIndex];
            }

            sb.Append("  path=").Append(path);
        }

        return sb.ToString();
    }

    private async Task UpdateSubscriptionAsync()
    {
        var url = SubscriptionUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText = "请先填写订阅 URL";
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            StatusText = "订阅 URL 无效";
            return;
        }

        StatusText = "更新订阅中...";
        AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Info, $"更新订阅：{SanitizeUrlForLog(url)}"));

        var (nodes, errors) = await NodeImport.LoadFromSubscriptionAsync(url, CancellationToken.None);

        Application.Current.Dispatcher.Invoke(() =>
        {
            _nodes.Clear();
            foreach (var n in nodes)
            {
                _nodes.Add(new ProxyNodeModel(n));
            }

            if (!string.IsNullOrWhiteSpace(_settings.SelectedNodeId))
            {
                SelectedNode = _nodes.FirstOrDefault(n => string.Equals(n.Id, _settings.SelectedNodeId, StringComparison.OrdinalIgnoreCase))
                    ?? _nodes.FirstOrDefault();
            }
            else
            {
                SelectedNode = _nodes.FirstOrDefault();
            }
        });

        _settings = _settings with
        {
            SubscriptionUrl = url,
            Nodes = nodes,
            SelectedNodeId = SelectedNode?.Id,
        };

        await _settingsStore.SaveAsync(_settings);

        AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Info, $"订阅更新完成：{nodes.Count} 个节点"));
        foreach (var e in errors.Take(10))
        {
            AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Warning, e));
        }

        StatusText = nodes.Count == 0 ? "订阅为空或解析失败" : "订阅已更新";
    }

    private static string SanitizeUrlForLog(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var safe = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;

        return safe.ToString();
    }

    private async Task ImportLinksAsync()
    {
        var text = NodeImportText;
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText = "请粘贴节点链接";
            return;
        }

        var (nodes, errors) = NodeImport.ParseText(text);

        var merged = _nodes.Select(m => m.Node).ToList();
        foreach (var node in nodes)
        {
            if (merged.Any(x => string.Equals(x.Id, node.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            merged.Add(node);
        }

        merged = merged
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Application.Current.Dispatcher.Invoke(() =>
        {
            _nodes.Clear();
            foreach (var n in merged)
            {
                _nodes.Add(new ProxyNodeModel(n));
            }

            SelectedNode ??= _nodes.FirstOrDefault();
            NodeImportText = string.Empty;
        });

        _settings = _settings with
        {
            Nodes = merged,
            SelectedNodeId = SelectedNode?.Id,
        };

        await _settingsStore.SaveAsync(_settings);

        AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Info, $"已导入：{nodes.Count}，节点总数：{merged.Count}"));
        foreach (var e in errors.Take(10))
        {
            AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Warning, e));
        }

        StatusText = "已导入节点";
    }

    private async Task ApplyNodeAsync()
    {
        if (SelectedNode is null)
        {
            StatusText = "请先选择节点";
            return;
        }

        await StartAsync();
    }

    private void ClearNodes()
    {
        _nodes.Clear();
        SelectedNode = null;
        _settings = _settings with
        {
            Nodes = new List<ProxyNode>(),
            SelectedNodeId = null,
        };
        _ = _settingsStore.SaveAsync(_settings);
        StatusText = "节点已清空";
    }

    private async Task PersistSelectionAsync()
    {
        try
        {
            _settings = _settings with
            {
                Nodes = _nodes.Select(n => n.Node).ToList(),
                SelectedNodeId = SelectedNode?.Id,
                SubscriptionUrl = SubscriptionUrl,
                LogLevel = LogLevel,
                EnableDirectCn = EnableDirectCn,
                EnableTun = EnableTun,
                EnableSystemProxy = EnableSystemProxy,
            };
            await _settingsStore.SaveAsync(_settings);
        }
        catch
        {
        }
    }

    private async Task TestLatencyAsync()
    {
        if (string.IsNullOrWhiteSpace(SingBoxPath))
        {
            StatusText = "请先设置 sing-box 路径";
            return;
        }

        if (_nodes.Count == 0) return;

        StatusText = "正在测试延迟...";
        await NodeTester.TestLatencyAsync(_nodes, SingBoxPath);
        StatusText = "延迟测试完成";
    }

    private async Task TestSpeedAsync()
    {
        if (string.IsNullOrWhiteSpace(SingBoxPath))
        {
            StatusText = "请先设置 sing-box 路径";
            return;
        }

        if (_nodes.Count == 0) return;

        StatusText = "正在测试速度...";
        await NodeTester.TestSpeedAsync(_nodes, SingBoxPath);
        StatusText = "速度测试完成";
    }

    private static bool IsLocalPortAvailable(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
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
        Application.Current.Dispatcher.BeginInvoke(() =>
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

            ConnectButtonText = info.State == CoreState.Running ? "断开" : "连接";
        });
    }

    private void AppendLog(CoreLogLine line)
    {
        lock (_gate)
        {
            _logs.Append('[').Append(line.Level).Append("] ").AppendLine(line.Line);

            const int MaxChars = 120_000;
            if (_logs.Length > MaxChars)
            {
                _logs.Remove(0, _logs.Length - MaxChars);
            }
        }

        if (Interlocked.Exchange(ref _logsUpdateScheduled, 1) == 0)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _logsUpdateScheduled, 0);
                OnPropertyChanged(nameof(LogsText));
            });
        }
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

    private async Task ToggleConnectAsync()
    {
        if (_core is not null && _core.RuntimeInfo.State == CoreState.Running)
        {
            await StopAsync();
            return;
        }

        await StartAsync();
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
