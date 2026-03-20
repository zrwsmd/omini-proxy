using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using WowProxy.App.Models;

namespace WowProxy.App.Services;

/// <summary>
/// 节点健康检查和自动故障转移服务
/// </summary>
public class NodeHealthMonitor : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly Func<ProxyNodeModel?> _getCurrentNode;
    private readonly Func<List<ProxyNodeModel>> _getAvailableNodes;
    private readonly Func<ProxyNodeModel, Task> _switchToNode;
    private readonly Action<string> _logMessage;
    private bool _isEnabled;
    private int _consecutiveFailures;
    private const int FailureThreshold = 3; // 连续失败3次才触发故障转移
    private const int CheckIntervalSeconds = 30; // 每30秒检查一次

    public NodeHealthMonitor(
        Func<ProxyNodeModel?> getCurrentNode,
        Func<List<ProxyNodeModel>> getAvailableNodes,
        Func<ProxyNodeModel, Task> switchToNode,
        Action<string> logMessage)
    {
        _getCurrentNode = getCurrentNode;
        _getAvailableNodes = getAvailableNodes;
        _switchToNode = switchToNode;
        _logMessage = logMessage;
        _consecutiveFailures = 0;

        _timer = new System.Threading.Timer(
            OnTimerTick,
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                if (_isEnabled)
                {
                    Start();
                }
                else
                {
                    Stop();
                }
            }
        }
    }

    private void Start()
    {
        _consecutiveFailures = 0;
        _timer.Change(TimeSpan.FromSeconds(CheckIntervalSeconds), TimeSpan.FromSeconds(CheckIntervalSeconds));
        _logMessage("节点健康监控已启动");
    }

    private void Stop()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _consecutiveFailures = 0;
        _logMessage("节点健康监控已停止");
    }

    private async void OnTimerTick(object? state)
    {
        if (!_isEnabled) return;

        try
        {
            var currentNode = _getCurrentNode();
            if (currentNode == null) return;

            // 检查当前节点健康状态
            var isHealthy = await CheckNodeHealthAsync(currentNode);

            if (isHealthy)
            {
                _consecutiveFailures = 0;
            }
            else
            {
                _consecutiveFailures++;
                _logMessage($"节点 {currentNode.Name} 健康检查失败 ({_consecutiveFailures}/{FailureThreshold})");

                if (_consecutiveFailures >= FailureThreshold)
                {
                    _logMessage($"节点 {currentNode.Name} 连续失败 {_consecutiveFailures} 次，触发自动故障转移");
                    await PerformFailoverAsync(currentNode);
                    _consecutiveFailures = 0;
                }
            }
        }
        catch (Exception ex)
        {
            _logMessage($"健康检查异常: {ex.Message}");
        }
    }

    private async Task<bool> CheckNodeHealthAsync(ProxyNodeModel node)
    {
        try
        {
            // 使用 Ping 检查节点服务器是否可达
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(node.Server, 5000);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private async Task PerformFailoverAsync(ProxyNodeModel failedNode)
    {
        var availableNodes = _getAvailableNodes()
            .Where(n => n.Id != failedNode.Id && !string.IsNullOrWhiteSpace(n.Server))
            .ToList();

        if (availableNodes.Count == 0)
        {
            _logMessage("没有可用的备用节点");
            return;
        }

        // 选择延迟最低的节点
        var bestNode = availableNodes
            .Where(n => n.Latency > 0)
            .OrderBy(n => n.Latency)
            .FirstOrDefault() ?? availableNodes.First();

        _logMessage($"自动切换到备用节点: {bestNode.Name}");

        try
        {
            await _switchToNode(bestNode);
            _logMessage($"已成功切换到节点: {bestNode.Name}");
        }
        catch (Exception ex)
        {
            _logMessage($"切换节点失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
