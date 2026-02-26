using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WowProxy.App.Models;

namespace WowProxy.App.Controls;

public class TrafficRadarControl : Canvas
{
    private DispatcherTimer _animationTimer;
    private Random _random = new Random();

    public static readonly DependencyProperty ConnectionsProperty = 
        DependencyProperty.Register(
            nameof(Connections), 
            typeof(ObservableCollection<ConnectionModel>), 
            typeof(TrafficRadarControl), 
            new PropertyMetadata(null, OnConnectionsChanged));

    public ObservableCollection<ConnectionModel> Connections
    {
        get { return (ObservableCollection<ConnectionModel>)GetValue(ConnectionsProperty); }
        set { SetValue(ConnectionsProperty, value); }
    }

    public TrafficRadarControl()
    {
        this.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E1E")); 
        this.ClipToBounds = true;

        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _animationTimer.Tick += AnimationTimer_Tick;
        _animationTimer.Start();
        
        this.SizeChanged += (s, e) => DrawRadarBackground();
    }

    private void DrawRadarBackground()
    {
        this.Children.Clear();
        
        if (ActualWidth == 0 || ActualHeight == 0) return;

        var center = new System.Windows.Point(ActualWidth / 2, ActualHeight / 2);
        
        // Draw circles
        for (int i = 1; i <= 4; i++)
        {
            var radius = Math.Min(ActualWidth, ActualHeight) / 2 * (i / 4.0);
            var circle = new System.Windows.Shapes.Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 0, 255, 100)),
                StrokeThickness = 1
            };
            SetLeft(circle, center.X - radius);
            SetTop(circle, center.Y - radius);
            this.Children.Add(circle);
        }
        
        // Draw cross lines
        var vLine = new System.Windows.Shapes.Line { X1 = center.X, Y1 = 0, X2 = center.X, Y2 = ActualHeight, Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 0, 255, 100)), StrokeThickness = 1 };
        var hLine = new System.Windows.Shapes.Line { X1 = 0, Y1 = center.Y, X2 = ActualWidth, Y2 = center.Y, Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 0, 255, 100)), StrokeThickness = 1 };
        this.Children.Add(vLine);
        this.Children.Add(hLine);
    }

    private static void OnConnectionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TrafficRadarControl)d;
        if (e.OldValue is ObservableCollection<ConnectionModel> oldColl)
        {
            oldColl.CollectionChanged -= control.Connections_CollectionChanged;
        }
        if (e.NewValue is ObservableCollection<ConnectionModel> newColl)
        {
            newColl.CollectionChanged += control.Connections_CollectionChanged;
        }
    }

    private void Connections_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // For performance, we don't recreate all visuals on every change, 
        // the tick timer will draw the "packets" and clean up dead ones
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (ActualWidth == 0 || ActualHeight == 0 || Connections == null) return;
        
        // Redraw base to clear old packets
        DrawRadarBackground();
        
        var center = new System.Windows.Point(ActualWidth / 2, ActualHeight / 2);
        var centerCircle = new System.Windows.Shapes.Ellipse
        {
            Width = 40, Height = 40,
            Fill = new SolidColorBrush(Colors.Green),
            ToolTip = "Local Node"
        };
        SetLeft(centerCircle, center.X - 20);
        SetTop(centerCircle, center.Y - 20);
        this.Children.Add(centerCircle);
        
        // Take top 30 active connections to avoid UI lag
        var activeConnections = Connections
            .Where(c => c.DownloadSpeed > 0 || c.UploadSpeed > 0)
            .OrderByDescending(c => c.DownloadSpeed + c.UploadSpeed)
            .Take(30)
            .ToList();

        int index = 0;
        foreach (var conn in activeConnections)
        {
            // Assign a pseudo-random fixed angle for each connection ID so it doesn't jump
            var hash = Math.Abs(conn.Id.GetHashCode());
            double angle = (hash % 360) * Math.PI / 180.0;
            
            // Map speed to size
            double maxSpeed = 5 * 1024 * 1024.0; // 5MB/s threshold for max size
            var speedRate = Math.Min(1.0, conn.DownloadSpeed / maxSpeed);
            var size = 10 + (speedRate * 30); // 10px to 40px
            
            // Distance from center (could be related to latency, but we don't have per-connection latency, so we use hash)
            double maxRadius = Math.Min(ActualWidth, ActualHeight) / 2 - size;
            double distance = 50 + (hash % (int)(maxRadius - 50));
            
            var posX = center.X + Math.Cos(angle) * distance;
            var posY = center.Y + Math.Sin(angle) * distance;
            
            // Draw connection line
            var line = new System.Windows.Shapes.Line
            {
                X1 = center.X, Y1 = center.Y,
                X2 = posX, Y2 = posY,
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(50 + 100 * speedRate), 0, 255, 100)),
                StrokeThickness = 1 + speedRate * 3
            };
            this.Children.Add(line);
            
            // Draw Target node
            var targetNode = new System.Windows.Shapes.Ellipse
            {
                Width = size, Height = size,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 0, 255, 150)),
                ToolTip = $"{conn.Process}\n{conn.Host}\n↓ {conn.DownloadSpeedText}  ↑ {conn.UploadSpeedText}"
            };
            SetLeft(targetNode, posX - size / 2);
            SetTop(targetNode, posY - size / 2);
            this.Children.Add(targetNode);
            
            // Draw label
            var label = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(conn.Process) ? conn.Host.Substring(0, Math.Min(10, conn.Host.Length)) : conn.Process,
                Foreground = new SolidColorBrush(Colors.LightGreen),
                FontSize = 10,
                IsHitTestVisible = false
            };
            SetLeft(label, posX + size / 2 + 5);
            SetTop(label, posY - 8);
            this.Children.Add(label);
            
            index++;
        }
    }
}
