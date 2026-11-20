using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Vde.Shell;

namespace Vde.Shell.Controls;

// A folder icon with a peek at what is inside: up to three of its files, drawn small and fanned over the folder's
// front, the way Windows' and Dolphin's large folder icons show their contents. A file shows its cached thumbnail
// (FileLook) or its type's icon. The folder is read off the UI thread, so a big or slow folder never holds a window;
// until it answers (and when it is empty or unreadable) the plain folder icon shows.
public sealed class FolderPreview : Panel
{
    private const int MaxPeek = 3, MaxScanned = 64;

    private readonly Canvas _peek = new();
    private readonly ShellIcon _folder;

    public FolderPreview(string path, int size, IReadOnlyList<string> folderIcons, FileTypes? types)
    {
        _folder = new ShellIcon { IconNames = folderIcons, IconSize = size };
        Width = Height = _folder.EffectiveSize;
        Children.Add(_folder);
        Children.Add(_peek);
        _ = Task.Run(() => Pick(path, types)).ContinueWith(t =>
        {
            if (t.Result is { Count: > 0 } picked)
                Dispatcher.UIThread.Post(() => Show(picked));
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    // The names to draw for up to MaxPeek of the folder's files; images that have a thumbnail come first.
    private static List<IReadOnlyList<string>> Pick(string path, FileTypes? types)
    {
        var withThumb = new List<IReadOnlyList<string>>();
        var plain = new List<IReadOnlyList<string>>();
        try
        {
            int seen = 0;
            foreach (var file in Directory.EnumerateFiles(path))
            {
                if (++seen > MaxScanned || withThumb.Count >= MaxPeek) break;
                if (Path.GetFileName(file).StartsWith('.')) continue;
                if (FileLook.Thumbnail(file) is { } thumb)
                    withThumb.Add([thumb]);
                else if (plain.Count < MaxPeek)
                    plain.Add(types?.Describe(file).IconNames ?? ["text-x-generic"]);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return withThumb.Concat(plain).Take(MaxPeek).ToList();
    }

    private void Show(List<IReadOnlyList<string>> picked)
    {
        int size = _folder.EffectiveSize;
        int small = IconSizes.Snap((int)(size * 0.42));
        _peek.Children.Clear();
        // the middle one last, so it is in front; the others lean out to each side
        var order = picked.Count == 1 ? new[] { 0 } : picked.Count == 2 ? new[] { 1, 0 } : new[] { 1, 2, 0 };
        foreach (var i in order)
        {
            double offset = picked.Count == 1 ? 0 : (i - (picked.Count - 1) / 2.0) * small * 0.62;
            var icon = new ShellIcon { IconNames = picked[i], IconSize = small };
            Canvas.SetLeft(icon, (size - small) / 2.0 + offset);
            Canvas.SetTop(icon, size * 0.36 + Math.Abs(offset) * 0.12);
            _peek.Children.Add(icon);
        }
    }
}
