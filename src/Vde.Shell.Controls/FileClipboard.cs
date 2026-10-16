using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Vde.Shell;

namespace Vde.Shell.Controls;

// The clipboard as the file manager and the desktop use it: files cut or copied, in the formats other desktops use.
public interface IFileClipboard
{
    Task SetAsync(FileTransfer? transfer);   // null empties it
    Task<FileTransfer?> GetAsync();
}

// Kept in memory: for tests, and as the fallback when a window has no system clipboard.
public sealed class InMemoryFileClipboard : IFileClipboard
{
    public FileTransfer? Current { get; private set; }
    public Task SetAsync(FileTransfer? transfer) { Current = transfer; return Task.CompletedTask; }
    public Task<FileTransfer?> GetAsync() => Task.FromResult(Current);
}

public sealed class AvaloniaFileClipboard(Func<TopLevel?> topLevel) : IFileClipboard
{
    public async Task SetAsync(FileTransfer? transfer)
    {
        if (topLevel()?.Clipboard is not { } clipboard)
            return;
        if (transfer is null)
        {
            await clipboard.ClearAsync();
            return;
        }
        var data = new DataObject();
        foreach (var (format, bytes) in transfer.ToFormats())
            data.Set(format, bytes);
        await clipboard.SetDataObjectAsync(data);
    }

    public async Task<FileTransfer?> GetAsync()
    {
        if (topLevel()?.Clipboard is not { } clipboard)
            return null;
        var formats = new Dictionary<string, byte[]>();
        foreach (var format in await clipboard.GetFormatsAsync())
            if (format is FileTransfer.UriList or FileTransfer.GnomeCopiedFiles or FileTransfer.KdeCutSelection
                && await clipboard.GetDataAsync(format) is { } value)
                formats[format] = value switch { byte[] b => b, string s => System.Text.Encoding.UTF8.GetBytes(s), _ => [] };
        return FileTransfer.FromFormats(formats);
    }
}
