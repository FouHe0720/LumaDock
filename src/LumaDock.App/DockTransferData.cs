using System.Windows;

namespace LumaDock.App;

/// <summary>Starts dragging immediately; Shell file data is rendered only if a consumer requests it.</summary>
public sealed class DockTransferData : IDataObject
{
    private readonly DataObject data = new();
    private readonly Lazy<string[]> files;

    public DockTransferData(string appId, Func<string[]> createFiles, Action<Exception> failed)
    {
        data.SetData("LumaDock.Pin", appId);
        data.SetData("LumaDock.AppEntry.Id", appId);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
        files = new(() =>
        {
            try { return createFiles(); }
            catch (Exception error) { failed(error); return []; }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public object? GetData(string format, bool autoConvert) => format == DataFormats.FileDrop ? files.Value : data.GetData(format, autoConvert);
    public object? GetData(string format) => GetData(format, true);
    public object? GetData(Type format) => GetData(format.FullName!);
    public bool GetDataPresent(string format, bool autoConvert) => format == DataFormats.FileDrop ? !files.IsValueCreated || files.Value.Length > 0 : data.GetDataPresent(format, autoConvert);
    public bool GetDataPresent(string format) => GetDataPresent(format, true);
    public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!);
    public string[] GetFormats(bool autoConvert) => data.GetFormats(autoConvert).Concat(GetDataPresent(DataFormats.FileDrop) ? [DataFormats.FileDrop] : Array.Empty<string>()).Distinct().ToArray();
    public string[] GetFormats() => GetFormats(true);
    public void SetData(string format, object value, bool autoConvert) => data.SetData(format, value, autoConvert);
    public void SetData(string format, object value) => data.SetData(format, value);
    public void SetData(Type format, object value) => data.SetData(format, value);
    public void SetData(object value) => data.SetData(value);
}
