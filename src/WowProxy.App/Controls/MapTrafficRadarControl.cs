using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WowProxy.App.Models;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace WowProxy.App.Controls;

/// <summary>
/// A world-map radar visualization showing live traffic connections as animated arcs.
/// Architecture:
///   Layer 0: Background (dark gradient)
///   Layer 1: Grid lines (lat/lon grid, equator, prime meridian)
///   Layer 2: World map landmass polygon (static, redrawn only on resize)
///   Layer 3: Dynamic connection arcs + labels (redrawn each tick)
///   Layer 4: Animated "data packet" dots traveling along arcs (moved each tick)
/// </summary>
public class MapTrafficRadarControl : Canvas
{
    // ─── Constants ──────────────────────────────────────────────────────────────

    private const double MapPadX = 0.03;   // fractional horizontal padding
    private const double MapPadY = 0.06;   // fractional vertical padding

    // Origin: Beijing (default; in a real app, detect local public IP's geo)
    private const double OriginLat = 39.9;
    private const double OriginLon = 116.4;

    // ─── Fields ──────────────────────────────────────────────────────────────────

    private readonly Canvas _mapLayer  = new() { IsHitTestVisible = false };
    private readonly Canvas _arcLayer  = new() { IsHitTestVisible = false };
    private readonly Canvas _dotLayer  = new() { IsHitTestVisible = false };

    private readonly DispatcherTimer _animTimer;
    private readonly Random _rng = new();

    // Tracks animated packets per connection id
    private readonly Dictionary<string, PacketDot> _packets = new();

    // Last snapshot of connections used for drawing
    private List<ConnectionModel> _lastSnapshot = new();

    private double _lastWidth;
    private double _lastHeight;

    // ─── Dependency Property ─────────────────────────────────────────────────────

    public static readonly DependencyProperty ConnectionsProperty =
        DependencyProperty.Register(
            nameof(Connections),
            typeof(ObservableCollection<ConnectionModel>),
            typeof(MapTrafficRadarControl),
            new PropertyMetadata(null, OnConnectionsChanged));

    public ObservableCollection<ConnectionModel> Connections
    {
        get => (ObservableCollection<ConnectionModel>)GetValue(ConnectionsProperty);
        set => SetValue(ConnectionsProperty, value);
    }

    // ─── Constructor ─────────────────────────────────────────────────────────────

    public MapTrafficRadarControl()
    {
        ClipToBounds = true;
        Background = new SolidColorBrush(WpfColor.FromRgb(3, 10, 30));

        // Stack layers
        Children.Add(_mapLayer);
        Children.Add(_arcLayer);
        Children.Add(_dotLayer);

        SizeChanged += (_, _) => OnResize();

        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _animTimer.Tick += OnAnimTick;
        _animTimer.Start();
    }

    // ─── Resize ──────────────────────────────────────────────────────────────────

    private void OnResize()
    {
        if (ActualWidth < 10 || ActualHeight < 10) return;

        // Stretch sub-canvases to fill
        foreach (Canvas c in new[] { _mapLayer, _arcLayer, _dotLayer })
        {
            c.Width = ActualWidth;
            c.Height = ActualHeight;
        }

        // Only redraw the static map if size actually changed
        if (Math.Abs(ActualWidth - _lastWidth) > 0.5 || Math.Abs(ActualHeight - _lastHeight) > 0.5)
        {
            _lastWidth = ActualWidth;
            _lastHeight = ActualHeight;
            DrawStaticMap();
        }
    }

    // ─── Static Map (Layer 1 + 2) ────────────────────────────────────────────────

    private void DrawStaticMap()
    {
        _mapLayer.Children.Clear();

        double w = ActualWidth;
        double h = ActualHeight;

          // --- Background gradient ---
          var bgGradient = new LinearGradientBrush
          {
              StartPoint = new WpfPoint(0, 0),
              EndPoint = new WpfPoint(0, 1)
          };
          bgGradient.GradientStops.Add(new GradientStop(WpfColor.FromRgb(2, 9, 30), 0.0));
          bgGradient.GradientStops.Add(new GradientStop(WpfColor.FromRgb(4, 18, 48), 0.55));
          bgGradient.GradientStops.Add(new GradientStop(WpfColor.FromRgb(4, 13, 38), 1.0));

          var bgRect = new WpfRectangle
          {
              Width = w,
              Height = h,
              Fill = bgGradient
          };
        _mapLayer.Children.Add(bgRect);

        // --- Grid lines ---
        // Latitude lines every 30°
        for (int lat = -60; lat <= 60; lat += 30)
        {
            var pt1 = LatLonToXY(lat, -180);
            var pt2 = LatLonToXY(lat, 180);
            bool isEquator = lat == 0;
              _mapLayer.Children.Add(new Line
              {
                  X1 = pt1.X, Y1 = pt1.Y, X2 = pt2.X, Y2 = pt2.Y,
                  Stroke = new SolidColorBrush(WpfColor.FromArgb(isEquator ? (byte)88 : (byte)38, 18, 174, 204)),
                  StrokeThickness = isEquator ? 1.2 : 0.55,
                  StrokeDashArray = isEquator ? null : new DoubleCollection { 4, 6 }
              });
        }

        // Longitude lines every 30°
        for (int lon = -180; lon <= 180; lon += 30)
        {
            var pt1 = LatLonToXY(90, lon);
            var pt2 = LatLonToXY(-90, lon);
            bool isPrime = lon == 0;
              _mapLayer.Children.Add(new Line
              {
                  X1 = pt1.X, Y1 = pt1.Y, X2 = pt2.X, Y2 = pt2.Y,
                  Stroke = new SolidColorBrush(WpfColor.FromArgb(isPrime ? (byte)84 : (byte)34, 18, 174, 204)),
                  StrokeThickness = isPrime ? 1.1 : 0.5,
                  StrokeDashArray = isPrime ? null : new DoubleCollection { 4, 6 }
              });
        }

          // --- Single global ocean tint (clean, no basin intersections) ---
          DrawOceanBase();

          // --- World map landmass ---
            DrawLandmass();


        // --- Geographic labels ---
        DrawGeographyLabels();

        // --- Origin marker (local machine) ---
        var origin = LatLonToXY(OriginLat, OriginLon);
          DrawPulseRing(_mapLayer, origin, 14, WpfColor.FromRgb(0, 255, 210), 1.8);
          DrawDot(_mapLayer, origin, 5, WpfColor.FromRgb(0, 204, 255));

        // Label
        var originLabel = new TextBlock
        {
              Text = "YOU",
              Foreground = new SolidColorBrush(WpfColor.FromRgb(74, 220, 255)),
            FontSize = 9,
            FontWeight = FontWeights.Bold
        };
        Canvas.SetLeft(originLabel, origin.X + 8);
        Canvas.SetTop(originLabel, origin.Y - 6);
        _mapLayer.Children.Add(originLabel);
    }

      private void DrawOceanBase()
      {
          // One global ocean body to keep the scene clean while preserving cyber depth.
          var oceanGradient = new LinearGradientBrush
          {
              StartPoint = new WpfPoint(0, 0),
              EndPoint = new WpfPoint(0, 1)
          };
          oceanGradient.GradientStops.Add(new GradientStop(WpfColor.FromArgb(102, 8, 58, 108), 0.00));
          oceanGradient.GradientStops.Add(new GradientStop(WpfColor.FromArgb(90, 6, 44, 96), 0.52));
          oceanGradient.GradientStops.Add(new GradientStop(WpfColor.FromArgb(112, 5, 36, 84), 1.00));

          var northWest = LatLonToXY(64, -180);
          var northEast = LatLonToXY(64, 180);
          var southEast = LatLonToXY(-66, 180);
          var southWest = LatLonToXY(-66, -180);

          var ocean = new Polygon
          {
              Points = new PointCollection { northWest, northEast, southEast, southWest },
              Fill = oceanGradient,
              Stroke = new SolidColorBrush(WpfColor.FromArgb(52, 18, 174, 204)),
              StrokeThickness = 0.65,
              IsHitTestVisible = false
          };

          _mapLayer.Children.Add(ocean);
      }

      private void DrawLandmass()
      {
          foreach (var polygon in WorldLandPolygons)
          {
              if (polygon.Length < 3) continue;

              var pts = new PointCollection(polygon.Length);
              foreach (var (lat, lon) in polygon)
                  pts.Add(LatLonToXY(lat, lon));

              var (fillColor, strokeColor) = GetLandStyleForPolygon(polygon);
              var shape = new Polygon
              {
                  Points = pts,
                  Fill = new SolidColorBrush(fillColor),
                  Stroke = new SolidColorBrush(strokeColor),
                  StrokeThickness = 0.75,
                  IsHitTestVisible = false
              };
              _mapLayer.Children.Add(shape);
          }
      }


    private void DrawGeographyLabels()
    {
        foreach (var continent in Continents)
        {
            var pos = LatLonToXY(continent.LabelLat, continent.LabelLon);
            var tb = new TextBlock
            {
                Text = continent.Name,
                Foreground = new SolidColorBrush(GetContinentLabelColor(continent.Name)),
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                IsHitTestVisible = false
            };

            Canvas.SetLeft(tb, pos.X - continent.Name.Length * 3.0);
            Canvas.SetTop(tb, pos.Y - 8);
            _mapLayer.Children.Add(tb);
        }
    }

    // ─── Animation Tick (Layer 3 + 4) ────────────────────────────────────────────

    private void OnAnimTick(object? sender, EventArgs e)
    {
        if (ActualWidth < 10 || ActualHeight < 10) return;

        // Ensure sub-canvas sizes are up to date
        if (_arcLayer.Width != ActualWidth)
            OnResize();

        // Get active connections snapshot (top 25 by download speed)
        _lastSnapshot = Connections?
            .Where(c => c.DownloadSpeed > 0 || c.UploadSpeed > 0)
            .OrderByDescending(c => c.DownloadSpeed + c.UploadSpeed)
            .Take(25)
            .ToList() ?? new();

        DrawArcs();
        TickPackets();
    }

    private void DrawArcs()
    {
        _arcLayer.Children.Clear();

        var origin = LatLonToXY(OriginLat, OriginLon);

        foreach (var conn in _lastSnapshot)
        {
            var target = GetConnectionPoint(conn, origin);
            double speed = conn.DownloadSpeed + conn.UploadSpeed;
            double t = Math.Min(1.0, speed / 2_000_000.0); // 0..1 based on 2 MB/s max
            var color = InterpolateColor(
                WpfColor.FromRgb(0, 80, 200),    // slow: blue
                WpfColor.FromRgb(0, 240, 100),   // fast: green
                t);

            byte alpha = (byte)(120 + 80 * t);
            double thickness = 0.8 + 2.5 * t;

            // Arc control point (pull upward toward top-center)
            var ctrl = ArcControlPoint(origin, target);

            // Draw arc
            var geom = new PathGeometry();
            var fig = new PathFigure { StartPoint = origin };
            fig.Segments.Add(new QuadraticBezierSegment(ctrl, target, true));
            geom.Figures.Add(fig);

            _arcLayer.Children.Add(new Path
            {
                Data = geom,
                Stroke = new SolidColorBrush(WpfColor.FromArgb(alpha, color.R, color.G, color.B)),
                StrokeThickness = thickness,
                IsHitTestVisible = false
            });

            // Target dot
            double dotR = 3 + 5 * t;
            DrawDot(_arcLayer, target, dotR, WpfColor.FromArgb(alpha, color.R, color.G, color.B));
            DrawPulseRing(_arcLayer, target, dotR * 3, WpfColor.FromArgb((byte)(alpha / 3), color.R, color.G, color.B), 0.8);

            // Speed label
            string label = BuildLabel(conn);
            var tb = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(WpfColor.FromArgb(200, color.R, color.G, color.B)),
                Background = new SolidColorBrush(WpfColor.FromArgb(90, 0, 0, 0)),
                FontSize = 9.5,
                Padding = new Thickness(2, 1, 2, 1)
            };
            Canvas.SetLeft(tb, target.X + dotR + 2);
            Canvas.SetTop(tb, target.Y - 8);
            _arcLayer.Children.Add(tb);

            // Ensure a packet exists for this connection
            if (!_packets.ContainsKey(conn.Id))
            {
                _packets[conn.Id] = new PacketDot
                {
                    ConnId = conn.Id,
                    Progress = _rng.NextDouble(),
                    Color = color
                };
            }
            // Update packet path info
            var pkt = _packets[conn.Id];
            pkt.Origin = origin;
            pkt.Target = target;
            pkt.Control = ctrl;
            pkt.Color = color;
            pkt.Speed = 0.008 + 0.018 * t; // faster for high-speed connections
        }

        // Remove stale packets
        var activeIds = _lastSnapshot.Select(c => c.Id).ToHashSet();
        foreach (var key in _packets.Keys.Where(k => !activeIds.Contains(k)).ToList())
            _packets.Remove(key);
    }

    private void TickPackets()
    {
        _dotLayer.Children.Clear();

        foreach (var pkt in _packets.Values)
        {
            pkt.Progress = (pkt.Progress + pkt.Speed) % 1.0;

            var pos = QuadBezierPoint(pkt.Origin, pkt.Control, pkt.Target, pkt.Progress);

            // Bright moving dot
            double r = 4;
            DrawDot(_dotLayer, pos, r, WpfColor.FromArgb(230, pkt.Color.R, pkt.Color.G, pkt.Color.B));

            // Soft glow halo
            DrawDot(_dotLayer, pos, r * 2.5,
                WpfColor.FromArgb(50, pkt.Color.R, pkt.Color.G, pkt.Color.B));
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private WpfPoint GetConnectionPoint(ConnectionModel conn, WpfPoint fallback)
    {
        if (conn.Country != null)
            return LatLonToXY(conn.Latitude, conn.Longitude);

        // Deterministic fallback based on connection id hash
        var hash = Math.Abs(conn.Id.GetHashCode());
        double angle = (hash % 360) * Math.PI / 180.0;
        double r = 80 + (hash % 120);
        return new WpfPoint(fallback.X + Math.Cos(angle) * r, fallback.Y + Math.Sin(angle) * r);
    }

    private static WpfPoint ArcControlPoint(WpfPoint from, WpfPoint to)
    {
        double mx = (from.X + to.X) / 2;
        double my = (from.Y + to.Y) / 2;

        // Pull control point upward, proportional to distance
        double dist = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
        double lift = Math.Min(dist * 0.4, 180);

        return new WpfPoint(mx, my - lift);
    }

    private static WpfPoint QuadBezierPoint(WpfPoint p0, WpfPoint p1, WpfPoint p2, double t)
    {
        double u = 1 - t;
        double x = u * u * p0.X + 2 * u * t * p1.X + t * t * p2.X;
        double y = u * u * p0.Y + 2 * u * t * p1.Y + t * t * p2.Y;
        return new WpfPoint(x, y);
    }

    private WpfPoint LatLonToXY(double lat, double lon)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        double padX = w * MapPadX;
        double padY = h * MapPadY;
        double mapW = w - 2 * padX;
        double mapH = h - 2 * padY;

        double x = (lon + 180.0) / 360.0 * mapW + padX;
        double y = (1.0 - (lat + 90.0) / 180.0) * mapH + padY;
        return new WpfPoint(x, y);
    }

    private static void DrawDot(Canvas canvas, WpfPoint center, double radius, WpfColor color)
    {
        var e = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = new SolidColorBrush(color),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(e, center.X - radius);
        Canvas.SetTop(e, center.Y - radius);
        canvas.Children.Add(e);
    }

    private static void DrawPulseRing(Canvas canvas, WpfPoint center, double radius, WpfColor color, double thickness)
    {
        var e = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            Fill = WpfBrushes.Transparent,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(e, center.X - radius);
        Canvas.SetTop(e, center.Y - radius);
        canvas.Children.Add(e);
    }

    private static WpfColor InterpolateColor(WpfColor a, WpfColor b, double t)
    {
        t = Math.Max(0, Math.Min(1, t));
        return WpfColor.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

      private static string BuildLabel(ConnectionModel conn)
      {
          string name = string.IsNullOrWhiteSpace(conn.SiteName)
              ? (string.IsNullOrWhiteSpace(conn.Process) ? conn.Host : conn.Process)
              : conn.SiteName;
          if (name.Length > 18) name = name[..16] + "..";

          long dl = conn.DownloadSpeed;
          string speed = dl >= 1024 * 1024
              ? $"{dl / 1048576.0:F1}MB/s"
              : $"{dl / 1024.0:F0}KB/s";

          return $"{name}  ▼{speed}";
      }

        private static (WpfColor Fill, WpfColor Stroke) GetLandStyleForPolygon((double lat, double lon)[] polygon)
        {
            var first = polygon[0];
            if (first.lat < -60)
                return (WpfColor.FromArgb(188, 52, 110, 168), WpfColor.FromArgb(220, 104, 200, 255)); // Antarctica

            if (first.lon <= -80 && first.lat >= 15)
                return (WpfColor.FromArgb(154, 18, 70, 132), WpfColor.FromArgb(204, 52, 168, 235)); // North America

            if (first.lon <= -34 && first.lat < 20)
                return (WpfColor.FromArgb(154, 14, 82, 144), WpfColor.FromArgb(206, 44, 186, 248)); // South America

            if (first.lon < 45 && first.lat >= 35)
                return (WpfColor.FromArgb(154, 28, 84, 136), WpfColor.FromArgb(206, 78, 188, 240)); // Europe + islands

            if (first.lon < 60 && first.lat < 35)
                return (WpfColor.FromArgb(154, 20, 96, 132), WpfColor.FromArgb(204, 56, 196, 232)); // Africa

            if (first.lon >= 60 && first.lat < 0)
                return (WpfColor.FromArgb(154, 24, 90, 148), WpfColor.FromArgb(208, 62, 198, 248)); // Oceania + SE islands

            return (WpfColor.FromArgb(156, 24, 78, 140), WpfColor.FromArgb(208, 60, 182, 244)); // Asia default
        }



      private static WpfColor GetContinentLabelColor(string continentName)
      {
          return continentName switch
          {
              "North America" => WpfColor.FromArgb(224, 118, 208, 248),
              "South America" => WpfColor.FromArgb(224, 106, 206, 252),
              "Europe" => WpfColor.FromArgb(226, 132, 216, 255),
              "Africa" => WpfColor.FromArgb(224, 118, 224, 246),
              "Asia" => WpfColor.FromArgb(226, 124, 220, 255),
              "Oceania" => WpfColor.FromArgb(224, 138, 224, 255),
              _ => WpfColor.FromArgb(220, 156, 230, 255), // Antarctica
          };
      }

      // ─── Collection Change Handling ──────────────────────────────────────────────

    private static void OnConnectionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (MapTrafficRadarControl)d;
        if (e.OldValue is ObservableCollection<ConnectionModel> old)
            old.CollectionChanged -= ctrl.Connections_CollectionChanged;
        if (e.NewValue is ObservableCollection<ConnectionModel> nw)
            nw.CollectionChanged += ctrl.Connections_CollectionChanged;
    }

    private void Connections_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) { }

    // ─── World Landmass Polygons ──────────────────────────────────────────────────
    // Low-resolution outlines of the main continents as (latitude, longitude) tuples.
    // Positive lat = North, positive lon = East.

    private static readonly (string Name, double LabelLat, double LabelLon)[] Continents =
    {
        ("North America", 48, -103),
        ("South America", -18, -60),
        ("Europe", 53, 16),
        ("Africa", 8, 20),
        ("Asia", 43, 90),
        ("Oceania", -23, 137),
        ("Antarctica", -77, 0),
    };

    private static readonly (double lat, double lon)[][] WorldLandPolygons =
    {
        // ── North America ──
        new (double lat, double lon)[]
        {
            (71, -141), (71, -120), (69, -105), (70, -85), (61, -75), (60, -65),
            (47, -53), (44, -66), (35, -75), (25, -80), (25, -90), (15, -85),
            (10, -85), (8, -77), (10, -75), (20, -87), (22, -97), (22, -105),
            (30, -110), (32, -117), (37, -122), (48, -124), (50, -128),
            (55, -130), (58, -137), (60, -145), (58, -152), (55, -162),
            (60, -165), (64, -166), (67, -164), (70, -158), (71, -156),
            (71, -141)
        },

        // ── Greenland ──
        new (double lat, double lon)[]
        {
            (83, -30), (83, -60), (76, -73), (68, -54), (60, -43), (61, -48),
            (65, -37), (72, -22), (77, -18), (83, -30)
        },

        // ── South America ──
        new (double lat, double lon)[]
        {
            // Clockwise path with fuller east-coast bulge and tapered southern cone.
            (12, -71), (11, -75), (8, -78), (4, -80), (0, -81), (-5, -80),
            (-10, -78), (-15, -76), (-20, -74), (-26, -72), (-33, -71),
            (-40, -72), (-47, -74), (-53, -72), (-56, -68), (-55, -64),
            (-52, -61), (-48, -59), (-43, -56), (-38, -54), (-32, -51),
            (-25, -47), (-18, -44), (-11, -40), (-6, -37), (-1, -36),
            (4, -40), (7, -46), (9, -52), (10, -58), (11, -64), (12, -68),
            (12, -71)
        },

        // ── Europe (simplified) ──
        new (double lat, double lon)[]
        {
            (71, 28), (70, 18), (63, 5), (58, 5), (51, 2), (48, -5),
            (43, -9), (36, -9), (36, -5), (38, 0), (43, 3), (44, 8),
            (43, 13), (40, 18), (41, 20), (42, 28), (45, 30), (46, 30),
            (48, 22), (50, 22), (54, 18), (56, 21), (59, 24), (60, 25),
            (60, 30), (65, 30), (68, 28), (70, 30), (71, 28)
        },

        // ── Africa ──
        new (double lat, double lon)[]
        {
            (37, 10), (37, 9), (33, 10), (25, 37), (12, 44), (11, 43),
            (8, 42), (2, 42), (-5, 40), (-12, 40), (-26, 33), (-34, 26),
            (-34, 18), (-29, 17), (-18, 12), (-7, 12), (5, 2), (5, -5),
            (10, -15), (15, -17), (20, -17), (28, -13), (35, -5), (37, 10)
        },

        // ── Asia (main body, very simplified) ──
        new (double lat, double lon)[]
        {
            (71, 28), (71, 50), (68, 60), (66, 70), (68, 80), (70, 100),
            (72, 110), (70, 130), (67, 140), (60, 140), (60, 150), (52, 140),
            (46, 135), (42, 130), (38, 121), (30, 121), (23, 116), (22, 113),
            (15, 108), (10, 104), (5, 102), (1, 104), (5, 100), (10, 99),
            (18, 100), (22, 98), (28, 98), (27, 88), (22, 88), (20, 80),
            (14, 80), (8, 77), (8, 78), (12, 80), (22, 68), (28, 62),
            (24, 58), (24, 52), (27, 50), (30, 48), (30, 38), (37, 36),
            (37, 28), (41, 26), (42, 28), (45, 30), (50, 30), (55, 38),
            (60, 42), (65, 42), (68, 50), (71, 50), (71, 28)
        },

        // ── Japan (simplified) ──
        new (double lat, double lon)[]
        {
            (31, 131), (33, 132), (35, 137), (35, 140), (37, 141),
            (41, 141), (43, 145), (44, 145), (44, 141), (40, 140),
            (36, 138), (34, 131), (31, 131)
        },

        // ── Australia ──
        new (double lat, double lon)[]
        {
            (-14, 126), (-12, 130), (-12, 136), (-18, 140), (-24, 150),
            (-38, 148), (-38, 145), (-36, 136), (-32, 133), (-32, 116),
            (-24, 114), (-18, 122), (-14, 126)
        },

        // ── New Zealand (North Island simplified) ──
        new (double lat, double lon)[]
        {
            (-34, 173), (-37, 175), (-41, 175), (-41, 172), (-34, 173)
        },

        // ── British Isles ──
        new (double lat, double lon)[]
        {
            (58, -5), (58, -3), (56, -2), (54, -3), (51, -3),
            (50, -5), (52, -5), (54, -6), (55, -6), (56, -6),
            (58, -5)
        },

        // ── Indonesia / SE Asia islands (very rough) ──
        new (double lat, double lon)[]
        {
            (5, 95), (4, 98), (2, 100), (0, 102), (-2, 104), (-6, 107),
            (-8, 115), (-8, 118), (-8, 124), (-4, 122), (-2, 120),
            (0, 119), (4, 118), (6, 116), (7, 110), (5, 104), (5, 95)
        },

        // ── Philippines (rough outline) ──
        new (double lat, double lon)[]
        {
            (18, 120), (20, 122), (18, 123), (12, 125), (8, 124),
            (8, 123), (10, 122), (12, 120), (15, 120), (18, 120)
        },

        // ── Iceland ──
        new (double lat, double lon)[]
        {
            (66, -24), (66, -14), (64, -13), (63, -18), (64, -22), (66, -24)
        },



        // ── Madagascar ──
        new (double lat, double lon)[]
        {
            (-12, 49), (-15, 50), (-20, 49), (-24, 47), (-25, 45), (-22, 44),
            (-17, 45), (-13, 47), (-12, 49)
        },

        // ── New Guinea ──
        new (double lat, double lon)[]
        {
            (-2, 131), (-2, 139), (-4, 147), (-7, 151), (-9, 148), (-9, 140),
            (-7, 134), (-4, 131), (-2, 131)
        },

        // ── New Zealand (South Island simplified) ──
        new (double lat, double lon)[]
        {
            (-41, 166), (-43, 169), (-46, 170), (-46, 166), (-44, 167), (-41, 166)
        },

        // ── Cuba ──
        new (double lat, double lon)[]
        {
            (23, -85), (23, -80), (22, -76), (21, -75), (20, -77), (20, -82), (21, -84), (23, -85)
        },

        // ── Hispaniola ──
        new (double lat, double lon)[]
        {
            (20, -74), (20, -70), (18, -68), (17, -71), (18, -74), (20, -74)
        },

        // ── Svalbard ──
        new (double lat, double lon)[]
        {
            (80, 10), (80, 22), (78, 25), (77, 18), (78, 10), (80, 10)
        },

        // ── Hawaiian Islands (very rough chain) ──
        new (double lat, double lon)[]
        {
            (22, -160), (22, -157), (21, -155), (19, -155), (19, -157), (20, -160), (22, -160)
        },

        // ── Falkland Islands ──
        new (double lat, double lon)[]
        {
            (-51, -61), (-51, -57), (-53, -57), (-53, -61), (-51, -61)
        },

        // ── Antarctica (stylized belt) ──
        new (double lat, double lon)[]
        {
            (-66, -179), (-68, -160), (-70, -130), (-71, -100), (-72, -70),
            (-73, -40), (-74, -10), (-73, 20), (-72, 50), (-71, 80),
            (-70, 110), (-69, 140), (-68, 165), (-66, 179),
            (-80, 179), (-82, 120), (-83, 60), (-84, 0), (-83, -60),
            (-82, -120), (-80, -179), (-66, -179)
        },
    };

    // ─── Packet dot state ────────────────────────────────────────────────────────

    private sealed class PacketDot
    {
        public string ConnId = "";
        public double Progress;   // 0..1 along arc
        public double Speed;
        public WpfPoint Origin;
        public WpfPoint Target;
        public WpfPoint Control;
        public WpfColor Color;
    }
}
