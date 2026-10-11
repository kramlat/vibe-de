using System.Diagnostics;

namespace Vibe.Registry;

// How processes share one hive file without a daemon (the protocol the Wine fork should follow too):
//   writers   take an exclusive flock on "<hive>.lock", re-read the hive if it changed, apply their change,
//             write "<hive>.tmp", fsync it and rename it over the hive, then release the lock
//   readers   never lock: the rename is atomic, so they always see a complete file, and are told about
//             changes by a file watcher (or by calling Refresh)
// .NET implements FileShare.None on Unix with flock(LOCK_EX), so C code can use flock(2) on the same file.
internal sealed class HiveFile
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);

    private (long length, long modified, uint crc)? _stamp;

    public string Path { get; }
    public bool ReadOnly { get; }

    // createMode is the permission a newly created hive file gets (an existing file keeps its own);
    // new directories get the same bits plus execute wherever read is allowed: 0600 -> 0700, 0644 -> 0755.
    public HiveFile(string path, bool readOnly, UnixFileMode createMode)
    {
        Path = System.IO.Path.GetFullPath(path);
        ReadOnly = readOnly;
        CreateMode = createMode;
    }

    public UnixFileMode CreateMode { get; }

    private UnixFileMode DirectoryMode
    {
        get
        {
            var mode = CreateMode;
            if ((mode & UnixFileMode.UserRead) != 0) mode |= UnixFileMode.UserExecute;
            if ((mode & UnixFileMode.GroupRead) != 0) mode |= UnixFileMode.GroupExecute;
            if ((mode & UnixFileMode.OtherRead) != 0) mode |= UnixFileMode.OtherExecute;
            return mode;
        }
    }

    private string LockPath => Path + ".lock";

    public IDisposable Lock()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!, DirectoryMode);
        var clock = Stopwatch.StartNew();
        for (int wait = 1; ; wait = Math.Min(wait * 2, 50))
        {
            try
            {
                return new LockHandle(new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (clock.Elapsed < LockTimeout)
            {
                Thread.Sleep(wait);   // someone else is writing; their critical section is a few milliseconds
            }
        }
    }

    // The hive as it is on disk now, or null when it is the same as the last one we saw.
    // A missing file reads as an empty hive.
    public Node? ReadIfChanged(Func<Node> emptyRoot)
    {
        var info = new FileInfo(Path);
        if (!info.Exists)
        {
            if (_stamp is null)
                return null;
            _stamp = null;
            return emptyRoot();
        }

        var stamp = StampOf(info);
        if (stamp == _stamp)
            return null;

        Node root;
        using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            root = FhiveFormat.Read(stream);
        _stamp = stamp;
        return root;
    }

    public void Write(Node root)
    {
        var temp = Path + ".tmp";
        // The replacement keeps the hive's permission (or gets CreateMode the first time), and is created
        // with it, so there is never a moment when the data is readable by more than it should be.
        var mode = File.Exists(Path) ? File.GetUnixFileMode(Path) : CreateMode;
        File.Delete(temp);
        using (var stream = new FileStream(temp, new FileStreamOptions
               {
                   Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = mode,
               }))
        {
            FhiveFormat.Write(root, stream);
            stream.Flush(flushToDisk: true);   // the data must be on disk before the rename makes it the hive
        }
        File.Move(temp, Path, overwrite: true);
        _stamp = StampOf(new FileInfo(Path));
    }

    // Calls changed (on a thread-pool thread, debounced) whenever the file is written, replaced or deleted.
    public IDisposable? Watch(Action changed)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        if (!Directory.Exists(directory))
        {
            // A fresh account has no hive directory yet; without it we would never hear about the first write.
            if (ReadOnly)
                return null;
            Directory.CreateDirectory(directory, DirectoryMode);
        }

        var fileName = System.IO.Path.GetFileName(Path);
        var debounce = new Timer(_ => changed(), null, Timeout.Infinite, Timeout.Infinite);
        var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        FileSystemEventHandler handler = (_, _) => debounce.Change(25, Timeout.Infinite);
        watcher.Changed += handler;
        watcher.Created += handler;
        watcher.Deleted += handler;
        watcher.Renamed += (_, _) => debounce.Change(25, Timeout.Infinite);
        watcher.EnableRaisingEvents = true;
        return new Watching(watcher, debounce);
    }

    // Length and modification time say "probably changed"; the checksum in the header makes sure.
    private static (long, long, uint) StampOf(FileInfo info)
    {
        uint crc = 0;
        try
        {
            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[64];
            if (stream.Read(header, 0, header.Length) == header.Length)
                crc = BitConverter.ToUInt32(header, 48);
        }
        catch (IOException)
        {
        }
        return (info.Length, info.LastWriteTimeUtc.Ticks, crc);
    }

    private sealed class LockHandle(FileStream stream) : IDisposable
    {
        public void Dispose() => stream.Dispose();
    }

    private sealed class Watching(FileSystemWatcher watcher, Timer timer) : IDisposable
    {
        public void Dispose()
        {
            watcher.Dispose();
            timer.Dispose();
        }
    }
}
