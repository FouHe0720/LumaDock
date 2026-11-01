namespace LumaDock.Core;

/// <summary>A gap is measured in the original list: 0 precedes the first pin, Count follows the last.</summary>
public static class DockOrder
{
    public static void InsertAtGap(List<string> pins, IEnumerable<string> incoming, int gap)
    {
        var ids = incoming.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) return;
        gap = Math.Clamp(gap, 0, pins.Count);
        var selected = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var insert = pins.Take(gap).Count(id => !selected.Contains(id));
        pins.RemoveAll(selected.Contains);
        pins.InsertRange(insert, ids);
    }
}
