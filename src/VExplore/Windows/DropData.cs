using Avalonia.Input;
using Vde.Shell;

namespace VExplore.Windows;

// Files in a drag, as every file manager puts them: a text/uri-list (and the GNOME copied-files list), so a drag
// can start in one program of the desktop and end in another.
public static class DropData
{
    public static IReadOnlyList<string> Paths(DragEventArgs e)
    {
        var formats = new Dictionary<string, byte[]>();
        foreach (var item in e.DataTransfer.Items.Select(i => (IDataTransferItem)i))
            foreach (var format in new[] { FileTransfer.UriList, FileTransfer.GnomeCopiedFiles })
                if (item.Contains(DataFormat.CreateBytesPlatformFormat(format)) && item.TryGetValue(DataFormat.CreateBytesPlatformFormat(format)) is { } bytes)
                    formats[format] = bytes;
        return FileTransfer.FromFormats(formats)?.Paths ?? [];
    }

    public static DataTransfer Of(IReadOnlyList<string> paths)
    {
        var data = new DataTransfer();
        foreach (var (format, bytes) in new FileTransfer(TransferKind.Copy, paths).ToFormats())
            data.Add(DataTransferItem.Create(DataFormat.CreateBytesPlatformFormat(format), bytes));
        return data;
    }

    public static DragDropEffects Effects(DropEffect effect) => effect switch
    {
        DropEffect.Copy => DragDropEffects.Copy, DropEffect.Move => DragDropEffects.Move, DropEffect.Link => DragDropEffects.Link, _ => DragDropEffects.None,
    };
}
