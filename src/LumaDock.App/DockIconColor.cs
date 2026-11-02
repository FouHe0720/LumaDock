using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LumaDock.App;

/// <summary>One-time color adjustment of cached real icon pixels. No shader, frame hook or file writes.</summary>
internal static class DockIconColor
{
    private sealed class LastAdjustment
    {
        internal int Amount = -1;
        internal ImageSource? Image;
    }

    // A source only retains its latest adjustment; evicting the original icon releases both.
    private static readonly ConditionalWeakTable<ImageSource, LastAdjustment> Adjustments = new();

    internal static ImageSource Enhance(ImageSource source, double amount)
    {
        ArgumentNullException.ThrowIfNull(source);
        var level = double.IsFinite(amount) ? (int)Math.Round(Math.Clamp(amount, 0, 1) * 1000) : 0;
        // Mutable images cannot be cached safely or read from the loading worker.
        if (level == 0 || source is not BitmapSource bitmap || !bitmap.IsFrozen) return source;
        try
        {
            if (bitmap.PixelWidth is < 1 or > 512 || bitmap.PixelHeight is < 1 or > 512) return source;
            var last = Adjustments.GetValue(source, static _ => new LastAdjustment());
            lock (last)
            {
                if (last.Amount == level && last.Image != null) return last.Image;
                var result = Adjust(bitmap, level / 1000d);
                last.Amount = level; last.Image = result;
                return result;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or IOException or COMException or OverflowException)
        {
            // Unsupported/failed image decoding keeps the already usable system icon.
            return source;
        }
    }

    private static ImageSource Adjust(BitmapSource original, double amount)
    {
        // Work on straight channels. Adjusting premultiplied channels would darken translucent edges.
        BitmapSource straight = original.Format == PixelFormats.Bgra32
            ? original : new FormatConvertedBitmap(original, PixelFormats.Bgra32, null, 0);
        var stride = checked(original.PixelWidth * 4);
        var pixels = new byte[checked(stride * original.PixelHeight)];
        straight.CopyPixels(pixels, stride, 0);
        var saturation = 1 + amount * .50;
        var contrast = 1 + amount * .04;
        var brightness = 1 + amount * .06;
        var changed = false;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0) continue;
            var b = pixels[i]; var g = pixels[i + 1]; var r = pixels[i + 2];
            var luma = r * .2126 + g * .7152 + b * .0722;
            var adjustedB = Channel(b); var adjustedG = Channel(g); var adjustedR = Channel(r);
            changed |= adjustedB != b || adjustedG != g || adjustedR != r;
            pixels[i] = adjustedB; pixels[i + 1] = adjustedG; pixels[i + 2] = adjustedR;
            // The original alpha channel, including soft antialiased edges, remains byte-identical.
            byte Channel(byte channel) => (byte)Math.Clamp(Math.Round(((luma + (channel - luma) * saturation - 127.5) * contrast + 127.5) * brightness), 0, 255);
        }
        if (!changed) return original;
        var dpiX = double.IsFinite(original.DpiX) && original.DpiX > 0 ? original.DpiX : 96;
        var dpiY = double.IsFinite(original.DpiY) && original.DpiY > 0 ? original.DpiY : 96;
        var image = BitmapSource.Create(original.PixelWidth, original.PixelHeight, dpiX, dpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze(); return image;
    }
}
