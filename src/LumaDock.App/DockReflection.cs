using System.Windows;
using System.Windows.Media;

namespace LumaDock.App;

/// <summary>A retained mirror of the real cached icon, clipped to a shallow glass shelf.
/// It owns no bitmap copy, live VisualBrush, render event or animation clock.</summary>
internal sealed class DockReflection : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(DockReflection),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    private bool vertical, outwardEnd;
    private double center, resting, iconWidth, iconHeight, strength, scale = 1, along, lift;
    private Rect shelf;
    private RectangleGeometry? clip;
    private ScaleTransform? mirror;
    private LinearGradientBrush? fade;

    public DockReflection()
    {
        IsHitTestVisible = false; SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    internal void Configure(string position, double cell, double cross, double restingCenter, double commonIconSize, double width, double height, double amount)
    {
        vertical = position is "Left" or "Right"; outwardEnd = position is "Bottom" or "Right";
        Width = vertical ? cross : cell; Height = vertical ? cell : cross;
        center = cell / 2; resting = restingCenter; iconWidth = width; iconHeight = height;
        strength = Math.Clamp(amount, 0, .5);
        var plane = resting + (outwardEnd ? 1 : -1) * (commonIconSize / 2 + 1.5);
        var depth = Math.Clamp(commonIconSize * .34, 11, 24);
        var start = outwardEnd ? plane : plane - depth;
        // Adjacent enlarged icons may overlap along the shelf; do not cut mirrors at cell borders.
        shelf = vertical ? new Rect(start, -commonIconSize, depth, cell + commonIconSize * 2)
                         : new Rect(-commonIconSize, start, cell + commonIconSize * 2, depth);
        clip = new RectangleGeometry(shelf); clip.Freeze();
        mirror = vertical ? new ScaleTransform(-1, 1, plane, 0) : new ScaleTransform(1, -1, 0, plane); mirror.Freeze();
        var near = vertical ? new Point(plane, 0) : new Point(0, plane);
        var far = vertical ? new Point(plane + (outwardEnd ? depth : -depth), 0) : new Point(0, plane + (outwardEnd ? depth : -depth));
        fade = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = near, EndPoint = far };
        fade.GradientStops.Add(new GradientStop(Colors.White, 0));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(116, 255, 255, 255), .48));
        fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1)); fade.Freeze();
        InvalidateVisual();
    }

    internal void SetPose(double iconScale, double alongOffset, double crossOffset)
    {
        if (scale == iconScale && along == alongOffset && lift == crossOffset) return;
        scale = iconScale; along = alongOffset; lift = crossOffset; InvalidateVisual();
    }

    internal Rect ShelfBounds => shelf;
    internal Rect MirroredImageBounds => mirror?.TransformBounds(ImageBounds) ?? Rect.Empty;
    private Rect ImageBounds
    {
        get
        {
            var x = vertical ? resting + lift : center + along;
            var y = vertical ? center + along : resting + lift;
            // Match Image.Stretch=Uniform rather than distorting a non-square custom source.
            var width = iconWidth; var height = iconHeight;
            if (Source is { Width: > 0, Height: > 0 } image)
            {
                var fit = Math.Min(width / image.Width, height / image.Height);
                width = image.Width * fit; height = image.Height * fit;
            }
            return new Rect(x - width * scale / 2, y - height * scale / 2, width * scale, height * scale);
        }
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (Source == null || strength <= 0 || clip == null || mirror == null || fade == null) return;
        var bounds = ImageBounds;
        if (bounds.IsEmpty || !bounds.IntersectsWith(mirror.TransformBounds(shelf))) return;
        drawing.PushClip(clip);
        drawing.PushOpacity(strength / (1 + Math.Abs(lift) * .025));
        drawing.PushOpacityMask(fade);
        drawing.PushTransform(mirror);
        drawing.DrawImage(Source, bounds);
        drawing.Pop(); drawing.Pop(); drawing.Pop(); drawing.Pop();
    }
}
