namespace Vde.Shell;

public enum ConflictAction
{
    Replace,    // overwrite a file; merge into a folder
    Skip,
    KeepBoth,   // the new one gets "name (copy).ext"
    Cancel,
}

public delegate ConflictAction ConflictResolver(string source, string destination);

public sealed record FileProgress(string CurrentPath, long BytesDone, long BytesTotal, int ItemsDone, int ItemsTotal);

public sealed class FileOperationOptions
{
    // Called when the destination exists. Without one, a conflict is an IOException.
    public ConflictResolver? OnConflict { get; init; }
    public IProgress<FileProgress>? Progress { get; init; }
    public CancellationToken Cancel { get; init; }
    // When set, copying stops between blocks while the gate is paused (the pause button of a transfer window).
    public PauseGate? Pause { get; init; }
}

// Lets one thread stop another at a safe point and let it go on again. A cancel wakes a paused waiter.
public sealed class PauseGate
{
    private readonly object _lock = new();
    private bool _paused;

    public bool IsPaused { get { lock (_lock) return _paused; } }

    public void Pause() { lock (_lock) _paused = true; }

    public void Resume()
    {
        lock (_lock)
        {
            _paused = false;
            Monitor.PulseAll(_lock);
        }
    }

    // Returns at once when not paused; otherwise waits for Resume, and throws if cancelled meanwhile.
    public void Wait(CancellationToken cancel)
    {
        lock (_lock)
        {
            while (_paused)
            {
                cancel.ThrowIfCancellationRequested();
                Monitor.Wait(_lock, 100);   // wakes now and then to see a cancel
            }
        }
    }
}

public static class FileOperations
{
    private const int BufferSize = 256 * 1024;

    // Returns the paths that were created.
    public static IReadOnlyList<string> Copy(IEnumerable<string> sources, string destinationFolder, FileOperationOptions? options = null)
        => Transfer(ShellVfs.Resolve(sources), ShellVfs.Resolve(destinationFolder), move: false, options ?? new());

    public static IReadOnlyList<string> Move(IEnumerable<string> sources, string destinationFolder, FileOperationOptions? options = null)
        => Transfer(ShellVfs.Resolve(sources), ShellVfs.Resolve(destinationFolder), move: true, options ?? new());

    public static string Rename(string path, string newName)
    {
        if (newName.Length == 0 || newName.Contains('/') || newName is "." or "..")
            throw new ArgumentException("Not a valid file name.", nameof(newName));

        path = Path.GetFullPath(ShellVfs.Resolve(path));
        var target = Path.Combine(Path.GetDirectoryName(path)!, newName);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException($"'{newName}' already exists.");

        if (Directory.Exists(path))
            Directory.Move(path, target);
        else
            File.Move(path, target);
        return target;
    }

    // "New folder", "New folder (2)", ...
    public static string CreateFolder(string parent, string baseName = "New folder")
    {
        parent = ShellVfs.Resolve(parent);
        for (int i = 1; ; i++)
        {
            var path = Path.Combine(parent, i == 1 ? baseName : $"{baseName} ({i})");
            if (File.Exists(path) || Directory.Exists(path))
                continue;
            Directory.CreateDirectory(path);
            return path;
        }
    }

    // An empty file: "New Text Document.txt", "New Text Document (2).txt", ...
    public static string CreateFile(string parent, string baseName = "New Text Document", string extension = ".txt")
    {
        parent = ShellVfs.Resolve(parent);
        for (int i = 1; ; i++)
        {
            var path = Path.Combine(parent, (i == 1 ? baseName : $"{baseName} ({i})") + extension);
            if (File.Exists(path) || Directory.Exists(path))
                continue;
            using (File.Open(path, FileMode.CreateNew)) { }
            return path;
        }
    }

    // Permanent deletion. Use Trash.Send for the recoverable kind.
    public static void Delete(IEnumerable<string> paths)
    {
        foreach (var path in ShellVfs.Resolve(paths))
        {
            if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null)
                Directory.Delete(path, recursive: true);
            else
                File.Delete(path);
        }
    }

    // ---- implementation ----

    private static IReadOnlyList<string> Transfer(IEnumerable<string> sources, string destinationFolder, bool move, FileOperationOptions options)
    {
        var list = sources.Select(Path.GetFullPath).ToList();
        destinationFolder = Path.GetFullPath(destinationFolder);
        Directory.CreateDirectory(destinationFolder);

        foreach (var source in list)
            if (Directory.Exists(source) && IsInside(destinationFolder, source))
                throw new IOException($"Cannot put '{source}' inside itself.");

        var (bytesTotal, itemsTotal) = Measure(list);
        var state = new Counter(bytesTotal, itemsTotal, options);
        var created = new List<string>();

        foreach (var source in list)
        {
            options.Cancel.ThrowIfCancellationRequested();
            var target = Path.Combine(destinationFolder, Path.GetFileName(source));

            if (move && Path.GetFullPath(target) == source)
                continue;   // moving something onto itself is a no-op

            target = ResolveConflict(source, target, options);
            if (target is null)
                continue;

            if (move && TryRename(source, target))
            {
                state.Done(source, ItemSize(source, target));
                created.Add(target);
                continue;
            }

            CopyTree(source, target, options.Cancel, state, options.OnConflict);
            created.Add(target);
            if (move)
                Delete([source]);
        }
        return created;
    }

    // Returns the path to write to, or null to skip. Throws when cancelled or unresolved.
    private static string? ResolveConflict(string source, string target, FileOperationOptions options)
    {
        if (!Exists(target))
            return target;

        // A folder merging into a folder is resolved per file later, so the folder itself is not a conflict.
        var action = options.OnConflict?.Invoke(source, target)
            ?? throw new IOException($"'{target}' already exists.");

        switch (action)
        {
            case ConflictAction.Replace:
                if (!Directory.Exists(source) && Directory.Exists(target))
                    throw new IOException($"Cannot replace folder '{target}' with a file.");
                return target;
            case ConflictAction.Skip:
                return null;
            case ConflictAction.KeepBoth:
                return UniqueName(target);
            default:
                throw new OperationCanceledException();
        }
    }

    private static string UniqueName(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} (copy{(i == 1 ? "" : " " + i)}){ext}");
            if (!Exists(candidate))
                return candidate;
        }
    }

    private static bool TryRename(string source, string target)
    {
        try
        {
            if (Directory.Exists(source) && new DirectoryInfo(source).LinkTarget is null)
            {
                if (Directory.Exists(target))
                    return false;   // merging: do it file by file
                Directory.Move(source, target);
            }
            else
            {
                File.Move(source, target, overwrite: true);
            }
            return true;
        }
        catch (IOException)
        {
            return false;   // e.g. different filesystem
        }
    }

    internal static void CopyTree(string source, string target, CancellationToken cancel, Counter? counter, ConflictResolver? onConflict = null)
    {
        var info = new FileInfo(source);
        if (info.LinkTarget is not null)
        {
            if (Exists(target))
                File.Delete(target);
            File.CreateSymbolicLink(target, info.LinkTarget);
            counter?.Done(source, 0);
            return;
        }

        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(target);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            {
                cancel.ThrowIfCancellationRequested();
                var child = Path.Combine(target, Path.GetFileName(entry));
                if (Exists(child) && !Directory.Exists(entry))
                {
                    var action = onConflict?.Invoke(entry, child) ?? ConflictAction.Replace;
                    if (action == ConflictAction.Skip) { counter?.Done(entry, FileLength(entry)); continue; }
                    if (action == ConflictAction.Cancel) throw new OperationCanceledException();
                    if (action == ConflictAction.KeepBoth) child = UniqueName(child);
                }
                CopyTree(entry, child, cancel, counter, onConflict);
            }
            try { Directory.SetLastWriteTime(target, Directory.GetLastWriteTime(source)); }
            catch (IOException) { }
            counter?.Done(source, 0);
            return;
        }

        CopyFile(source, target, cancel, counter);
    }

    private static void CopyFile(string source, string target, CancellationToken cancel, Counter? counter)
    {
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    counter?.Bytes(source, read);
                }
            }
            File.SetUnixFileMode(target, File.GetUnixFileMode(source));
            File.SetLastWriteTime(target, File.GetLastWriteTime(source));
            counter?.Done(source, 0);
        }
        catch
        {
            // Never leave a half-written file behind.
            try { File.Delete(target); } catch (IOException) { }
            throw;
        }
    }

    private static (long bytes, int items) Measure(IEnumerable<string> roots)
    {
        long bytes = 0;
        int items = 0;
        foreach (var root in roots)
        {
            if (Directory.Exists(root) && new DirectoryInfo(root).LinkTarget is null)
                foreach (var file in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
                {
                    items++;
                    if (File.Exists(file))
                        bytes += FileLength(file);
                }
            else
                bytes += FileLength(root);
            items++;
        }
        return (bytes, items);
    }

    private static long ItemSize(string source, string target) => Directory.Exists(target) ? 0 : FileLength(target);

    private static long FileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
    }

    private static bool Exists(string path)
        => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private static bool IsInside(string path, string folder)
        => path == folder || path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);

    internal sealed class Counter
    {
        private readonly long _bytesTotal;
        private readonly int _itemsTotal;
        private readonly FileOperationOptions _options;
        private long _bytes;
        private int _items;

        public Counter(long bytesTotal, int itemsTotal, FileOperationOptions options)
        {
            _bytesTotal = bytesTotal;
            _itemsTotal = itemsTotal;
            _options = options;
        }

        public void Bytes(string path, long count)
        {
            _options.Pause?.Wait(_options.Cancel);
            _bytes += count;
            _options.Progress?.Report(new FileProgress(path, _bytes, _bytesTotal, _items, _itemsTotal));
        }

        public void Done(string path, long bytes)
        {
            _bytes += bytes;
            _items++;
            _options.Progress?.Report(new FileProgress(path, _bytes, _bytesTotal, _items, _itemsTotal));
        }
    }
}
