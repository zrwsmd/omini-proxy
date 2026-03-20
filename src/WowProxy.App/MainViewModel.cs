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
using WowProxy.App.ViewModels;
using WowProxy.Core.Abstractions;
using WowProxy.Core.Abstractions.Models;
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
    private readonly SemaphoreSlim _coreLock = new(1, 1);
    private int _logsUpdateScheduled;
    private System.Threading.Timer? _statusRestoreTimer;

    private AppSettings _settings;
    private SingBoxCoreAdapter? _core;
    private CoreState _coreState = CoreState.Stopped;
    private DashboardViewModel _dashboard;
    private SettingsViewModel _settingsViewModel;
    private ChainProxyViewModel _chainProxy;
    private MitmCaptureViewModel _mitmCapture;
    private UserRulesViewModel _userRules;
    private Services.NodeHealthMonitor? _healthMonitor;

    private bool _enableSystemProxy;
    private string _statusText;
    private int _mainTabSelectedIndex;
    private string? _subscriptionUrl;
    private string _nodeImportText;
    private readonly ObservableCollection<ProxyNodeModel> _nodes;
    private ObservableCollection<ProxyNodeModel> _filteredNodes;
    private ProxyNodeModel? _selectedNode;
    private ProxyNodeModel? _activeNode;
    private readonly ObservableCollection<ProxyNodeModel> _selectedNodes = new();
    private string _connectButtonText;
    private bool _enableTun;
    private string _selectedGroup = "全部";
    private readonly ObservableCollection<string> _nodeGroups = new() { "全部" };
    private readonly ObservableCollection<SubscriptionEntry> _subscriptionGroups = new();

    public MainViewModel(JsonSettingsStore settingsStore, WindowsSystemProxy systemProxy, AppSettings settings)
    {
        _settingsStore = settingsStore;
        _systemProxy = systemProxy;
        _settings = settings;

        _enableSystemProxy = settings.EnableSystemProxy;
        _subscriptionUrl = settings.SubscriptionUrl;
        _nodeImportText = string.Empty;
        _nodes = new ObservableCollection<ProxyNodeModel>((settings.Nodes ?? new List<ProxyNode>())
            .Select(n => new ProxyNodeModel(n)));
        _filteredNodes = new ObservableCollection<ProxyNodeModel>(_nodes);

        // Restore subscription groups
        if (settings.SubscriptionGroups != null)
        {
            foreach (var entry in settings.SubscriptionGroups)
            {
                _subscriptionGroups.Add(entry);
                if (!_nodeGroups.Contains(entry.GroupName))
                    _nodeGroups.Add(entry.GroupName);
            }
        }

        // Restore Active Node from settings
        if (!string.IsNullOrWhiteSpace(settings.SelectedNodeId))
        {
            var activeNode = _nodes.FirstOrDefault(n => string.Equals(n.Id, settings.SelectedNodeId, StringComparison.OrdinalIgnoreCase));
            if (activeNode != null)
            {
                ActiveNode = activeNode;
            }
        }

        // Select the active node by default if available, otherwise the first one
        _selectedNode = _activeNode ?? _filteredNodes.FirstOrDefault();

        _connectButtonText = "连接";
        _enableTun = settings.EnableTun;
        _statusText = "未启动";

        ConnectCommand = new AsyncRelayCommand(_ => ToggleConnectAsync());
        UpdateSubscriptionCommand = new AsyncRelayCommand(_ => UpdateSubscriptionAsync());
        ImportLinksCommand = new AsyncRelayCommand(_ => ImportLinksAsync());
        RemoveNodeCommand = new RelayCommand(_ => RemoveNode());
        RemoveSelectedNodesCommand = new RelayCommand(_ => RemoveSelectedNodes());
        SetActiveNodeCommand = new AsyncRelayCommand(_ => SetActiveNodeAsync());
        ClearNodesCommand = new RelayCommand(_ => ClearNodes());
        TestLatencyCommand = new AsyncRelayCommand(_ => TestLatencyAsync());
        TestSpeedCommand = new AsyncRelayCommand(_ => TestSpeedAsync());
        CopyNodeLinkCommand = new RelayCommand(_ => CopyNodeLink());
        SetGroupCommand = new RelayCommand(_ => SetGroupForSelectedNodes());
        CreateGroupCommand = new RelayCommand(_ => CreateNewGroup());
        RenameGroupCommand = new RelayCommand(p => RenameGroup(p as string));
        DeleteGroupCommand = new RelayCommand(p => DeleteGroup(p as string));

        _dashboard = new DashboardViewModel(this);
        _settingsViewModel = new SettingsViewModel(this, settings);
        _chainProxy = new ChainProxyViewModel(this, settings.EnableChainProxy, settings.ChainProxyNodeIds?.ToList());
        _mitmCapture = new MitmCaptureViewModel();
        _userRules = new UserRulesViewModel(settings.UserRules, () => _ = PersistSelectionAsync());
        
        // 初始化节点健康监控
        _healthMonitor = new Services.NodeHealthMonitor(
            getCurrentNode: () => ActiveNode,
            getAvailableNodes: () => _filteredNodes.ToList(),
            switchToNode: async (node) => await SwitchToNodeAsync(node),
            logMessage: (msg) => AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Info, msg))
        );
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DashboardViewModel Dashboard => _dashboard;
    public SettingsViewModel Settings => _settingsViewModel;
    public ChainProxyViewModel ChainProxy => _chainProxy;
    public MitmCaptureViewModel MitmCapture => _mitmCapture;
    public UserRulesViewModel UserRules => _userRules;

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand UpdateSubscriptionCommand { get; }
    public AsyncRelayCommand ImportLinksCommand { get; }
    public RelayCommand RemoveNodeCommand { get; }
    public RelayCommand RemoveSelectedNodesCommand { get; }
    public AsyncRelayCommand SetActiveNodeCommand { get; }
    public RelayCommand ClearNodesCommand { get; }
    public AsyncRelayCommand TestLatencyCommand { get; }
    public AsyncRelayCommand TestSpeedCommand { get; }
    public RelayCommand CopyNodeLinkCommand { get; }
    public RelayCommand SetGroupCommand { get; }
    public RelayCommand CreateGroupCommand { get; }
    public RelayCommand RenameGroupCommand { get; }
    public RelayCommand DeleteGroupCommand { get; }

    public string? SingBoxPath => _settingsViewModel.SingBoxPath;
    public bool EnableClashApi => _settingsViewModel.EnableClashApi;
    public string? ClashApiSecret
    {
        get => _settingsViewModel.ClashApiSecret;
        set => _settingsViewModel.ClashApiSecret = value;
    }
    public int ClashApiPort
    {
        get
        {
            int.TryParse(_settingsViewModel.ClashApiPortText, out var port);
            return port;
        }
    }

    public void NotifySettingsChanged()
    {
        _ = PersistSelectionAsync();
    }

    public bool EnableSystemProxy
    {
        get => _enableSystemProxy;
        set
        {
            if (_enableSystemProxy == value) return;
            _enableSystemProxy = value;
            OnPropertyChanged();
            _ = PersistSelectionAsync();
        }
    }

    public string LogLevel => _settingsViewModel.LogLevel;
    public bool EnableDirectCn => _settingsViewModel.EnableDirectCn;

    public bool EnableTun
    {
        get => _enableTun;
        set
        {
            if (_enableTun == value) return;
            _enableTun = value;
            OnPropertyChanged();
            _ = PersistSelectionAsync();
        }
    }

    public int MainTabSelectedIndex
    {
        get => _mainTabSelectedIndex;
        set { if (_mainTabSelectedIndex != value) { _mainTabSelectedIndex = value; OnPropertyChanged(); } }
    }

    public string? SubscriptionUrl
    {
        get => _subscriptionUrl;
        set
        {
            if (_subscriptionUrl == value) return;
            _subscriptionUrl = value;
            OnPropertyChanged();
        }
    }

    public string NodeImportText
    {
        get => _nodeImportText;
        set
        {
            if (_nodeImportText == value) return;
            _nodeImportText = value;
            OnPropertyChanged();
        }
    }

    // All nodes (backing store)
    public ObservableCollection<ProxyNodeModel> Nodes => _nodes;

    // Selected nodes (synced from DataGrid.SelectionChanged in code-behind)
    public ObservableCollection<ProxyNodeModel> SelectedNodes => _selectedNodes;

    // Filtered nodes (DataGrid binds to this)
    public ObservableCollection<ProxyNodeModel> FilteredNodes
    {
        get => _filteredNodes;
        private set
        {
            _filteredNodes = value;
            OnPropertyChanged();
        }
    }

    // Group tab names: "全部" + each subscription group name
    public ObservableCollection<string> NodeGroups => _nodeGroups;

    // Currently selected group tab
    public string SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            var newVal = value ?? "全部";
            if (_selectedGroup == newVal) return;
            _selectedGroup = newVal;
            OnPropertyChanged();
            RebuildFilteredNodes();
            // Restore selection to active node if visible, else first
            SelectedNode = _filteredNodes.FirstOrDefault(n => n.IsActive) ?? _filteredNodes.FirstOrDefault();
        }
    }

    public ProxyNodeModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (ReferenceEquals(_selectedNode, value) || (_selectedNode is not null && value is not null && _selectedNode.Id == value.Id))
                return;
            _selectedNode = value;
            OnPropertyChanged();
        }
    }

    public ProxyNodeModel? ActiveNode
    {
        get => _activeNode;
        private set
        {
            if (ReferenceEquals(_activeNode, value)) return;
            if (_activeNode != null) _activeNode.IsActive = false;
            _activeNode = value;
            if (_activeNode != null) _activeNode.IsActive = true;
            OnPropertyChanged();
            _ = PersistSelectionAsync();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
            
            // Auto-restore status text after temporary messages
            if (ShouldAutoRestoreStatus(value))
            {
                _statusRestoreTimer?.Dispose();
                _statusRestoreTimer = new System.Threading.Timer(_ =>
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        UpdateStatusFromCoreState();
                    });
                }, null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            }
        }
    }

    private bool ShouldAutoRestoreStatus(string statusText)
    {
        // Auto-restore for temporary operation messages
        return statusText.Contains("已创建分组") ||
               statusText.Contains("已删除分组") ||
               statusText.Contains("已导入节点") ||
               statusText.Contains("节点已移除") ||
               statusText.Contains("已移除");
    }

    private void UpdateStatusFromCoreState()
    {
        _statusText = _coreState switch
        {
            CoreState.Running => "运行中",
            CoreState.Starting => "正在启动...",
            CoreState.Stopping => "正在停止...",
            CoreState.Faulted => "启动失败",
            _ => "已停止"
        };
        OnPropertyChanged(nameof(StatusText));
    }

    public string ConnectButtonText
    {
        get => _connectButtonText;
        private set
        {
            if (_connectButtonText == value) return;
            _connectButtonText = value;
            OnPropertyChanged();
        }
    }

    public CoreState CoreState
    {
        get => _coreState;
        private set
        {
            if (_coreState == value) return;
            _coreState = value;
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
        _statusRestoreTimer?.Dispose();
        _healthMonitor?.Dispose();
        _dashboard.Dispose();
        await StopAsync();
        _systemProxy.RestoreFromSnapshotIfAny();
    }

    /// <summary>
    /// 切换到指定节点（用于自动故障转移）
    /// </summary>
    private async Task SwitchToNodeAsync(ProxyNodeModel node)
    {
        if (node == null || node == ActiveNode) return;

        ActiveNode = node;
        
        // 如果代理正在运行，重启以应用新节点
        if (_coreState == CoreState.Running)
        {
            await StopAsync();
            await Task.Delay(1000);
            await StartAsync();
        }
    }

    // ── Group helpers ─────────────────────────────────────────────────────────

    private void RebuildFilteredNodes()
    {
        var filtered = _selectedGroup == "全部"
            ? _nodes.ToList()
            : _nodes.Where(n => string.Equals(n.Node.SubscriptionGroup, _selectedGroup, StringComparison.OrdinalIgnoreCase)).ToList();

        _filteredNodes.Clear();
        foreach (var n in filtered)
            _filteredNodes.Add(n);
    }

    private void RebuildNodeGroups()
    {
        // Collect groups from both nodes and subscription groups (to keep empty groups)
        var nodeGroups = _nodes
            .Select(n => n.Node.SubscriptionGroup)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var subscriptionGroupNames = _subscriptionGroups
            .Select(s => s.GroupName)
            .Where(g => !string.IsNullOrWhiteSpace(g));

        var allGroups = nodeGroups.Union(subscriptionGroupNames, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g)
            .ToList();

        // Remove tabs that no longer exist in either nodes or subscription groups (except "全部")
        for (var i = _nodeGroups.Count - 1; i >= 1; i--)
        {
            if (!allGroups.Contains(_nodeGroups[i], StringComparer.OrdinalIgnoreCase))
                _nodeGroups.RemoveAt(i);
        }

        // Add new tabs
        foreach (var g in allGroups)
        {
            if (!_nodeGroups.Contains(g!, StringComparer.OrdinalIgnoreCase))
                _nodeGroups.Add(g!);
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private void RenameGroup(string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName) || groupName == "全部") return;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var dialog = new Views.PromptWindow("重命名分组", $"请输入分组“{groupName}”内的新名称:", groupName);
            if (System.Windows.Application.Current.MainWindow != null)
                dialog.Owner = System.Windows.Application.Current.MainWindow;

            if (dialog.ShowDialog() == true)
            {
                var newName = dialog.InputText.Trim();
                if (string.IsNullOrEmpty(newName) || string.Equals(newName, groupName, StringComparison.OrdinalIgnoreCase)) return;

                // 1. Update subscription groups
                var subEntry = _subscriptionGroups.FirstOrDefault(g => string.Equals(g.GroupName, groupName, StringComparison.OrdinalIgnoreCase));
                if (subEntry != null)
                {
                    _subscriptionGroups.Remove(subEntry);
                    _subscriptionGroups.Add(subEntry with { GroupName = newName });
                }

                // 2. Update all nodes with this group name
                foreach (var proxyNode in _nodes.Where(n => string.Equals(n.Node.SubscriptionGroup, groupName, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    proxyNode.Node = proxyNode.Node with { SubscriptionGroup = newName };
                }

                bool wasSelected = string.Equals(SelectedGroup, groupName, StringComparison.OrdinalIgnoreCase);

                RebuildNodeGroups();

                if (wasSelected) SelectedGroup = newName;
                else RebuildFilteredNodes();

                _ = PersistSelectionAsync();
            }
        });
    }

    private void DeleteGroup(string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName) || groupName == "全部") return;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var nodesCount = _nodes.Count(n => string.Equals(n.Node.SubscriptionGroup, groupName, StringComparison.OrdinalIgnoreCase));
            var message = nodesCount > 0
                ? $"确定要删除分组 \"{groupName}\" 及其包含的 {nodesCount} 个节点吗？\n\n⚠️ 注意：节点及关联信息将被彻底移除，此操作不可撤销。"
                : $"确定要删除空分组 \"{groupName}\" 吗？";

            var dialog = new Views.ConfirmWindow(
                "⚠️ 删除分组确认",
                message,
                "删除",
                isDangerous: true);
            
            if (System.Windows.Application.Current.MainWindow != null)
                dialog.Owner = System.Windows.Application.Current.MainWindow;

            if (dialog.ShowDialog() == true && dialog.IsConfirmed)
            {
                // 1. Remove subscription mapping
                var subEntry = _subscriptionGroups.FirstOrDefault(g => string.Equals(g.GroupName, groupName, StringComparison.OrdinalIgnoreCase));
                if (subEntry != null)
                {
                    _subscriptionGroups.Remove(subEntry);
                }

                // 2. Remove associated nodes
                var nodesToRemove = _nodes.Where(n => string.Equals(n.Node.SubscriptionGroup, groupName, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var node in nodesToRemove)
                {
                    _nodes.Remove(node);
                    if (node.IsActive)
                    {
                        ActiveNode = null;
                    }
                }

                RebuildNodeGroups();
                if (string.Equals(SelectedGroup, groupName, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedGroup = "全部";
                }
                else
                {
                    RebuildFilteredNodes();
                }
                
                _ = PersistSelectionAsync();
                
                // Show status feedback
                StatusText = nodesCount > 0 
                    ? $"已删除分组 \"{groupName}\" 及其 {nodesCount} 个节点"
                    : $"已删除分组 \"{groupName}\"";
            }
        });
    }

    private void CreateNewGroup()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var dialog = new Views.PromptWindow("新建分组", "请输入新分组名称:", "");
            if (System.Windows.Application.Current.MainWindow != null)
                dialog.Owner = System.Windows.Application.Current.MainWindow;

            if (dialog.ShowDialog() == true)
            {
                var newGroup = dialog.InputText.Trim();
                if (string.IsNullOrEmpty(newGroup)) return;

                if (newGroup == "全部")
                {
                    StatusText = "分组名称不能为 \"全部\"";
                    return;
                }

                // Check if group already exists
                if (_subscriptionGroups.Any(g => string.Equals(g.GroupName, newGroup, StringComparison.OrdinalIgnoreCase)))
                {
                    StatusText = $"分组 \"{newGroup}\" 已存在";
                    return;
                }

                // Add to subscription groups and immediately add to NodeGroups for display
                _subscriptionGroups.Add(new SubscriptionEntry(newGroup, ""));
                
                // Immediately add to NodeGroups if not already present
                if (!_nodeGroups.Contains(newGroup, StringComparer.OrdinalIgnoreCase))
                {
                    _nodeGroups.Add(newGroup);
                }
                
                // Switch to the new group
                SelectedGroup = newGroup;
                _ = PersistSelectionAsync();
                StatusText = $"已创建分组 \"{newGroup}\"";
            }
        });
    }

    private void SetGroupForSelectedNodes()
    {
        if (_selectedNodes.Count == 0) return;

        var currentGroup = _selectedNodes.First().Node.SubscriptionGroup ?? "";
        
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            // Collect all custom/subscription groups, exclude the special "全部" tab
            var availableGroups = _nodeGroups.Where(g => g != "全部").ToList();

            var dialog = new Views.SelectGroupWindow(availableGroups, currentGroup);
            if (System.Windows.Application.Current.MainWindow != null)
                dialog.Owner = System.Windows.Application.Current.MainWindow;

            if (dialog.ShowDialog() == true)
            {
                var newGroup = dialog.SelectedGroup.Trim();
                // Ensure null rather than empty string for consistency
                if (string.IsNullOrEmpty(newGroup)) newGroup = null;

                foreach (var proxyNode in _selectedNodes.ToList())
                {
                    proxyNode.Node = proxyNode.Node with { SubscriptionGroup = newGroup };
                }

                // Since we mutated the nodes, rebuild the group tabs and filtered list and save
                RebuildNodeGroups();
                RebuildFilteredNodes();
                _ = PersistSelectionAsync();
            }
        });
    }

    private async Task StartAsync()
    {
        await _coreLock.WaitAsync();
        try
        {
            await StartLockedAsync();
        }
        finally
        {
            _coreLock.Release();
        }
    }

    private async Task StartLockedAsync()
    {
        if (EnableTun && !IsRunningAsAdmin())
        {
            AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Error, "TUN 模式需要管理员权限（请右键以管理员身份运行）。"));
            CoreState = CoreState.Faulted;
            StatusText = "请以管理员身份运行";
            return;
        }

        if (!TryParsePorts(out var mixedPort, out var clashApiPort, out var error))
        {
            CoreState = CoreState.Faulted;
            StatusText = error;
            return;
        }

        if (string.IsNullOrWhiteSpace(SingBoxPath))
        {
            CoreState = CoreState.Faulted;
            StatusText = "请先选择 sing-box.exe";
            return;
        }

        // Chain proxy validation
        if (_chainProxy.EnableChainProxy)
        {
            var chainIds = _chainProxy.GetChainNodeIds();
            if (chainIds.Count < 2)
            {
                CoreState = CoreState.Faulted;
                StatusText = "链式代理至少需要 2 个节点";
                return;
            }
        }
        else if (_nodes.Count > 0 && ActiveNode is null)
        {
            CoreState = CoreState.Faulted;
            StatusText = "请先设置活动节点";
            return;
        }

        if (!IsLocalPortAvailable(mixedPort))
        {
            AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Error, $"端口被占用：127.0.0.1:{mixedPort}（请改端口或关闭占用进程）"));
            CoreState = CoreState.Faulted;
            StatusText = "端口被占用";
            return;
        }

        if (_chainProxy.EnableChainProxy)
        {
            var chainNames = _chainProxy.ChainNodes.Select(c => c.DisplayName).ToList();
            AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Info,
                $"链式代理模式：本地 → {string.Join(" → ", chainNames)} → 目标网站"));
        }
        else if (ActiveNode is not null)
        {
            AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Info, BuildSelectedNodeSummary(ActiveNode.Node)));
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
            EnableSystemProxy: EnableSystemProxy,
            SubscriptionUrl: SubscriptionUrl,
            Nodes: _nodes.Select(n => n.Node).ToList(),
            SelectedNodeId: ActiveNode?.Id,
            LogLevel: LogLevel,
            EnableDirectCn: EnableDirectCn,
            EnableTun: EnableTun,
            TunInterfaceName: null,
            BypassTunProcesses: null,
            SubscriptionGroups: _subscriptionGroups.ToList(),
            EnableChainProxy: _chainProxy.EnableChainProxy,
            ChainProxyNodeIds: _chainProxy.GetChainNodeIds()
        );

        await _settingsStore.SaveAsync(_settings);

        var workDir = Path.Combine(AppDataPaths.GetCoreRoot(), "sing-box");
        Directory.CreateDirectory(workDir);
        var configPath = Path.Combine(workDir, "config.json");

        var configFactory = new SingBoxConfigFactoryV2();

        if (_core is not null)
        {
            await _core.StopAsync();
            await _core.DisposeAsync();
            _core = null;
        }

        CoreState = CoreState.Starting;
        var maxAttempts = EnableTun ? 3 : 1;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var tunInitIssue = false;

            var runtimeSettings = EnableTun
                ? _settings with { TunInterfaceName = BuildTunInterfaceName(attempt) }
                : _settings with { TunInterfaceName = null };

            WowProxy.Domain.AppRuntime.TunInterfaceName = runtimeSettings.TunInterfaceName;
            await configFactory.WriteAsync(runtimeSettings, configPath);
            TryAppendTunSummary(configPath);

            var core = new SingBoxCoreAdapter(_settings.SingBoxPath!);
            core.LogReceived += (_, line) =>
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
            core.RuntimeInfoChanged += (_, info) => UpdateStatus(info);

            var check = await core.CheckConfigAsync(configPath, workDir);
            if (!check.IsOk)
            {
                AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Error, check.Stderr.Trim()));
                CoreState = CoreState.Faulted;
                StatusText = "配置检查失败";
                return;
            }

            await core.StartAsync(new CoreStartOptions(workDir, configPath));
            await Task.Delay(2500);

            if (core.RuntimeInfo.State == CoreState.Running)
            {
                _core = core;
                if (EnableSystemProxy)
                {
                    _systemProxy.EnableGlobalProxy($"127.0.0.1:{mixedPort}");
                }

                StatusText = "运行中";
                
                // 启动节点健康监控
                if (_healthMonitor != null && _nodes.Count > 1)
                {
                    _healthMonitor.IsEnabled = true;
                }
                
                await RunSelfTestAsync(mixedPort);
                return;
            }

            if (!EnableTun || !tunInitIssue || attempt == maxAttempts - 1)
            {
                CoreState = CoreState.Faulted;
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
        return "wowproxy-tun-" + Guid.NewGuid().ToString("N").Substring(0, 6);
    }

    private void TryAppendTunSummary(string configPath)
    {
        if (!EnableTun) return;
        try
        {
            using var stream = File.OpenRead(configPath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("inbounds", out var inbounds) || inbounds.ValueKind != JsonValueKind.Array)
                return;

            foreach (var inbound in inbounds.EnumerateArray())
            {
                if (!inbound.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                    continue;
                if (!string.Equals(typeEl.GetString(), "tun", StringComparison.OrdinalIgnoreCase))
                    continue;

                var iface = inbound.TryGetProperty("interface_name", out var ifaceEl) && ifaceEl.ValueKind == JsonValueKind.String
                    ? ifaceEl.GetString() : null;
                var addr = inbound.TryGetProperty("address", out var addrEl) && addrEl.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", addrEl.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)))
                    : string.Empty;

                AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Info, $"TUN 配置：interface_name={iface ?? "(auto)"} address=[{addr}] auto_route=true"));
                return;
            }
        }
        catch { }
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
                sb.Append("  sni=").Append(node.TlsServerName);
            if (!string.IsNullOrWhiteSpace(node.UtlsFingerprint))
                sb.Append("  fp=").Append(node.UtlsFingerprint);
            if (!string.IsNullOrWhiteSpace(node.TlsAlpn))
                sb.Append("  alpn=").Append(node.TlsAlpn);
            else if (isWs)
                sb.Append("  alpn=http/1.1(auto)");
            if (node.TlsInsecure)
                sb.Append("  insecure=true");
        }

        if (!string.IsNullOrWhiteSpace(node.TransportType))
            sb.Append("  transport=").Append(node.TransportType);
        if (!string.IsNullOrWhiteSpace(node.TransportHost))
            sb.Append("  host=").Append(node.TransportHost);
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

        // Determine group name for this subscription URL
        var existingEntry = _subscriptionGroups.FirstOrDefault(e => string.Equals(e.Url, url, StringComparison.OrdinalIgnoreCase));
        string groupName;
        if (existingEntry is not null)
        {
            groupName = existingEntry.GroupName;
        }
        else
        {
            // Auto-generate group name: "订阅1", "订阅2", ...
            var idx = _subscriptionGroups.Count + 1;
            groupName = $"订阅{idx}";
        }

        var (nodes, errors) = await NodeImport.LoadFromSubscriptionAsync(url, CancellationToken.None, groupName);

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var activeId = ActiveNode?.Id;

            // Register this subscription group
            if (existingEntry is null)
            {
                _subscriptionGroups.Add(new SubscriptionEntry(groupName, url));
            }

            // Keep nodes from OTHER subscription groups and manual nodes (no group)
            var subscriptionNodeIds = new HashSet<string>(nodes.Select(n => n.Id), StringComparer.OrdinalIgnoreCase);
            var otherNodes = _nodes
                .Where(m => !string.Equals(m.Node.SubscriptionGroup, groupName, StringComparison.OrdinalIgnoreCase)
                            && !subscriptionNodeIds.Contains(m.Id))
                .Select(m => m.Node)
                .ToList();

            var merged = nodes.Concat(otherNodes)
                .GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _nodes.Clear();
            foreach (var n in merged)
                _nodes.Add(new ProxyNodeModel(n));

            RebuildNodeGroups();
            SelectedGroup = groupName;

            // Restore active node
            if (!string.IsNullOrWhiteSpace(activeId))
                ActiveNode = _nodes.FirstOrDefault(n => string.Equals(n.Id, activeId, StringComparison.OrdinalIgnoreCase));
            if (ActiveNode == null && !string.IsNullOrWhiteSpace(_settings.SelectedNodeId))
                ActiveNode = _nodes.FirstOrDefault(n => string.Equals(n.Id, _settings.SelectedNodeId, StringComparison.OrdinalIgnoreCase));

            if (ActiveNode != null && !FilteredNodes.Contains(ActiveNode))
            {
                // If the active node is not in the currently selected group, don't force select it
                SelectedNode = FilteredNodes.FirstOrDefault();
            }
            else
            {
                SelectedNode = ActiveNode ?? FilteredNodes.FirstOrDefault();
            }

            nodes = merged;
        });

        _settings = _settings with
        {
            SubscriptionUrl = url,
            Nodes = nodes,
            SelectedNodeId = ActiveNode?.Id,
            SubscriptionGroups = _subscriptionGroups.ToList(),
        };

        await _settingsStore.SaveAsync(_settings);

        AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Info, $"订阅更新完成：{nodes.Count} 个节点（分组：{groupName}）"));
        foreach (var e in errors.Take(10))
            AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Warning, e));

        StatusText = nodes.Count == 0 ? "订阅为空或解析失败" : "订阅已更新";
    }

    private static string SanitizeUrlForLog(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
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

        var (nodes, errors, isClash) = NodeImport.ParseText(text);

        // Assign nodes to current selected group or create a new group for Clash imports
        string? importGroupName = null;
        if (nodes.Count > 0)
        {
            if (isClash)
            {
                // For Clash YAML imports, create a new group
                var idx = _subscriptionGroups.Count + 1;
                importGroupName = $"导入{idx}";
                nodes = nodes.Select(n => n with { SubscriptionGroup = importGroupName }).ToList();
            }
            else if (_selectedGroup != "全部" && !string.IsNullOrWhiteSpace(_selectedGroup))
            {
                // For individual node links, import to current selected group
                nodes = nodes.Select(n => n with { SubscriptionGroup = _selectedGroup }).ToList();
            }
        }

        var merged = _nodes.Select(m => m.Node).ToList();
        foreach (var node in nodes)
        {
            var existingIndex = merged.FindIndex(x => string.Equals(x.Id, node.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                // If the incoming node has a group but the existing one doesn't, update it
                if (!string.IsNullOrWhiteSpace(node.SubscriptionGroup) && string.IsNullOrWhiteSpace(merged[existingIndex].SubscriptionGroup))
                    merged[existingIndex] = node;
                continue;
            }
            merged.Add(node);
        }

        merged = merged.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            // Register new group if clash import
            if (importGroupName is not null)
            {
                _subscriptionGroups.Add(new SubscriptionEntry(importGroupName, string.Empty));
            }

            _nodes.Clear();
            foreach (var n in merged)
                _nodes.Add(new ProxyNodeModel(n));

            RebuildNodeGroups();

            // Switch to the new group tab if created
            if (importGroupName is not null)
            {
                SelectedGroup = importGroupName;
            }
            else
            {
                RebuildFilteredNodes();
            }

            SelectedNode ??= _filteredNodes.FirstOrDefault();
            NodeImportText = string.Empty;
        });

        _settings = _settings with
        {
            Nodes = merged,
            SelectedNodeId = ActiveNode?.Id,
            SubscriptionGroups = _subscriptionGroups.ToList(),
        };

        await _settingsStore.SaveAsync(_settings);

        AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Info, $"已导入：{nodes.Count}，节点总数：{merged.Count}" + (importGroupName is not null ? $"（分组：{importGroupName}）" : string.Empty)));
        foreach (var e in errors.Take(10))
            AppendLog(new CoreLogLine(DateTimeOffset.Now, CoreLogLevel.Warning, e));

        StatusText = "已导入节点";
    }

    private async Task SetActiveNodeAsync()
    {
        if (SelectedNode is null)
        {
            StatusText = "请先选择一个节点";
            return;
        }

        var wasRunning = _core?.RuntimeInfo.State == CoreState.Running;
        ActiveNode = SelectedNode;
        StatusText = $"活动节点已切换：{ActiveNode.Name}";

        if (wasRunning)
        {
            StatusText = $"正在切换节点：{ActiveNode.Name}，重启内核中...";
            await StopAsync();
            await StartAsync();
        }
    }
    private void CopyNodeLink()
    {
        if (SelectedNode is null)
        {
            StatusText = "请先选择要复制的节点";
            return;
        }

        var node = SelectedNode.Node;
        string shareLink;

        if (node.Type == ProxyNodeType.Vmess)
        {
            // 构造标准 vmess:// 分享链接 (v2rayN 格式)
            var vmessObj = new Dictionary<string, object?>
            {
                ["v"] = "2",
                ["ps"] = node.Name,
                ["add"] = node.Server,
                ["port"] = node.Port.ToString(),
                ["id"] = node.Uuid ?? "",
                ["aid"] = (node.AlterId ?? 0).ToString(),
                ["scy"] = node.Security ?? "auto",
                ["net"] = node.TransportType ?? "tcp",
                ["type"] = "none",
                ["host"] = node.TransportHost ?? "",
                ["path"] = node.TransportPath ?? "",
                ["tls"] = node.TlsEnabled ? "tls" : "",
                ["sni"] = node.TlsServerName ?? "",
                ["alpn"] = node.TlsAlpn ?? "",
                ["fp"] = node.UtlsFingerprint ?? "",
            };
            var json = JsonSerializer.Serialize(vmessObj, new JsonSerializerOptions { WriteIndented = true });
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            shareLink = $"vmess://{base64}";
        }
        else if (!string.IsNullOrWhiteSpace(node.Raw) && 
                 (node.Raw.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ||
                  node.Raw.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase) ||
                  node.Raw.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)))
        {
            // vless/trojan/ss 直接使用原始链接
            shareLink = node.Raw;
        }
        else
        {
            StatusText = "该节点类型暂不支持导出分享链接";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(shareLink);
            StatusText = $"已复制 {node.Name} 的分享链接到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText = $"复制失败: {ex.Message}";
        }
    }

    private void RemoveNode()
    {
        if (SelectedNode is null)
        {
            StatusText = "请先选择要移除的节点";
            return;
        }

        var nodeToRemove = SelectedNode;
        _nodes.Remove(nodeToRemove);
        _filteredNodes.Remove(nodeToRemove);

        if (ReferenceEquals(ActiveNode, nodeToRemove)) ActiveNode = null;
        if (ReferenceEquals(SelectedNode, nodeToRemove)) SelectedNode = null;

        RebuildNodeGroups();
        _ = PersistSelectionAsync();
        StatusText = "节点已移除";
    }

    private void RemoveSelectedNodes()
    {
        var toRemove = _selectedNodes.ToList();
        if (toRemove.Count == 0)
        {
            // Fall back to single selected node
            if (SelectedNode is not null) toRemove.Add(SelectedNode);
        }
        if (toRemove.Count == 0)
        {
            StatusText = "请先选择要移除的节点";
            return;
        }

        var removedActive = false;
        foreach (var node in toRemove)
        {
            _nodes.Remove(node);
            _filteredNodes.Remove(node);
            if (ReferenceEquals(ActiveNode, node)) removedActive = true;
        }
        _selectedNodes.Clear();

        if (removedActive) ActiveNode = null;
        SelectedNode = _filteredNodes.FirstOrDefault();

        RebuildNodeGroups();
        _ = PersistSelectionAsync();
        StatusText = $"已移除 {toRemove.Count} 个节点";
    }

    private void ClearNodes()
    {
        _nodes.Clear();
        _filteredNodes.Clear();
        _subscriptionGroups.Clear();

        // Reset groups to just "全部"
        while (_nodeGroups.Count > 1)
            _nodeGroups.RemoveAt(_nodeGroups.Count - 1);

        _selectedGroup = "全部";
        OnPropertyChanged(nameof(SelectedGroup));

        SelectedNode = null;
        _settings = _settings with
        {
            Nodes = new List<ProxyNode>(),
            SelectedNodeId = null,
            SubscriptionGroups = null,
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
                SelectedNodeId = ActiveNode?.Id,
                SubscriptionUrl = SubscriptionUrl,
                LogLevel = LogLevel,
                EnableDirectCn = EnableDirectCn,
                EnableTun = EnableTun,
                EnableSystemProxy = EnableSystemProxy,
                BypassTunProcesses = null,
                SubscriptionGroups = _subscriptionGroups.ToList(),
                EnableChainProxy = _chainProxy.EnableChainProxy,
                ChainProxyNodeIds = _chainProxy.GetChainNodeIds(),
                UserRules = _userRules.GetRules(),
            };
            await _settingsStore.SaveAsync(_settings);
        }
        catch { }
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
        await _coreLock.WaitAsync();
        try
        {
            // 停止节点健康监控
            if (_healthMonitor != null)
            {
                _healthMonitor.IsEnabled = false;
            }
            
            // Always restore system proxy regardless of current EnableSystemProxy state,
            // because the user may have toggled it off after connecting.
            _systemProxy.RestoreFromSnapshotIfAny();
            if (_core is not null)
            {
                await _core.StopAsync();
                await _core.DisposeAsync();
                _core = null;
            }
            CoreState = CoreState.Stopped;
            StatusText = "已停止";
        }
        finally
        {
            _coreLock.Release();
        }
    }

    private void UpdateStatus(CoreRuntimeInfo info)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            CoreState = info.State;
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
                _logs.Remove(0, _logs.Length - MaxChars);
        }

        if (Interlocked.Exchange(ref _logsUpdateScheduled, 1) == 0)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
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

        if (!int.TryParse(_settingsViewModel.MixedPortText, out mixedPort) || mixedPort is < 1 or > 65535)
        {
            error = "Mixed 端口无效";
            return false;
        }

        if (!int.TryParse(_settingsViewModel.ClashApiPortText, out clashApiPort) || clashApiPort is < 1 or > 65535)
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
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
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
    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
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
