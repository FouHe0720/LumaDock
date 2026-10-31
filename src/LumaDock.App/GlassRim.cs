using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace LumaDock.App;

/// <summary>Original layered glass edge and a short, input-driven reflection along its contour.</summary>
internal sealed class GlassRim : FrameworkElement
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(nameof(Radius), typeof(double), typeof(GlassRim), new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    private static readonly Pen Outer = Edge(1, (0, 152), (.24, 43), (.58, 12), (1, 76));
    private static readonly Pen Inner = Edge(.75, (0, 66), (.3, 12), (.68, 3), (1, 34));
    private static readonly Pen Lower = Edge(.85, [(0, 0), (.44, 0), (.76, 22), (1, 68)], true);
    private static readonly Pen Deep = Edge(.6, (0, 21), (.38, 0), (.7, 0), (1, 14));
    private static readonly Brush Reflection = Sheen();
    private const long LifetimeMilliseconds = 620;
    private const long ReleaseMilliseconds = 180;
    private readonly DispatcherTimer motion;
    private bool motionEnabled = true;
    private double motionStrength = .45;
    private Color accentColor = Color.FromRgb(92, 185, 197);
    private Point lastPosition = new(-1, -1);
    private long lastInput, lastTick, releasedAt;
    private double position, target, phase, envelope, impulse = .6, direction = 1;
    private bool drawStaticEdge = true;
    private Contour? outerContour, innerContour;

    public bool DrawStaticEdge
    {
        get => drawStaticEdge;
        set { if (drawStaticEdge == value) return; drawStaticEdge = value; InvalidateVisual(); }
    }

    /// <summary>Optional exact contours, cached only on geometry changes. The ordinary window keeps its rounded contour.</summary>
    internal void SetMotionContours(Geometry? outer, Geometry? inner)
    {
        outerContour = Contour.Create(outer); innerContour = Contour.Create(inner);
        InvalidateVisual();
    }

    public bool MotionEnabled
    {
        get => motionEnabled;
        set { if (motionEnabled == value) return; motionEnabled = value; if (!value) StopMotion(); }
    }
    public double MotionStrength
    {
        get => motionStrength;
        set
        {
            var next = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : .45;
            if (motionStrength == next) return;
            motionStrength = next;
            if (next == 0) StopMotion(); else if (motion.IsEnabled) InvalidateVisual();
        }
    }
    public Color AccentColor
    {
        get => accentColor;
        set { if (accentColor == value) return; accentColor = value; InvalidateVisual(); }
    }
    public bool IsMotionRunning => motion.IsEnabled;

    public GlassRim()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        motion = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(34) };
        motion.Tick += Advance;
        Unloaded += (_, _) => StopMotion();
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopMotion(); };
    }

    public void TrackPointer(Point point)
    {
        if (!MotionEnabled || MotionStrength == 0 || !IsVisible || !IsLoaded || ActualWidth < 6 || ActualHeight < 6) return;
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return;
        var normalized = new Point(Math.Clamp(point.X / ActualWidth, 0, 1), Math.Clamp(point.Y / ActualHeight, 0, 1));
        // Composition can synthesize MouseMove. A stationary cursor must never
        // keep this finite animation alive or create a render/hit-test feedback loop.
        if (Math.Abs(normalized.X - lastPosition.X) < .0005 && Math.Abs(normalized.Y - lastPosition.Y) < .0005) return;
        var now = Environment.TickCount64;
        if (motion.IsEnabled && now - lastInput < 24) return;
        var bounds = Bounds(.7);
        var radius = SafeRadius(bounds, 0);
        var length = outerContour?.Length ?? Perimeter(bounds, radius);
        var next = outerContour?.Project(point) ?? Project(bounds, radius, point) / length;
        var distance = lastInput == 0 ? 0 : Math.Sqrt(Math.Pow((normalized.X - lastPosition.X) * ActualWidth, 2) + Math.Pow((normalized.Y - lastPosition.Y) * ActualHeight, 2));
        var elapsed = Math.Max(16, now - lastInput);
        var travel = WrapDelta(next - target);
        if (Math.Abs(travel) > .0001) direction = Math.Sign(travel);
        impulse = .55 + .45 * Math.Clamp(distance / elapsed / 1.4, 0, 1);
        lastPosition = normalized;
        target = next;
        if (!motion.IsEnabled) { position = next; lastTick = now; }
        lastInput = now;
        releasedAt = 0;
        envelope = impulse;
        if (!motion.IsEnabled) motion.Start();
        InvalidateVisual();
    }

    /// <summary>Let the current reflection dissolve; leaving never starts a new animation.</summary>
    public void ReleasePointer()
    {
        if (motion.IsEnabled && releasedAt == 0) releasedAt = Environment.TickCount64;
    }

    /// <summary>Used for deactivation, reduced motion and hiding. No timer remains registered.</summary>
    public void StopMotion()
    {
        motion.Stop();
        var wasVisible = envelope > 0;
        envelope = 0;
        releasedAt = 0;
        if (wasVisible) InvalidateVisual();
    }

    private void Advance(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        if (!MotionEnabled || MotionStrength == 0 || !IsVisible || !IsLoaded || now - lastInput >= LifetimeMilliseconds || (releasedAt != 0 && now - releasedAt >= ReleaseMilliseconds))
        { StopMotion(); return; }
        var dt = Math.Clamp((now - lastTick) / 1000d, .001, .08);
        lastTick = now;
        position = Wrap(position + WrapDelta(target - position) * (1 - Math.Exp(-15 * dt)));
        phase += dt * (2.2 + impulse * 1.8);
        var age = Math.Clamp((now - lastInput) / (double)LifetimeMilliseconds, 0, 1);
        envelope = impulse * Math.Pow(1 - age, 1.45);
        if (releasedAt != 0) envelope *= Math.Pow(1 - (now - releasedAt) / (double)ReleaseMilliseconds, 1.2);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 6 || ActualHeight < 6) return;
        var bounds = Bounds(.7);
        var radius = SafeRadius(bounds, 0);
        if (DrawStaticEdge)
        {
            // Separated light and shade make a narrow meniscus instead of a bright halo.
            dc.DrawRoundedRectangle(Reflection, Outer, bounds, radius, radius);
            var inset = Bounds(2.3);
            dc.DrawRoundedRectangle(null, Inner, inset, SafeRadius(inset, 1.6), SafeRadius(inset, 1.6));
            dc.DrawRoundedRectangle(null, Lower, bounds, radius, radius);
            if (ActualWidth > 14 && ActualHeight > 14)
            {
                var deep = Bounds(4.1);
                dc.DrawRoundedRectangle(null, Deep, deep, SafeRadius(deep, 3.4), SafeRadius(deep, 3.4));
            }
        }
        var amount = Math.Clamp(envelope * MotionStrength, 0, 1);
        if (amount <= .002) return;
        var length = outerContour?.Length ?? Perimeter(bounds, radius);
        var drift = direction * Math.Sin(phase) * 18 * MotionStrength / length;
        var center = Wrap(position + drift);
        var span = Math.Clamp(Math.Min(ActualWidth, ActualHeight) * .42 + 88, 100, 275) * (1 + Math.Sin(phase * .85) * .1 * MotionStrength);
        // Both strokes follow the same rounded contour, at different depths.
        // Their short relative drift gives the edge a fluid response to movement.
        DrawRibbon(dc, bounds, radius, center, span, amount, 1.35, 0, outerContour);
        if (ActualWidth > 16 && ActualHeight > 16)
        {
            var inner = Bounds(4.5);
            DrawRibbon(dc, inner, SafeRadius(inner, 3.8), Wrap(center - direction * 12 / length), span * .76, amount * .42, .85, 1, innerContour);
        }
    }

    private void DrawRibbon(DrawingContext dc, Rect bounds, double radius, double center, double span, double amount, double width, int depth, Contour? contour)
    {
        var perimeter = contour?.Length ?? Perimeter(bounds, radius);
        span = Math.Min(span, perimeter * .42);
        var half = span / perimeter / 2;
        Point Sample(double fraction) => contour?.At(fraction) ?? At(bounds, radius, fraction * perimeter);
        var start = Sample(center - half);
        var end = Sample(center + half);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            for (var index = 1; index <= 30; index++) context.LineTo(Sample(center - half + 2 * half * index / 30), true, false);
        }
        geometry.Freeze();
        var brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = start, EndPoint = end };
        byte Alpha(double alpha) => (byte)Math.Clamp(Math.Round(alpha * amount), 0, 255);
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(Alpha(82), 255, 255, 255), .22));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(Alpha(depth == 0 ? 228 : 190), 255, 255, 255), .48));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(Alpha(148), accentColor.R, accentColor.G, accentColor.B), .72));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        brush.Freeze();
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>Arc-length lookup for an optional asymmetric outline; no flattening occurs in the motion clock.</summary>
    private sealed class Contour(Point[] points, double[] distances)
    {
        public double Length => distances[^1];
        public static Contour? Create(Geometry? geometry)
        {
            if (geometry == null) return null;
            var path = geometry.GetFlattenedPathGeometry(.08, ToleranceType.Absolute);
            if (path.Figures.Count == 0) return null;
            var figure = path.Figures[0]; var list = new List<Point> { figure.StartPoint };
            foreach (var segment in figure.Segments)
                if (segment is LineSegment line) list.Add(line.Point);
                else if (segment is PolyLineSegment polyline) list.AddRange(polyline.Points);
            if (figure.IsClosed && list[^1] != list[0]) list.Add(list[0]);
            if (list.Count < 2) return null;
            var points = list.ToArray(); var lengths = new double[points.Length];
            for (var i = 1; i < points.Length; i++) lengths[i] = lengths[i - 1] + (points[i] - points[i - 1]).Length;
            return lengths[^1] > .001 ? new(points, lengths) : null;
        }
        public Point At(double fraction)
        {
            var distance = Wrap(fraction) * Length;
            var found = Array.BinarySearch(distances, distance);
            var index = Math.Clamp(found >= 0 ? found : ~found - 1, 0, points.Length - 2);
            var span = distances[index + 1] - distances[index];
            return points[index] + (points[index + 1] - points[index]) * (span <= .00001 ? 0 : (distance - distances[index]) / span);
        }
        public double Project(Point point)
        {
            var best = double.PositiveInfinity; var position = 0d;
            for (var i = 0; i < points.Length - 1; i++)
            {
                var edge = points[i + 1] - points[i]; var square = edge.LengthSquared;
                if (square <= .00001) continue;
                var t = Math.Clamp(Vector.Multiply(point - points[i], edge) / square, 0, 1);
                var delta = point - (points[i] + edge * t);
                if (delta.LengthSquared >= best) continue;
                best = delta.LengthSquared; position = distances[i] + (distances[i + 1] - distances[i]) * t;
            }
            return position / Length;
        }
    }

    private Rect Bounds(double inset) => new(inset, inset, Math.Max(0, ActualWidth - inset * 2), Math.Max(0, ActualHeight - inset * 2));
    private double SafeRadius(Rect bounds, double inset) => Math.Min(Math.Max(0, (double.IsFinite(Radius) ? Radius : 24) - inset), Math.Min(bounds.Width, bounds.Height) / 2);
    private static double Perimeter(Rect rect, double radius) => Math.Max(1, 2 * (rect.Width + rect.Height - 4 * radius) + 2 * Math.PI * radius);
    private static double Wrap(double value) => value - Math.Floor(value);
    private static double WrapDelta(double value) => value - Math.Floor(value + .5);

    private static Point At(Rect rect, double radius, double distance)
    {
        var length = Perimeter(rect, radius);
        var d = (distance % length + length) % length;
        var horizontal = Math.Max(0, rect.Width - 2 * radius);
        var vertical = Math.Max(0, rect.Height - 2 * radius);
        var arc = Math.PI * radius / 2;
        if (d <= horizontal) return new Point(rect.Left + radius + d, rect.Top);
        d -= horizontal;
        if (radius > 0 && d <= arc) return OnArc(rect.Right - radius, rect.Top + radius, radius, -Math.PI / 2 + d / radius);
        d -= arc;
        if (d <= vertical) return new Point(rect.Right, rect.Top + radius + d);
        d -= vertical;
        if (radius > 0 && d <= arc) return OnArc(rect.Right - radius, rect.Bottom - radius, radius, d / radius);
        d -= arc;
        if (d <= horizontal) return new Point(rect.Right - radius - d, rect.Bottom);
        d -= horizontal;
        if (radius > 0 && d <= arc) return OnArc(rect.Left + radius, rect.Bottom - radius, radius, Math.PI / 2 + d / radius);
        d -= arc;
        if (d <= vertical) return new Point(rect.Left, rect.Bottom - radius - d);
        d -= vertical;
        return radius > 0 ? OnArc(rect.Left + radius, rect.Top + radius, radius, Math.PI + d / radius) : rect.TopLeft;
    }

    private static Point OnArc(double x, double y, double radius, double angle) => new(x + radius * Math.Cos(angle), y + radius * Math.Sin(angle));

    private static double Project(Rect rect, double radius, Point point)
    {
        var horizontal = Math.Max(0, rect.Width - 2 * radius);
        var vertical = Math.Max(0, rect.Height - 2 * radius);
        var arc = Math.PI * radius / 2;
        var bestDistance = double.PositiveInfinity;
        var best = 0d;
        void Candidate(double distance)
        {
            var p = At(rect, radius, distance);
            var square = (p.X - point.X) * (p.X - point.X) + (p.Y - point.Y) * (p.Y - point.Y);
            if (square < bestDistance) { bestDistance = square; best = distance; }
        }
        void Arc(double cx, double cy, double startAngle, double offset)
        {
            if (radius <= 0) return;
            var delta = (Math.Atan2(point.Y - cy, point.X - cx) - startAngle + 2 * Math.PI) % (2 * Math.PI);
            if (delta <= Math.PI / 2) Candidate(offset + delta * radius);
            else { Candidate(offset); Candidate(offset + arc); }
        }
        Candidate(Math.Clamp(point.X - rect.Left - radius, 0, horizontal));
        Arc(rect.Right - radius, rect.Top + radius, -Math.PI / 2, horizontal);
        Candidate(horizontal + arc + Math.Clamp(point.Y - rect.Top - radius, 0, vertical));
        Arc(rect.Right - radius, rect.Bottom - radius, 0, horizontal + arc + vertical);
        Candidate(horizontal + 2 * arc + vertical + Math.Clamp(rect.Right - radius - point.X, 0, horizontal));
        Arc(rect.Left + radius, rect.Bottom - radius, Math.PI / 2, 2 * horizontal + 2 * arc + vertical);
        Candidate(2 * horizontal + 3 * arc + vertical + Math.Clamp(rect.Bottom - radius - point.Y, 0, vertical));
        Arc(rect.Left + radius, rect.Top + radius, Math.PI, 2 * horizontal + 3 * arc + 2 * vertical);
        return best;
    }

    private static Pen Edge(double width, params (double Position, int Alpha)[] stops) => Edge(width, stops, false);
    private static Pen Edge(double width, (double Position, int Alpha)[] stops, bool dark)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(.1, 0), EndPoint = new Point(.85, 1) };
        foreach (var stop in stops) brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)stop.Alpha, dark ? (byte)5 : (byte)255, dark ? (byte)14 : (byte)255, dark ? (byte)25 : (byte)255), stop.Position));
        brush.Freeze(); var pen = new Pen(brush, width); pen.Freeze(); return pen;
    }
    private static Brush Sheen()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(.35, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(9, 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, .34));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(5, 10, 22, 35), 1));
        brush.Freeze(); return brush;
    }
}
