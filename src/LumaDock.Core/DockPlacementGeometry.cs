namespace LumaDock.Core;

/// <summary>Physical-pixel placement. Anchors describe the remaining travel inside a monitor work area.</summary>
public readonly record struct DockWorkArea(int Left, int Top, int Width, int Height);
public readonly record struct DockFrame(int X, int Y, int Width, int Height);
public readonly record struct DockAnchor(double X, double Y);

public static class DockPlacementGeometry
{
    public static DockFrame Resolve(DockWorkArea work, int width, int height, double x, double y, int inset = 0)
    {
        var frame = Clamp(work, new(work.Left, work.Top, width, height), inset);
        var (left, top, travelX, travelY) = Range(work, frame, inset);
        return frame with { X = left + (int)Math.Round(Normalize(x, .5) * travelX), Y = top + (int)Math.Round(Normalize(y, 1) * travelY) };
    }

    public static DockFrame Clamp(DockWorkArea work, DockFrame frame, int inset = 0)
    {
        var padding = Padding(work, inset);
        var width = Math.Clamp(frame.Width, 1, Math.Max(1, work.Width - padding * 2));
        var height = Math.Clamp(frame.Height, 1, Math.Max(1, work.Height - padding * 2));
        var left = work.Left + padding; var top = work.Top + padding;
        return new(Math.Clamp(frame.X, left, left + Math.Max(0, work.Width - padding * 2 - width)),
            Math.Clamp(frame.Y, top, top + Math.Max(0, work.Height - padding * 2 - height)), width, height);
    }

    public static DockAnchor AnchorFor(DockWorkArea work, DockFrame frame, int inset = 0)
    {
        frame = Clamp(work, frame, inset);
        var (left, top, travelX, travelY) = Range(work, frame, inset);
        return new(travelX > 0 ? (frame.X - left) / (double)travelX : .5,
            travelY > 0 ? (frame.Y - top) / (double)travelY : .5);
    }

    private static (int Left, int Top, int TravelX, int TravelY) Range(DockWorkArea work, DockFrame frame, int inset)
    {
        var padding = Padding(work, inset);
        return (work.Left + padding, work.Top + padding, Math.Max(0, work.Width - padding * 2 - frame.Width), Math.Max(0, work.Height - padding * 2 - frame.Height));
    }
    private static int Padding(DockWorkArea work, int inset) => Math.Clamp(inset, 0, Math.Max(0, (Math.Min(work.Width, work.Height) - 1) / 2));
    private static double Normalize(double value, double fallback) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;
}
