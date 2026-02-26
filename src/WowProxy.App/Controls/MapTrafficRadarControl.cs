using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using WowProxy.App.Models;

namespace WowProxy.App.Controls;

public class MapTrafficRadarControl : Canvas
{
    private DispatcherTimer _animationTimer;

    public static readonly DependencyProperty ConnectionsProperty = 
        DependencyProperty.Register(
            nameof(Connections), 
            typeof(ObservableCollection<ConnectionModel>), 
            typeof(MapTrafficRadarControl), 
            new PropertyMetadata(null, OnConnectionsChanged));

    public ObservableCollection<ConnectionModel> Connections
    {
        get { return (ObservableCollection<ConnectionModel>)GetValue(ConnectionsProperty); }
        set { SetValue(ConnectionsProperty, value); }
    }

    // World Map simplified path data (Robinson or similar pseudo-cylindrical projection)
    private const string WorldMapData = "M 326.839 123.003 C 326.839 123.003 328.61 123.111 328.847 121.751 C 329.083 120.392 329.406 120.672 328.847 121.751 C 328.288 122.829 328.051 123.003 326.839 123.003 Z M 352.484 278.434 C 352.348 277.935 352.898 277.854 353.491 277.019 C 353.805 276.577 353.682 276.109 353.385 275.955 C 353.1 275.807 352.406 276.248 351.995 275.768 C 351.644 275.358 351.724 274.636 352.668 274.721 C 353.766 274.819 354.195 275.643 355.772 275.836 C 356.12 275.878 356.331 276.471 356.883 276.702 C 357.575 276.992 358.5 276.216 359.882 276.43 C 361.341 276.657 362.302 277.074 361.309 278.076 C 360.551 278.841 359.544 278.878 359.544 278.878 C 359.544 278.878 359.043 278.706 358.985 279.141 C 358.91 279.699 359.13 280.99 358.749 281.332 C 358.267 281.765 357.773 281.259 357.773 281.259 C 357.773 281.259 357.07 281.565 357.199 281.821 C 357.348 282.116 357.942 282.262 357.942 282.262 C 357.942 282.262 357.884 282.686 357.062 282.352 C 356.291 282.039 355.602 282.52 355.337 282.47 C 355 282.406 355.074 282.029 355.074 282.029 C 355.074 282.029 354.341 282.288 353.945 282.091 C 352.337 281.29 353.511 280.528 353.473 279.418 C 353.456 278.9 352.621 278.937 352.484 278.434 Z M 326.068 123.364 C 326.068 123.364 324.966 123.774 325.281 124.939 C 325.596 126.104 324.335 124.551 323.547 124.314 C 322.759 124.077 325.044 122.9 326.068 123.364 Z M 324.57 286.075 L 324.453 286.969 C 323.719 287.054 322.502 286.671 321.834 286.177 C 321.411 285.864 322.185 285.345 322.564 284.978 C 322.915 284.636 323.183 284.721 323.513 284.84 C 324.301 285.127 324.643 285.939 324.57 286.075 Z M 447.852 169.577 L 447.28 169.761 C 445.698 170.274 445.412 168.966 446.067 168.322 C 446.495 167.902 447.653 167.731 448.273 168.125 C 448.868 168.502 448.167 169.475 447.852 169.577 Z M 449.623 171.185 L 448.784 171.721 L 448.514 170.838 L 449.261 170.472 C 449.261 170.472 449.497 170.528 449.539 170.9 C 449.58 171.261 449.623 171.185 449.623 171.185 Z M 443.435 174.12 C 442.227 175.753 440.09 174.453 439.462 173.344 C 438.307 171.306 438.674 168.611 440.758 167.576 C 441.79 167.063 441.604 168.204 442.138 168.497 C 442.756 168.835 444.025 168.209 444.596 168.636 C 445.195 169.083 445.242 169.837 444.825 170.364 C 444.5 170.771 443.512 170.47 443.834 171.691 C 444 172.31 444.606 172.535 444.407 173.181 C 444.257 173.669 443.899 173.492 443.435 174.12 Z M 428.149 135.597 L 427.79 134.484 C 427.79 134.484 429.027 134.225 429.566 134.404 C 430.104 134.58 429.744 135.637 429.744 135.637 L 428.149 135.597 Z M 217.747 220.155 C 217.747 220.155 215.176 221.751 214.373 221.907 C 213.572 222.064 214.88 221.2 215.011 220.751 C 215.141 220.302 214.471 219.782 215.344 219.46 C 216.215 219.141 217.747 220.155 217.747 220.155 Z M 486.262 254.779 C 485.49 255.452 485.642 256.096 484.815 256.402 C 484.585 256.488 484.341 254.912 484.664 254.349 C 485.071 253.641 486.095 254.067 486.262 254.779 Z M 488.087 259.673 L 488.163 260.407 C 488.163 260.407 487.697 260.59 487.491 260.519 C 487.286 260.448 487.319 260.153 487.319 260.153 L 487.89 259.789 C 487.89 259.789 488.077 259.567 488.087 259.673 Z M 213.633 226.756 C 215.361 226.541 215.702 227.344 216.538 227.149 C 217.568 226.91 218.156 224.288 219.143 224.524 C 220.156 224.767 219.567 225.432 219.116 226.234 C 218.337 227.616 220.126 227.8 221.135 228.618 C 221.6 228.995 221.731 230.141 221.161 230.076 C 220.615 230.013 220.573 229.467 220.443 229.452 C 220.219 229.426 219.722 230.297 218.814 230.12 C 217.514 229.866 217.818 228.471 216.142 228.169 C 214.363 227.846 213.561 228.846 212.923 228.751 C 212.083 228.625 212.378 226.912 213.633 226.756 Z M 481.565 242.067 C 481.565 242.067 481.823 241.246 482.491 241.458 C 483.081 241.644 483.21 242.662 482.721 242.855 C 482.43 242.972 481.565 242.067 481.565 242.067 Z M 486.082 245.894 C 486.082 245.894 485.459 246.335 485.484 245.71 C 485.509 245.086 486.602 244.646 486.744 245.166 C 486.886 245.688 486.082 245.894 486.082 245.894 Z M 487.64 246.208 L 488.163 246.387 C 488.163 246.387 488.455 246.892 488.196 247 C 487.937 247.108 487.35 246.85 487.35 246.85 C 487.35 246.85 487.214 246.46 487.279 246.334 C 487.345 246.21 487.64 246.208 487.64 246.208 Z M 486.327 248.814 C 486.446 248.272 487.327 248.016 487.562 248.56 C 487.72 248.922 487.294 249.202 486.892 249.332 C 486.441 249.479 486.229 249.256 486.327 248.814 Z M 504.629 203.411 C 504.629 203.411 505.281 202.43 506.029 202.949 C 506.776 203.47 506.319 204.673 505.571 204.544 C 504.821 204.414 504.402 203.737 504.629 203.411 Z M 165.733 116.595 L 164.711 117.18 C 164.711 117.18 163.633 116.326 163.644 115.821 C 163.655 115.316 163.923 114.966 164.475 115.011 C 165.027 115.056 165.344 115.91 165.344 115.91 L 165.733 116.595 Z M 168.647 115.087 C 168.204 115.908 167.319 116.353 166.452 116.035 C 165.807 115.798 166.757 115.052 166.822 114.654 C 166.903 114.158 167.662 114.285 167.89 114.34 C 168.176 114.408 168.99 114.449 168.647 115.087 Z Z";

    public MapTrafficRadarControl()
    {
        this.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0f111a")); 
        this.ClipToBounds = true;

        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _animationTimer.Tick += AnimationTimer_Tick;
        _animationTimer.Start();
        
        this.SizeChanged += (s, e) => DrawStaticMap();
    }

    private void DrawStaticMap()
    {
        this.Children.Clear();
        
        if (ActualWidth == 0 || ActualHeight == 0) return;

        // Draw a base World Map using a pre-defined path
        var geometry = Geometry.Parse(WorldMapData);
        var path = new Path
        {
            Data = geometry,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(100, 30, 40, 50)),
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 255, 255, 255)),
            StrokeThickness = 0.5,
            Stretch = Stretch.Uniform,
            Width = ActualWidth * 0.9,
            Height = ActualHeight * 0.9
        };
        
        SetLeft(path, ActualWidth * 0.05);
        SetTop(path, ActualHeight * 0.05);
        this.Children.Add(path);

        // Draw horizontal equator line and vertical meridian
        var eq = new Line { X1 = 0, Y1 = ActualHeight/2, X2 = ActualWidth, Y2 = ActualHeight/2, Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 0, 255, 100)), StrokeThickness=1 };
        var mer = new Line { X1 = ActualWidth/2, Y1 = 0, X2 = ActualWidth/2, Y2 = ActualHeight, Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 0, 255, 100)), StrokeThickness=1 };
        
        this.Children.Add(eq);
        this.Children.Add(mer);
    }

    private static void OnConnectionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MapTrafficRadarControl)d;
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
    }

    private System.Windows.Point LatLonToXY(double lat, double lon)
    {
        // Simple Equirectangular projection mapping to Canvas dimensions
        var mapWidth = ActualWidth * 0.9;
        var mapHeight = ActualHeight * 0.9;
        
        // standard lon -180 to 180, lat -90 to 90
        var x = (lon + 180) * (mapWidth / 360) + (ActualWidth * 0.05);
        
        // Latitude is flipped because Y axis goes down
        var y = (-lat + 90) * (mapHeight / 180) + (ActualHeight * 0.05);

        return new System.Windows.Point(x, y);
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (ActualWidth == 0 || ActualHeight == 0 || Connections == null) return;
        
        DrawStaticMap(); // Clear dynamic items and redraw map
        
        // Assume origin is center of screen (or maybe actual local GeoIP. Hardcoding to arbitrary local point for demo)
        var origin = new System.Windows.Point(ActualWidth / 2, ActualHeight / 2);
        
        var originNode = new Ellipse
        {
            Width = 10, Height = 10,
            Fill = new SolidColorBrush(Colors.Cyan)
        };
        SetLeft(originNode, origin.X - 5);
        SetTop(originNode, origin.Y - 5);
        this.Children.Add(originNode);

        var activeConnections = Connections
            .Where(c => c.DownloadSpeed > 0 || c.UploadSpeed > 0)
            .OrderByDescending(c => c.DownloadSpeed)
            .Take(20)
            .ToList();

        foreach (var conn in activeConnections)
        {
            // If location isn't resolved yet, default to a random local perimeter
            var targetX = origin.X;
            var targetY = origin.Y;

            if (conn.Country != null)
            {
                var pt = LatLonToXY(conn.Latitude, conn.Longitude);
                targetX = pt.X;
                targetY = pt.Y;
            }
            else
            {
                // Fallback circular cluster if no IP geo available
                var hash = Math.Abs(conn.Id.GetHashCode());
                double angle = (hash % 360) * Math.PI / 180.0;
                targetX = origin.X + Math.Cos(angle) * 100;
                targetY = origin.Y + Math.Sin(angle) * 100;
            }

            // Draw Parabolic curve
            var path = new Path();
            path.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 0, 255, 100));
            path.StrokeThickness = Math.Max(1, Math.Min(4, conn.DownloadSpeed / 100000.0)); // Scale with speed

            var geom = new PathGeometry();
            var figure = new PathFigure { StartPoint = origin };
            
            // Calculate a control point to make it arc up
            var midX = (origin.X + targetX) / 2;
            var midY = (origin.Y + targetY) / 2 - 50; // Pull Arc upwards
            
            figure.Segments.Add(new QuadraticBezierSegment(new System.Windows.Point(midX, midY), new System.Windows.Point(targetX, targetY), true));
            geom.Figures.Add(figure);
            path.Data = geom;
            
            this.Children.Add(path);

            // Draw glowing target dot
            var size = 4 + Math.Min(10, conn.DownloadSpeed / 100000.0);
            var dot = new Ellipse
            {
                Width = size, Height = size,
                Fill = new SolidColorBrush(Colors.LightGreen)
            };
            SetLeft(dot, targetX - size/2);
            SetTop(dot, targetY - size/2);
            this.Children.Add(dot);

            // Detailed text label as requested
            var speedStr = conn.DownloadSpeed > 1024 * 1024 
                ? $"{(conn.DownloadSpeed / 1024.0 / 1024.0):F1} MB/s" 
                : $"{(conn.DownloadSpeed / 1024.0):F1} KB/s";
            
            // Use process name or hostname if process missing
            string labelName = string.IsNullOrWhiteSpace(conn.Process) ? conn.Host : conn.Process;
            if (labelName.Length > 15) labelName = labelName.Substring(0, 15) + "..";

            var label = new TextBlock
            {
                Text = $"{labelName} ▼ {speedStr}",
                Foreground = new SolidColorBrush(Colors.White),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(100, 0,0,0)), // semi transparent bg for readability
                FontSize = 10,
                Padding = new Thickness(2)
            };
            
            SetLeft(label, targetX + size);
            SetTop(label, targetY - 10);
            this.Children.Add(label);
        }
    }
}
