using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WowProxy.App.Models;
using WowProxy.Core.Abstractions;

namespace WowProxy.App;

public partial class MainWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public MainWindow()
    {
        InitializeComponent();
        SetWindowIcon();
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

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // Ctrl+V: Paste and import nodes
        if (e.Key == System.Windows.Input.Key.V && 
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
        {
            // Check if focus is not in a TextBox (to avoid interfering with normal paste)
            if (System.Windows.Input.Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
            {
                try
                {
                    if (System.Windows.Clipboard.ContainsText())
                    {
                        var clipboardText = System.Windows.Clipboard.GetText();
                        if (!string.IsNullOrWhiteSpace(clipboardText))
                        {
                            vm.NodeImportText = clipboardText;
                            if (vm.ImportLinksCommand.CanExecute(null))
                            {
                                vm.ImportLinksCommand.Execute(null);
                            }
                            e.Handled = true;
                        }
                    }
                }
                catch
                {
                    // Ignore clipboard errors
                }
            }
        }
    }

    private void SetWindowIcon()
    {
        try
        {
            var icon = CreateGraphicIcon(CoreState.Stopped);
            var hIcon = icon.Handle;
            var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            Icon = imageSource;
            DestroyIcon(hIcon);
            icon.Dispose();
        }
        catch
        {
            // Ignore icon errors
        }
    }

    private Icon CreateGraphicIcon(CoreState state)
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
            using var centerPen = new Pen(Color.White, 2.5f)
            {
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
        return System.Drawing.Icon.FromHandle(hIcon);
    }
}
