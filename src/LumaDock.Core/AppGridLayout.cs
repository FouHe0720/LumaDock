namespace LumaDock.Core;

/// <summary>Application-tile geometry in device-independent pixels.</summary>
/// <param name="TileHeight">Tile content and padding, excluding its 10 DIP outer margin.</param>
public sealed record AppGridMetrics(int Columns, int Rows, double TileHeight, double NameHeight, double LineHeight);

/// <summary>Preserves two readable name lines as the user's desktop font grows.</summary>
public static class AppGridLayout
{
    public static AppGridMetrics Calculate(double width, double height, int requestedColumns,
        double fontSize, double fontLineSpacing = 1.3)
    {
        // Layout can run before WPF has measured a newly shown window. Invalid or
        // empty viewports still produce one scrollable tile rather than zero cells.
        width = double.IsFinite(width) ? Math.Max(0, width) : 0;
        height = double.IsFinite(height) ? Math.Max(0, height) : 0;
        fontSize = double.IsFinite(fontSize) && fontSize > 0 ? Math.Clamp(fontSize, 6, 96) : 14;
        fontLineSpacing = double.IsFinite(fontLineSpacing) && fontLineSpacing > 0
            ? Math.Clamp(fontLineSpacing, 1, 3) : 1.3;

        var lineHeight = Math.Ceiling(Math.Max(fontSize * fontLineSpacing, fontSize * 1.2));
        var nameHeight = lineHeight * 2 + 2;
        var tileHeight = 48 + 10 + nameHeight + 8 + 18;
        var minCellWidth = Math.Max(112, fontSize * 6.8);
        var columns = (int)Math.Clamp(Math.Floor(width / minCellWidth), 1, Math.Clamp(requestedColumns, 1, 10));
        var rows = (int)Math.Clamp(Math.Floor(height / (tileHeight + 10)), 1, 4);
        return new(columns, rows, tileHeight, nameHeight, lineHeight);
    }
}
