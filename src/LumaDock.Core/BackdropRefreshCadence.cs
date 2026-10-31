namespace LumaDock.Core;

/// <summary>Backs off unchanged backdrop samples without reducing optical output quality.</summary>
public sealed class BackdropRefreshCadence
{
    private int unchanged;
    public int DelayMilliseconds => unchanged switch { < 3 => 120, < 6 => 250, < 9 => 500, _ => 1000 };
    public void Observe(bool changed) => unchanged = changed ? 0 : Math.Min(9, unchanged + 1);
    public void Reset() => unchanged = 0;
}
