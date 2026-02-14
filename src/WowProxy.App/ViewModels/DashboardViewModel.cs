using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using WowProxy.App.Models;
using WowProxy.Infrastructure;

namespace WowProxy.App.ViewModels;

public class DashboardViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly MainViewModel _mainViewModel;
    private ClashApiClient? _apiClient;
    private readonly ObservableCollection<ConnectionModel> _connections = new();
    private string _filterText = string.Empty;
    private readonly object _lock = new();

    public DashboardViewModel(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        // 启用集合同步以支持多线程访问 (BindingOperations.EnableCollectionSynchronization)
        // 但这里我们直接在 UI 线程操作 ObservableCollection
        System.Windows.Data.BindingOperations.EnableCollectionSynchronization(_connections, _lock);

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        CloseConnectionCommand = new RelayCommand(async obj => await CloseConnectionAsync(obj));
    }

    public ObservableCollection<ConnectionModel> Connections => _connections;

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (_filterText != value)
            {
                _filterText = value;
                OnPropertyChanged();
                // 触发过滤逻辑（如果使用 CollectionViewSource）
                // 这里我们简化处理：UpdateConnections 时会应用过滤
            }
        }
    }

    public RelayCommand CloseConnectionCommand { get; }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        // Only run if proxy is running
        if (!_mainViewModel.StatusText.Contains("运行中"))
        {
            if (_connections.Count > 0) _connections.Clear();
            _apiClient = null; // Reset client when stopped
            return;
        }

        if (_apiClient is null)
        {
            // 如果用户未启用 Clash API，则默认使用 9090 (假设 sing-box 配置中开启了)
            // 但我们的 sing-box 配置逻辑是：只有 EnableClashApi=true 时才写入 experimental.clash_api
            // 所以如果用户没勾选 EnableClashApi，这里连不上是正常的。
            // 为了保证功能可用，我们需要确保 MainViewModel 在启动时总是开启 Clash API，或者提示用户。
            // 这里我们先尝试连接。
            var port = _mainViewModel.EnableClashApi ? _mainViewModel.ClashApiPort : 9090;
            var secret = _mainViewModel.ClashApiSecret;
            _apiClient = new ClashApiClient(port, secret);
        }

        try
        {
            var response = await _apiClient.GetConnectionsAsync();
            if (response is null) return;

            UpdateConnections(response.Connections);
        }
        catch
        {
            // Ignore errors
        }
    }

    private void UpdateConnections(List<Core.Abstractions.Models.Clash.Connection> allConnections)
    {
        // 1. 应用过滤
        var filtered = allConnections;
        if (!string.IsNullOrWhiteSpace(_filterText))
        {
            var key = _filterText.Trim();
            filtered = allConnections.Where(c =>
                c.Metadata.Host.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                c.Metadata.DestinationIP.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                c.Metadata.Process.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                (c.Metadata.ProcessPath != null && c.Metadata.ProcessPath.Contains(key, StringComparison.OrdinalIgnoreCase)) ||
                c.Metadata.DestinationPort.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                c.Metadata.Network.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                c.Metadata.Type.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                c.Chains.Any(chain => chain.Contains(key, StringComparison.OrdinalIgnoreCase)) ||
                c.Rule.Contains(key, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        var existingIds = _connections.Select(c => c.Id).ToHashSet();
        var newIds = filtered.Select(c => c.Id).ToHashSet();

        // 2. 移除不再列表中的连接
        lock (_lock)
        {
            for (var i = _connections.Count - 1; i >= 0; i--)
            {
                if (!newIds.Contains(_connections[i].Id))
                {
                    _connections.RemoveAt(i);
                }
            }

            // 3. 添加或更新连接
            foreach (var conn in filtered)
            {
                var existing = _connections.FirstOrDefault(c => c.Id == conn.Id);
                if (existing is not null)
                {
                    existing.Update(conn);
                }
                else
                {
                    _connections.Add(new ConnectionModel(conn));
                }
            }
        }
    }

    private async Task CloseConnectionAsync(object? obj)
    {
        if (obj is ConnectionModel model && _apiClient is not null)
        {
            if (await _apiClient.CloseConnectionAsync(model.Id))
            {
                _connections.Remove(model);
            }
        }
    }

    public void Dispose()
    {
        _timer.Stop();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
