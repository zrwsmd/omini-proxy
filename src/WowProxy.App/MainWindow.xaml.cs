using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WowProxy.App.Models;

namespace WowProxy.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void NodeDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        foreach (ProxyNodeModel item in e.RemovedItems)
            vm.SelectedNodes.Remove(item);

        foreach (ProxyNodeModel item in e.AddedItems)
            if (!vm.SelectedNodes.Contains(item))
                vm.SelectedNodes.Add(item);
    }
}
