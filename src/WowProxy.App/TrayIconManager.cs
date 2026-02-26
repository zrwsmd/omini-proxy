using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using WowProxy.Core.Abstractions;

namespace WowProxy.App;

internal sealed class TrayIconManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly NotifyIcon _notifyIcon;
    private readonly MainViewModel _viewModel;
    private readonly Window _mainWindow;
    private readonly ToolStripMenuItem _connectMenuItem;
    private bool _disposed;
    private Icon? _currentIcon;

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

        _currentIcon = CreateGraphicIcon(_viewModel.CoreState);
        _notifyIcon = new NotifyIcon
        {
            Icon = _currentIcon,
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
        else if (e.PropertyName == nameof(MainViewModel.CoreState))
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(UpdateIcon);
        }
    }

    private void UpdateIcon()
    {
        if (_disposed) return;
        var oldIcon = _currentIcon;
        _currentIcon = CreateGraphicIcon(_viewModel.CoreState);
        _notifyIcon.Icon = _currentIcon;

        if (oldIcon != null)
        {
            DestroyIcon(oldIcon.Handle);
            oldIcon.Dispose();
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
    /// 使用 GDI+ 绘制一个简单的圆点图标作为托盘图标，颜色根据内核状态变化。
    /// </summary>
    private static Icon CreateGraphicIcon(CoreState state)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);

            // 根据状态选择颜色
            Color mainColor = state switch
            {
                CoreState.Running => Color.FromArgb(0x32, 0xCD, 0x32), // LimeGreen
                CoreState.Starting => Color.FromArgb(0xFF, 0x8C, 0x00), // DarkOrange
                CoreState.Faulted => Color.FromArgb(0xDC, 0x14, 0x3C), // Crimson
                CoreState.Stopping => Color.FromArgb(0x80, 0x80, 0x80), // Gray
                _ => Color.FromArgb(0x1E, 0x90, 0xFF) // DodgerBlue (Stopped)
            };

            using var brush = new SolidBrush(mainColor);
            g.FillEllipse(brush, 1, 1, 14, 14);

            // 绘制一个中心白色小圆点
            g.FillEllipse(Brushes.White, 5, 5, 6, 6);
        }

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        if (_currentIcon != null)
        {
            DestroyIcon(_currentIcon.Handle);
            _currentIcon.Dispose();
        }
    }
}
