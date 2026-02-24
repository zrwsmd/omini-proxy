using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace WowProxy.App;

internal sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly MainViewModel _viewModel;
    private readonly Window _mainWindow;
    private readonly ToolStripMenuItem _connectMenuItem;
    private bool _disposed;

    public TrayIconManager(MainViewModel viewModel, Window mainWindow)
    {
        _viewModel = viewModel;
        _mainWindow = mainWindow;

        _connectMenuItem = new ToolStripMenuItem(_viewModel.ConnectButtonText, null, OnConnectClicked);

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("显示程序", null, OnShowClicked);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_connectMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("退出 WowProxy", null, OnExitClicked);

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateGraphicIcon(),
            Text = "WowProxy",
            Visible = true,
            ContextMenuStrip = contextMenu,
        };

        _notifyIcon.DoubleClick += OnShowClicked;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        UpdateTooltip();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ConnectButtonText) or nameof(MainViewModel.StatusText))
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(UpdateTooltip);
        }
    }

    private void UpdateTooltip()
    {
        if (_disposed) return;
        _connectMenuItem.Text = _viewModel.ConnectButtonText;
        var tip = $"WowProxy — {_viewModel.StatusText}";
        // NotifyIcon.Text max 64 chars
        _notifyIcon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private void OnShowClicked(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        });
    }

    private void OnConnectClicked(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_viewModel.ConnectCommand.CanExecute(null))
                _viewModel.ConnectCommand.Execute(null);
        });
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            System.Windows.Application.Current.Shutdown();
        });
    }

    /// <summary>
    /// 使用 GDI+ 绘制一个简单的蓝色圆点图标作为托盘图标，避免外部资源加载问题。
    /// </summary>
    private static Icon CreateGraphicIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            // 绘制一个深蓝色圆点
            using var brush = new SolidBrush(Color.FromArgb(0x1E, 0x90, 0xFF)); // DodgerBlue
            g.FillEllipse(brush, 1, 1, 14, 14);
            // 绘制一个中心白色小圆点
            g.FillEllipse(Brushes.White, 5, 5, 6, 6);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
