using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using LumaDock.Core;

namespace LumaDock.App.Center;

/// <summary>Small tile model: bitmap work is asynchronous and all UI updates stay on the dispatcher.</summary>
public sealed class AppTileViewModel(AppEntry entry) : INotifyPropertyChanged
{
    private BitmapSource? _icon;
    public AppEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public BitmapSource? Icon { get => _icon; private set { _icon = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public async Task LoadIconAsync(AppController controller, int physicalPixels = 80)
    {
        try { Icon = await controller.GetIconAsync(Entry, physicalPixels); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Icon unavailable: {Entry.Name}: {ex.Message}"); }
    }
}
