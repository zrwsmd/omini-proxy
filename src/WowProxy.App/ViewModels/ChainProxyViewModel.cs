using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WowProxy.App.Models;

namespace WowProxy.App.ViewModels;

/// <summary>
/// ViewModel for the chain proxy configuration UI.
/// Allows users to build an ordered chain of proxy nodes for multi-hop routing.
/// Traffic flow: Local → Hop1 → Hop2 → ... → Exit Node → Target
/// </summary>
public class ChainProxyViewModel : INotifyPropertyChanged
{
    private readonly MainViewModel _mainViewModel;
    private bool _enableChainProxy;
    private ProxyNodeModel? _selectedAvailableNode;
    private ChainNodeItem? _selectedChainNode;

    public ChainProxyViewModel(MainViewModel mainViewModel, bool enableChainProxy, List<string>? chainNodeIds)
    {
        _mainViewModel = mainViewModel;
        _enableChainProxy = enableChainProxy;
        ChainNodes = new ObservableCollection<ChainNodeItem>();

        AddToChainCommand = new RelayCommand(_ => AddToChain());
        RemoveFromChainCommand = new RelayCommand(_ => RemoveFromChain());
        MoveUpCommand = new RelayCommand(_ => MoveUp());
        MoveDownCommand = new RelayCommand(_ => MoveDown());
        ClearChainCommand = new RelayCommand(_ => ClearChain());

        // Restore chain from saved settings
        if (chainNodeIds is { Count: > 0 })
        {
            foreach (var id in chainNodeIds)
            {
                var nodeModel = _mainViewModel.Nodes.FirstOrDefault(
                    n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
                if (nodeModel != null)
                {
                    ChainNodes.Add(new ChainNodeItem(nodeModel, ChainNodes.Count));
                }
            }
            RenumberChain();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayCommand AddToChainCommand { get; }
    public RelayCommand RemoveFromChainCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand ClearChainCommand { get; }

    public bool EnableChainProxy
    {
        get => _enableChainProxy;
        set
        {
            if (_enableChainProxy == value) return;
            _enableChainProxy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChainFlowText));
            _mainViewModel.NotifySettingsChanged();
        }
    }

    /// <summary>All nodes available to add to the chain (from main node list).</summary>
    public ObservableCollection<ProxyNodeModel> AvailableNodes => _mainViewModel.Nodes;

    /// <summary>The ordered chain of nodes.</summary>
    public ObservableCollection<ChainNodeItem> ChainNodes { get; }

    public ProxyNodeModel? SelectedAvailableNode
    {
        get => _selectedAvailableNode;
        set { if (_selectedAvailableNode != value) { _selectedAvailableNode = value; OnPropertyChanged(); } }
    }

    public ChainNodeItem? SelectedChainNode
    {
        get => _selectedChainNode;
        set { if (_selectedChainNode != value) { _selectedChainNode = value; OnPropertyChanged(); } }
    }

    /// <summary>Visual text showing the chain flow, e.g. "本地 → 机房节点 → 住宅IP → 目标网站"</summary>
    public string ChainFlowText
    {
        get
        {
            if (!EnableChainProxy || ChainNodes.Count < 2)
                return "请至少添加 2 个节点组成链式代理";

            var parts = new List<string> { "🖥 本地" };
            foreach (var item in ChainNodes)
                parts.Add(item.DisplayName);
            parts.Add("🌐 目标网站");

            return string.Join("  →  ", parts);
        }
    }

    /// <summary>Get the list of chain node IDs for persistence.</summary>
    public List<string> GetChainNodeIds()
    {
        return ChainNodes.Select(c => c.NodeId).ToList();
    }

    private void AddToChain()
    {
        if (SelectedAvailableNode is null) return;

        // Allow the same node type but not the exact same node instance twice
        if (ChainNodes.Any(c => c.NodeId == SelectedAvailableNode.Id))
            return;

        ChainNodes.Add(new ChainNodeItem(SelectedAvailableNode, ChainNodes.Count));
        RenumberChain();
        NotifyChainChanged();
    }

    private void RemoveFromChain()
    {
        if (SelectedChainNode is null) return;
        ChainNodes.Remove(SelectedChainNode);
        RenumberChain();
        SelectedChainNode = ChainNodes.LastOrDefault();
        NotifyChainChanged();
    }

    private void MoveUp()
    {
        if (SelectedChainNode is null) return;
        var idx = ChainNodes.IndexOf(SelectedChainNode);
        if (idx <= 0) return;
        ChainNodes.Move(idx, idx - 1);
        RenumberChain();
        NotifyChainChanged();
    }

    private void MoveDown()
    {
        if (SelectedChainNode is null) return;
        var idx = ChainNodes.IndexOf(SelectedChainNode);
        if (idx < 0 || idx >= ChainNodes.Count - 1) return;
        ChainNodes.Move(idx, idx + 1);
        RenumberChain();
        NotifyChainChanged();
    }

    private void ClearChain()
    {
        ChainNodes.Clear();
        SelectedChainNode = null;
        NotifyChainChanged();
    }

    private void RenumberChain()
    {
        for (int i = 0; i < ChainNodes.Count; i++)
        {
            ChainNodes[i].HopIndex = i;
            ChainNodes[i].RoleName = i == 0
                ? "入口 (第1跳)"
                : i == ChainNodes.Count - 1
                    ? "出口 (暴露IP)"
                    : $"中转 (第{i + 1}跳)";
        }
    }

    private void NotifyChainChanged()
    {
        OnPropertyChanged(nameof(ChainFlowText));
        _mainViewModel.NotifySettingsChanged();
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Represents a single node in the proxy chain with its order/role info.
/// </summary>
public class ChainNodeItem : INotifyPropertyChanged
{
    private int _hopIndex;
    private string _roleName = "";

    public ChainNodeItem(ProxyNodeModel nodeModel, int hopIndex)
    {
        NodeModel = nodeModel;
        _hopIndex = hopIndex;
    }

    public ProxyNodeModel NodeModel { get; }
    public string NodeId => NodeModel.Id;
    public string DisplayName => $"{NodeModel.Name}";
    public string NodeInfo => $"{NodeModel.Type} | {NodeModel.Server}:{NodeModel.Port}";

    public int HopIndex
    {
        get => _hopIndex;
        set { if (_hopIndex != value) { _hopIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(HopLabel)); } }
    }

    public string RoleName
    {
        get => _roleName;
        set { if (_roleName != value) { _roleName = value; OnPropertyChanged(); } }
    }

    public string HopLabel => $"#{_hopIndex + 1}";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
