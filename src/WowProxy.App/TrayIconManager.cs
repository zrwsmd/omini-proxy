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

        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OnShowClicked(s, EventArgs.Empty);
        };
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
    /// 使用 GDI+ 绘制一个精美的现代风格托盘图标，颜色和图形根据内核状态变化。
    /// </summary>
    private static Icon CreateGraphicIcon(CoreState state)
    {
        int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // 现代扁平色系 (参考 Tailwind CSS)
            Color stateColor = state switch
            {
                CoreState.Running => Color.FromArgb(255, 34, 197, 94),   // 翠绿 (Green 500)
                CoreState.Starting => Color.FromArgb(255, 245, 158, 11), // 活力橙 (Amber 500)
                CoreState.Faulted => Color.FromArgb(255, 239, 68, 68),   // 警示红 (Red 500)
                CoreState.Stopping => Color.FromArgb(255, 156, 163, 175),// 高级灰 (Gray 400)
                _ => Color.FromArgb(255, 59, 130, 246)                   // 科技蓝 (Blue 500)
            };

            // 1. 绘制外部圆形底板 (深色高级质感)
            using var bgBrush = new SolidBrush(Color.FromArgb(255, 30, 41, 59)); // Slate 800
            g.FillEllipse(bgBrush, 0, 0, 32, 32);

            // 2. 根据状态绘制光环边框
            using var ringPen = new Pen(stateColor, 2f);
            g.DrawEllipse(ringPen, 1.5f, 1.5f, 29, 29);

            // 3. 绘制中心标志
            using var centerPen = new Pen(Color.White, 2.5f) { 
                StartCap = System.Drawing.Drawing2D.LineCap.Round, 
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round
            };

            if (state == CoreState.Faulted)
            {
                // 错误状态画一个 X
                g.DrawLine(centerPen, 10, 10, 22, 22);
                g.DrawLine(centerPen, 22, 10, 10, 22);
            }
            else
            {
                // 其他状态画 WowProxy 的 'W' 标志
                PointF[] wPoints = {
                    new PointF(7, 12),
                    new PointF(12, 22),
                    new PointF(16, 15),
                    new PointF(20, 22),
                    new PointF(25, 12)
                };
                
                if (state == CoreState.Stopped || state == CoreState.Stopping) 
                {
                    // 停止时 W 颜色变暗
                    centerPen.Color = Color.FromArgb(150, 255, 255, 255);
                }
                
                g.DrawLines(centerPen, wPoints);
            }
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
