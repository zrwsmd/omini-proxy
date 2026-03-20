using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using WowProxy.Core.Abstractions;

namespace WowProxy.App.Views;

public partial class ConfirmWindow : Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public bool IsConfirmed { get; private set; }

    public ConfirmWindow(string title, string message, string confirmButtonText = "确定", bool isDangerous = true)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmButtonText;
        
        if (isDangerous)
        {
            ConfirmButton.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));
        }
        else
        {
            ConfirmButton.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3B82F6"));
        }

        // Set window icon to match tray icon
        SetWindowIcon();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        DialogResult = false;
    }

    private void TopBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
        {
            this.DragMove();
        }
    }

    private void SetWindowIcon()
    {
        try
        {
            var icon = CreateGraphicIcon();
            var hIcon = icon.Handle;
            var imageSource = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                System.Windows.Int32Rect.Empty,
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

    private Icon CreateGraphicIcon()
    {
        int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            // 使用蓝色主题色
            Color stateColor = Color.FromArgb(255, 59, 130, 246); // Blue 500

            // 1. 绘制外部圆形底板
            using var bgBrush = new SolidBrush(Color.FromArgb(255, 30, 41, 59)); // Slate 800
            g.FillEllipse(bgBrush, 0, 0, 32, 32);

            // 2. 绘制光环边框
            using var ringPen = new Pen(stateColor, 2f);
            g.DrawEllipse(ringPen, 1.5f, 1.5f, 29, 29);

            // 3. 绘制中心 'W' 标志
            using var centerPen = new Pen(Color.White, 2.5f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round
            };

            PointF[] wPoints = {
                new PointF(7, 12),
                new PointF(12, 22),
                new PointF(16, 15),
                new PointF(20, 22),
                new PointF(25, 12)
            };

            g.DrawLines(centerPen, wPoints);
        }

        var hIcon = bmp.GetHicon();
        return System.Drawing.Icon.FromHandle(hIcon);
    }
}
