using System.Buffers.Binary;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace LumaDock.Core;

/// <summary>A decoded, straight-alpha BGRA wallpaper image. The renderer never retains its buffer.</summary>
public sealed record LensSource(int Width, int Height, byte[] Bgra);

/// <summary>The glass surface bounds in physical virtual-desktop pixels.</summary>
public sealed record LensViewport(double X, double Y, double Width, double Height);

/// <summary>Where the entire source image is displayed on the desktop; Tile repeats that rectangle.</summary>
public sealed record WallpaperPlacement(double Left, double Top, double Width, double Height, bool Tile, byte R, byte G, byte B);

/// <summary>
/// An event-rendered wallpaper lens. The curved perimeter refracts actual source
/// pixels; the centre keeps the source orientation. No screen capture or frame loop.
/// </summary>
public static class LiquidLensRenderer
{
    public const int MaximumOutputPixels = 2_097_152;

    /// <returns>A premultiplied BGRA buffer suitable for WPF PixelFormats.Pbgra32.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static byte[] Render(LensSource source, WallpaperPlacement placement, LensViewport viewport,
        int outputWidth, int outputHeight, double radiusPixels, bool dark, double clarity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(source.Bgra);
        if (source.Width <= 0 || source.Height <= 0 || (long)source.Width * source.Height * 4 > source.Bgra.Length)
            throw new ArgumentException("The wallpaper dimensions exceed its BGRA buffer.", nameof(source));
        if (outputWidth <= 0 || outputHeight <= 0 || (long)outputWidth * outputHeight > MaximumOutputPixels)
            throw new ArgumentOutOfRangeException(nameof(outputWidth), "The lens output must contain between one and 2,097,152 pixels.");
        if (!ValidRect(placement.Left, placement.Top, placement.Width, placement.Height))
            throw new ArgumentException("Wallpaper placement must be a finite, positive rectangle.", nameof(placement));
        if (!ValidRect(viewport.X, viewport.Y, viewport.Width, viewport.Height))
            throw new ArgumentException("The lens viewport must be a finite, positive rectangle.", nameof(viewport));
        if (!double.IsFinite(radiusPixels) || radiusPixels < 0)
            throw new ArgumentOutOfRangeException(nameof(radiusPixels));
        if (!double.IsFinite(clarity) || clarity < 0 || clarity > 1)
            throw new ArgumentOutOfRangeException(nameof(clarity));
        cancellationToken.ThrowIfCancellationRequested();

        var output = new byte[outputWidth * outputHeight * 4];
        var stepX = viewport.Width / outputWidth;
        var stepY = viewport.Height / outputHeight;
        var footprint = Math.Max(stepX, stepY);
        var halfWidth = viewport.Width * .5;
        var halfHeight = viewport.Height * .5;
        var radius = Math.Min(radiusPixels, Math.Min(halfWidth, halfHeight));
        var edgeBand = Math.Clamp(radius * .85, 18, 28);
        var maxDeflection = 12 * (.7 + .3 * clarity);
        var blurRadius = clarity < .75 ? (1 - clarity) * 2.4 : 0;
        var sampler = new Sampler(source, placement);
        var bodyImageAmount = dark ? .28 + .06 * clarity : .28 + .08 * clarity;
        var bodyBaseAmount = dark ? 4 : 255 * (1 - bodyImageAmount);
        Span<byte> bodyTone = stackalloc byte[256];
        for (var channel = 0; channel < bodyTone.Length; channel++)
            bodyTone[channel] = Channel(channel * bodyImageAmount + bodyBaseAmount);
        // Only a small coordinate table is needed for normal window widths. Very
        // wide, one-row outputs fall back to direct calculation to bound scratch memory.
        var interiorBlurRadius = InteriorBlurRadius(outputWidth, outputHeight, footprint, clarity);
        var flatCentreSampling = blurRadius == 0 || interiorBlurRadius > 0;
        var columns = flatCentreSampling && outputWidth <= 8192 ? new SampleAxis[outputWidth] : null;
        if (columns != null)
            for (var x = 0; x < outputWidth; x++)
            {
                if ((x & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                columns[x] = sampler.ColumnAt(viewport.X + (x + .5) * stepX);
            }
        var bodyInset = Math.Max(edgeBand, footprint * .5);

        for (var y = 0; y < outputHeight; y++)
        {
            if ((y & 7) == 0) cancellationToken.ThrowIfCancellationRequested();
            var localY = (y + .5) * stepY;
            var centredY = localY - halfHeight;
            var bodyRow = localY >= bodyInset && localY <= viewport.Height - bodyInset;
            var outsideCornerRows = localY >= radius && localY <= viewport.Height - radius;
            var sourceRow = sampler.RowAt(viewport.Y + localY);
            for (var x = 0; x < outputWidth; x++)
            {
                var localX = (x + .5) * stepX;
                var offset = (y * outputWidth + x) * 4;
                // Most of a window is a flat, fully covered centre. It does not
                // need a distance field, normals, shading, or per-pixel tone maths.
                if (flatCentreSampling && bodyRow && localX >= bodyInset && localX <= viewport.Width - bodyInset
                    && (outsideCornerRows || localX >= radius && localX <= viewport.Width - radius))
                {
                    var packed = sampler.Sample(columns != null ? columns[x] : sampler.ColumnAt(viewport.X + localX), sourceRow);
                    output[offset] = bodyTone[(byte)packed];
                    output[offset + 1] = bodyTone[(byte)(packed >> 8)];
                    output[offset + 2] = bodyTone[(byte)(packed >> 16)];
                    output[offset + 3] = 255;
                    continue;
                }
                var centredX = localX - halfWidth;
                var qx = Math.Abs(centredX) - (halfWidth - radius);
                var qy = Math.Abs(centredY) - (halfHeight - radius);
                var outsideX = Math.Max(qx, 0);
                var outsideY = Math.Max(qy, 0);
                var cornerLength = Math.Sqrt(outsideX * outsideX + outsideY * outsideY);
                var signedDistance = cornerLength + Math.Min(Math.Max(qx, qy), 0) - radius;
                var coverage = Math.Clamp(.5 - signedDistance / footprint, 0, 1);
                if (coverage <= 0) continue;

                var depth = Math.Max(0, -signedDistance);
                var normalX = 0d; var normalY = 0d;
                var profile = 0d;
                if (depth < edgeBand)
                {
                    if (cornerLength > .000001)
                    {
                        normalX = outsideX / cornerLength * Math.Sign(centredX);
                        normalY = outsideY / cornerLength * Math.Sign(centredY);
                    }
                    else if (qx > qy) normalX = Math.Sign(centredX);
                    else normalY = Math.Sign(centredY);
                    var t = 1 - depth / edgeBand;
                    profile = t * t * (3 - 2 * t);
                }

                // Perimeter rays bend along the analytic rounded-rectangle normal.
                // The varying offset changes image scale near the edge, rather than
                // drawing a decorative gradient in place of actual refraction.
                var sampleX = viewport.X + localX + normalX * maxDeflection * profile;
                var sampleY = viewport.Y + localY + normalY * maxDeflection * profile;
                var colour = blurRadius == 0 ? sampler.At(sampleX, sampleY)
                    : (sampler.At(sampleX - blurRadius, sampleY) + sampler.At(sampleX + blurRadius, sampleY)
                        + sampler.At(sampleX, sampleY - blurRadius) + sampler.At(sampleX, sampleY + blurRadius)) * .25;

                var imageAmount = bodyImageAmount;
                // A clearer narrow rim makes the bent image visible while the body
                // remains dark/light enough for text to keep its contrast.
                imageAmount += profile * .20;
                var baseAmount = dark ? 4 : 255 * (1 - imageAmount);
                var lighting = Math.Clamp(.5 + (-normalX * .55 - normalY * .83) * .5, 0, 1);
                var ridge = Math.Max(0, 1 - Math.Abs(depth - 1.1) / 1.6);
                var glint = ridge * ridge * (dark ? 24 : 13) * (.3 + .7 * lighting);
                var innerShade = Math.Max(0, 1 - Math.Abs(depth - 4) / 3) * (1 - lighting) * 5;
                var finishing = glint + profile * lighting * 2 - innerShade;
                output[offset] = Premultiply(colour.B * imageAmount + baseAmount + finishing, coverage);
                output[offset + 1] = Premultiply(colour.G * imageAmount + baseAmount + finishing, coverage);
                output[offset + 2] = Premultiply(colour.R * imageAmount + baseAmount + finishing, coverage);
                output[offset + 3] = Channel(255 * coverage);
            }
        }
        BlurInterior(output, outputWidth, outputHeight, stepX, stepY, radius, edgeBand, interiorBlurRadius, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }

    private static int InteriorBlurRadius(int width, int height, double footprint, double clarity) =>
        clarity >= .9 || width < 128 || height < 80 ? 0
            : Math.Clamp((int)Math.Round((1 - clarity) * 24 / footprint), 0, 10);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BlurInterior(byte[] output, int width, int height, double stepX, double stepY,
        double cornerRadius, double edgeBand, int radius, CancellationToken cancellation)
    {
        if (radius == 0) return;
        byte[]? horizontal = null; byte[]? vertical = null;
        try
        {
            horizontal = ArrayPool<byte>.Shared.Rent(output.Length);
            vertical = ArrayPool<byte>.Shared.Rent(output.Length);
            var source = output;
            for (var pass = 0; pass < 2; pass++)
            {
                BoxHorizontal(source, horizontal, width, height, radius, cancellation);
                BoxVertical(horizontal, vertical, width, height, radius, cancellation);
                source = vertical;
            }

            // Blur the legibility region only. Keep the refracting perimeter,
            // fractional corner coverage and original alpha completely intact.
            var inset = edgeBand + 4;
            var innerCornerRadius = Math.Max(0, cornerRadius - inset);
            var physicalHeight = height * stepY;
            var physicalWidth = width * stepX;
            for (var y = 0; y < height; y++)
            {
                if ((y & 7) == 0) cancellation.ThrowIfCancellationRequested();
                var py = (y + .5) * stepY;
                if (py <= inset || py >= physicalHeight - inset) continue;
                for (var x = 0; x < width; x++)
                {
                    var px = (x + .5) * stepX;
                    if (px <= inset || px >= physicalWidth - inset) continue;
                    var depth = Math.Min(Math.Min(px, physicalWidth - px), Math.Min(py, physicalHeight - py));
                    if (innerCornerRadius > 0)
                    {
                        var qx = Math.Max(0, Math.Max(cornerRadius - px, px - (physicalWidth - cornerRadius)));
                        var qy = Math.Max(0, Math.Max(cornerRadius - py, py - (physicalHeight - cornerRadius)));
                        if (qx * qx + qy * qy >= innerCornerRadius * innerCornerRadius) continue;
                        if (qx > 0 && qy > 0) depth = Math.Min(depth, cornerRadius - Math.Sqrt(qx * qx + qy * qy));
                    }
                    var offset = (y * width + x) * 4;
                    if (output[offset + 3] != 255) continue;
                    var blend = Math.Clamp((int)((depth - inset) * 32), 0, 256);
                    if (blend == 256)
                    {
                        output[offset] = vertical[offset];
                        output[offset + 1] = vertical[offset + 1];
                        output[offset + 2] = vertical[offset + 2];
                    }
                    else
                    {
                        var inverse = 256 - blend;
                        output[offset] = (byte)((output[offset] * inverse + vertical[offset] * blend + 128) >> 8);
                        output[offset + 1] = (byte)((output[offset + 1] * inverse + vertical[offset + 1] * blend + 128) >> 8);
                        output[offset + 2] = (byte)((output[offset + 2] * inverse + vertical[offset + 2] * blend + 128) >> 8);
                    }
                }
            }
        }
        finally
        {
            if (horizontal != null) ArrayPool<byte>.Shared.Return(horizontal);
            if (vertical != null) ArrayPool<byte>.Shared.Return(vertical);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BoxHorizontal(byte[] source, byte[] destination, int width, int height, int radius, CancellationToken cancellation)
    {
        var reciprocal = (int)Math.Round(65536d / (radius * 2 + 1));
        for (var y = 0; y < height; y++)
        {
            if ((y & 7) == 0) cancellation.ThrowIfCancellationRequested();
            var row = y * width * 4;
            var b = 0; var g = 0; var r = 0;
            for (var initial = -radius; initial <= radius; initial++)
            {
                var index = row + Math.Clamp(initial, 0, width - 1) * 4;
                b += source[index]; g += source[index + 1]; r += source[index + 2];
            }
            for (var x = 0; x < width; x++)
            {
                var target = row + x * 4;
                destination[target] = (byte)((b * reciprocal + 32768) >> 16);
                destination[target + 1] = (byte)((g * reciprocal + 32768) >> 16);
                destination[target + 2] = (byte)((r * reciprocal + 32768) >> 16);
                var remove = row + Math.Max(0, x - radius) * 4;
                var add = row + Math.Min(width - 1, x + radius + 1) * 4;
                b += source[add] - source[remove];
                g += source[add + 1] - source[remove + 1];
                r += source[add + 2] - source[remove + 2];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BoxVertical(byte[] source, byte[] destination, int width, int height, int radius, CancellationToken cancellation)
    {
        var reciprocal = (int)Math.Round(65536d / (radius * 2 + 1));
        var stride = width * 4;
        for (var x = 0; x < width; x++)
        {
            if ((x & 15) == 0) cancellation.ThrowIfCancellationRequested();
            var column = x * 4;
            var b = 0; var g = 0; var r = 0;
            for (var initial = -radius; initial <= radius; initial++)
            {
                var index = Math.Clamp(initial, 0, height - 1) * stride + column;
                b += source[index]; g += source[index + 1]; r += source[index + 2];
            }
            for (var y = 0; y < height; y++)
            {
                var target = y * stride + column;
                destination[target] = (byte)((b * reciprocal + 32768) >> 16);
                destination[target + 1] = (byte)((g * reciprocal + 32768) >> 16);
                destination[target + 2] = (byte)((r * reciprocal + 32768) >> 16);
                var remove = Math.Max(0, y - radius) * stride + column;
                var add = Math.Min(height - 1, y + radius + 1) * stride + column;
                b += source[add] - source[remove];
                g += source[add + 1] - source[remove + 1];
                r += source[add + 2] - source[remove + 2];
            }
        }
    }

    private static bool ValidRect(double x, double y, double width, double height) =>
        double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(width) && double.IsFinite(height)
        && width > 0 && height > 0 && double.IsFinite(x + width) && double.IsFinite(y + height);
    private static byte Channel(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    private static byte Premultiply(double value, double coverage) => Channel(Math.Clamp(value, 0, 255) * coverage);

    private readonly record struct Colour(double B, double G, double R)
    {
        public static Colour operator +(Colour left, Colour right) => new(left.B + right.B, left.G + right.G, left.R + right.R);
        public static Colour operator *(Colour value, double amount) => new(value.B * amount, value.G * amount, value.R * amount);
    }

    private readonly record struct SampleAxis(int First, int Second, int Weight, bool Valid);

    private readonly struct Sampler
    {
        private readonly byte[] pixels;
        private readonly int width, height, stride;
        private readonly double left, top, right, bottom, scaleX, scaleY;
        private readonly bool tile;
        private readonly uint background;

        public Sampler(LensSource source, WallpaperPlacement placement)
        {
            pixels = source.Bgra; width = source.Width; height = source.Height; stride = width * 4;
            left = placement.Left; top = placement.Top; right = left + placement.Width; bottom = top + placement.Height;
            scaleX = width / placement.Width; scaleY = height / placement.Height; tile = placement.Tile;
            background = (uint)(placement.B | placement.G << 8 | placement.R << 16);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Colour At(double screenX, double screenY)
        {
            var packed = Sample(ColumnAt(screenX), RowAt(screenY));
            return new((byte)packed, (byte)(packed >> 8), (byte)(packed >> 16));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SampleAxis ColumnAt(double screenX) => Axis(screenX, left, right, scaleX, width, 4);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SampleAxis RowAt(double screenY) => Axis(screenY, top, bottom, scaleY, height, stride);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private SampleAxis Axis(double coordinate, double start, double end, double scale, int length, int byteStep)
        {
            if (!tile && (coordinate < start || coordinate >= end)) return default;
            var position = (coordinate - start) * scale - .5;
            if (tile) position -= Math.Floor(position / length) * length;
            else position = Math.Clamp(position, 0, length - 1);
            var first = (int)Math.Floor(position);
            var second = first + 1 < length ? first + 1 : tile ? 0 : first;
            return new(first * byteStep, second * byteStep, (int)((position - first) * 256 + .5), true);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint Sample(SampleAxis column, SampleAxis row)
        {
            if (!column.Valid || !row.Valid) return background;
            var p00 = Read(row.First + column.First); var p10 = Read(row.First + column.Second);
            var p01 = Read(row.Second + column.First); var p11 = Read(row.Second + column.Second);
            if ((p00 & p10 & p01 & p11 & 0xFF000000) != 0xFF000000)
            {
                p00 = Flatten(p00); p10 = Flatten(p10); p01 = Flatten(p01); p11 = Flatten(p11);
            }
            var wx = column.Weight; var wy = row.Weight;
            var b = Mix((byte)p00, (byte)p10, (byte)p01, (byte)p11, wx, wy);
            var g = Mix((byte)(p00 >> 8), (byte)(p10 >> 8), (byte)(p01 >> 8), (byte)(p11 >> 8), wx, wy);
            var r = Mix((byte)(p00 >> 16), (byte)(p10 >> 16), (byte)(p01 >> 16), (byte)(p11 >> 16), wx, wy);
            return (uint)(b | g << 8 | r << 16);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint Read(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan(offset, 4));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Mix(int a, int b, int c, int d, int x, int y) =>
            ((a * (256 - x) + b * x) * (256 - y) + (c * (256 - x) + d * x) * y + 32768) >> 16;

        private uint Flatten(uint value)
        {
            var alpha = (int)(value >> 24);
            if (alpha == 255) return value;
            if (alpha == 0) return background;
            var inverse = 255 - alpha;
            var b = ((byte)value * alpha + (byte)background * inverse + 127) / 255;
            var g = ((byte)(value >> 8) * alpha + (byte)(background >> 8) * inverse + 127) / 255;
            var r = ((byte)(value >> 16) * alpha + (byte)(background >> 16) * inverse + 127) / 255;
            return (uint)(b | g << 8 | r << 16);
        }
    }
}
