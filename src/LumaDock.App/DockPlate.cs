using System.Windows;
using System.Windows.Media;

namespace LumaDock.App;

/// <summary>Small retained vector surface. No blur effect, image allocation or frame loop.</summary>
internal sealed class DockPlate : FrameworkElement
{
    public string Shape { get; set; } = "Capsule";
    public double Radius { get; set; } = 32;
    public bool FinishOnly { get; set; }
    public Brush Fill { get; set; } = Brushes.Transparent;
    public Color Accent { get; set; }
    public bool Dark { get; set; }
    public bool Vertical { get; set; }
    public bool OutwardEnd { get; set; } = true;

    public DockPlate() { IsHitTestVisible = false; }

    internal static Geometry Contour(Size size, string shape, double radius, double inset = 0)
    {
        var w = Math.Max(0, size.Width - inset * 2); var h = Math.Max(0, size.Height - inset * 2);
        var r = Math.Clamp(radius - inset, 0, Math.Min(w, h) / 2);
        var tl = r; var tr = r; var br = r; var bl = r;
        if (shape == "Pebble") { tl *= .68; br *= .68; }
        var x = inset; var y = inset; var right = x + w; var bottom = y + h;
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(new Point(x + tl, y), true, true);
            c.LineTo(new Point(right - tr, y), true, false); Arc(c, right, y + tr, tr);
            c.LineTo(new Point(right, bottom - br), true, false); Arc(c, right - br, bottom, br);
            c.LineTo(new Point(x + bl, bottom), true, false); Arc(c, x, bottom - bl, bl);
            c.LineTo(new Point(x, y + tl), true, false); Arc(c, x + tl, y, tl);
        }
        geometry.Freeze(); return geometry;
    }
    private static void Arc(StreamGeometryContext c, double x, double y, double r)
    {
        if (r <= 0) c.LineTo(new Point(x, y), true, false);
        else c.ArcTo(new Point(x, y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        var contour = Contour(RenderSize, Shape, Radius, .7);
        if (!FinishOnly)
        {
            // Fixed, narrow vector shadow stays cheap even during magnification.
            foreach (var (width, alpha) in new[] { (10d, 3), (6d, 5), (3d, 8) })
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)alpha, 0, 0, 0)), width), contour);
            dc.DrawGeometry(Fill, null, contour);
            return;
        }
        var edge = new LinearGradientBrush { StartPoint = new Point(.12, 0), EndPoint = new Point(.8, 1) };
        edge.GradientStops.Add(new GradientStop(Color.FromArgb(Dark ? (byte)132 : (byte)210, 255, 255, 255), 0));
        edge.GradientStops.Add(new GradientStop(Color.FromArgb(35, 255, 255, 255), .4));
        edge.GradientStops.Add(new GradientStop(Color.FromArgb(12, 255, 255, 255), .64));
        edge.GradientStops.Add(new GradientStop(Color.FromArgb(92, 255, 255, 255), 1)); edge.Freeze();
        dc.DrawGeometry(null, new Pen(edge, .85), contour);
        var inner = Contour(RenderSize, Shape, Radius, 2);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(Dark ? (byte)18 : (byte)50, 255, 255, 255)), .65), inner);
        var tint = new LinearGradientBrush
        {
            StartPoint = Vertical ? new Point(OutwardEnd ? 0 : 1, 0) : new Point(0, OutwardEnd ? 0 : 1),
            EndPoint = Vertical ? new Point(OutwardEnd ? 1 : 0, 0) : new Point(0, OutwardEnd ? 1 : 0)
        };
        tint.GradientStops.Add(new GradientStop(Color.FromArgb(7, 255, 255, 255), 0));
        tint.GradientStops.Add(new GradientStop(Colors.Transparent, .48));
        // A shallow, clear shelf catches the actual icon mirrors without a blur pass.
        tint.GradientStops.Add(new GradientStop(Color.FromArgb(Dark ? (byte)7 : (byte)15, 255, 255, 255), .76));
        tint.GradientStops.Add(new GradientStop(Color.FromArgb(Dark ? (byte)18 : (byte)28, Accent.R, Accent.G, Accent.B), 1)); tint.Freeze();
        dc.DrawGeometry(tint, null, contour);
        // The slim signature inlay gives the rail a distinct, architectural silhouette.
        var length = Shape == "Rail" ? Math.Min(74, (Vertical ? ActualHeight : ActualWidth) * .28) : 24;
        var accent = new SolidColorBrush(Color.FromArgb(Shape == "Rail" ? (byte)165 : (byte)82, Accent.R, Accent.G, Accent.B)); accent.Freeze();
        var a = Vertical ? new Point(ActualWidth - 4, (ActualHeight - length) / 2) : new Point((ActualWidth - length) / 2, ActualHeight - 4);
        var b = Vertical ? new Point(a.X, a.Y + length) : new Point(a.X + length, a.Y);
        dc.DrawLine(new Pen(accent, Shape == "Rail" ? 2 : 1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, a, b);
    }
}
