using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace WowProxy.App.Views;

public partial class SelectGroupWindow : Window
{
    private readonly string _noGroupMarker = "(无分组 - 取消所在分组)";
    public string SelectedGroup { get; private set; } = string.Empty;

    public SelectGroupWindow(IEnumerable<string> groups, string currentGroup = "")
    {
        InitializeComponent();
        
        var displayGroups = groups.ToList();
        if (!displayGroups.Contains(_noGroupMarker))
        {
            displayGroups.Insert(0, _noGroupMarker);
        }
        
        GroupComboBox.ItemsSource = displayGroups;
        
        if (string.IsNullOrEmpty(currentGroup))
        {
            GroupComboBox.SelectedItem = _noGroupMarker;
        }
        else
        {
            GroupComboBox.SelectedItem = displayGroups.Contains(currentGroup) ? currentGroup : displayGroups.FirstOrDefault();
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GroupComboBox.SelectedItem as string;
        if (selected == _noGroupMarker)
        {
            SelectedGroup = "";
        }
        else
        {
            SelectedGroup = selected ?? "";
        }
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            this.DragMove();
        }
    }
}
