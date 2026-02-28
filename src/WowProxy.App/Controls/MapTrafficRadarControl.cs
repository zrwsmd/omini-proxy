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
        Background = new SolidColorBrush(WpfColor.FromRgb(8, 12, 26));

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
        var bgRect = new WpfRectangle
        {
            Width = w,
            Height = h,
            Fill = new LinearGradientBrush(
                WpfColor.FromRgb(6, 10, 22),
                WpfColor.FromRgb(12, 18, 40),
                new WpfPoint(0, 0),
                new WpfPoint(0, 1))
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
                Stroke = new SolidColorBrush(WpfColor.FromArgb(isEquator ? (byte)45 : (byte)20, 0, 220, 120)),
                StrokeThickness = isEquator ? 1.0 : 0.5,
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
                Stroke = new SolidColorBrush(WpfColor.FromArgb(isPrime ? (byte)45 : (byte)15, 0, 220, 120)),
                StrokeThickness = isPrime ? 1.0 : 0.5,
                StrokeDashArray = isPrime ? null : new DoubleCollection { 4, 6 }
            });
        }

        // --- Ocean basins + labels ---
        DrawOceanBasins();

        // --- World map landmass ---
        DrawLandmass();

        // --- Geographic labels ---
        DrawGeographyLabels();

        // --- Origin marker (local machine) ---
        var origin = LatLonToXY(OriginLat, OriginLon);
        DrawPulseRing(_mapLayer, origin, 14, WpfColor.FromRgb(0, 255, 180), 2.0);
        DrawDot(_mapLayer, origin, 5, WpfColor.FromRgb(0, 255, 180));

        // Label
        var originLabel = new TextBlock
        {
            Text = "YOU",
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0, 255, 180)),
            FontSize = 9,
            FontWeight = FontWeights.Bold
        };
        Canvas.SetLeft(originLabel, origin.X + 8);
        Canvas.SetTop(originLabel, origin.Y - 6);
        _mapLayer.Children.Add(originLabel);
    }

    private void DrawLandmass()
    {
        // Simplified world land polygons as lat/lon coordinate arrays.
        // Each entry is an array of (lat, lon) pairs forming a closed polygon.
        // Accuracy is low-resolution (good enough for a radar-style map).
        foreach (var polygon in WorldLandPolygons)
        {
            if (polygon.Length < 3) continue;

            var pts = new PointCollection(polygon.Length);
            foreach (var (lat, lon) in polygon)
                pts.Add(LatLonToXY(lat, lon));

            var shape = new Polygon
            {
                Points = pts,
                Fill = new SolidColorBrush(WpfColor.FromArgb(76, 20, 55, 90)),
                Stroke = new SolidColorBrush(WpfColor.FromArgb(118, 30, 130, 180)),
                StrokeThickness = 0.65
            };
            _mapLayer.Children.Add(shape);
        }
    }

    private void DrawOceanBasins()
    {
        foreach (var basin in OceanBasins)
        {
            if (basin.Polygon.Length < 3) continue;

            var pts = new PointCollection(basin.Polygon.Length);
            foreach (var (lat, lon) in basin.Polygon)
                pts.Add(LatLonToXY(lat, lon));

            _mapLayer.Children.Add(new Polygon
            {
                Points = pts,
                Fill = new SolidColorBrush(WpfColor.FromArgb(34, 0, 120, 170)),
                Stroke = new SolidColorBrush(WpfColor.FromArgb(42, 0, 170, 210)),
                StrokeThickness = 0.45,
                IsHitTestVisible = false
            });

            var labelPos = LatLonToXY(basin.LabelLat, basin.LabelLon);
            var label = new TextBlock
            {
                Text = basin.Name,
                Foreground = new SolidColorBrush(WpfColor.FromArgb(128, 120, 220, 240)),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.8,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, labelPos.X - basin.Name.Length * 2.8);
            Canvas.SetTop(label, labelPos.Y - 7);
            _mapLayer.Children.Add(label);
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
                Foreground = new SolidColorBrush(WpfColor.FromArgb(150, 150, 220, 255)),
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

    private static readonly (string Name, double LabelLat, double LabelLon, (double lat, double lon)[] Polygon)[] OceanBasins =
    {
        ("Pacific Ocean", 3, -155, new (double lat, double lon)[]
        {
            (58, -179), (58, -130), (42, -108), (20, -93), (2, -83), (-25, -76),
            (-52, -74), (-58, -112), (-60, -155), (-56, -179), (58, -179)
        }),
        ("Atlantic Ocean", 8, -26, new (double lat, double lon)[]
        {
            (72, -74), (72, 14), (60, 12), (48, 2), (34, -6), (20, -12),
            (6, -16), (-8, -20), (-24, -22), (-40, -20), (-54, -16),
            (-58, -24), (-58, -52), (-50, -58), (-34, -60), (-16, -56),
            (2, -50), (22, -46), (42, -46), (58, -54), (72, -74)
        }),
        ("Indian Ocean", -15, 78, new (double lat, double lon)[]
        {
            (30, 32), (30, 114), (10, 112), (-6, 109), (-16, 103), (-32, 95),
            (-44, 76), (-44, 42), (-24, 34), (-2, 38), (20, 42), (30, 32)
        }),
        ("Arctic Ocean", 77, 15, new (double lat, double lon)[]
        {
            (84, -179), (84, 179), (72, 179), (70, 120), (72, 70), (72, 20),
            (70, -20), (72, -70), (70, -125), (72, -170), (84, -179)
        }),
        ("Southern Ocean", -63, 10, new (double lat, double lon)[]
        {
            (-58, -179), (-58, 179), (-72, 179), (-74, 120), (-72, 40),
            (-74, -40), (-72, -120), (-58, -179)
        }),
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

        // ── Scandinavia ──
        new (double lat, double lon)[]
        {
            (58, 5), (56, 8), (57, 10), (58, 12), (57, 12), (55, 14),
            (56, 15), (57, 18), (60, 18), (60, 25), (65, 25), (68, 18),
            (70, 18), (71, 28), (70, 30), (68, 28), (65, 30),
            (63, 28), (62, 26), (63, 22), (65, 14), (65, 10),
            (63, 5), (58, 5)
        },

        // ── Sri Lanka ──
        new (double lat, double lon)[]
        {
            (10, 80), (9, 81), (7, 81), (6, 80), (8, 79), (10, 80)
        },

        // ── Taiwan ──
        new (double lat, double lon)[]
        {
            (25, 121), (25, 122), (23, 122), (22, 121), (23, 120), (25, 121)
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
