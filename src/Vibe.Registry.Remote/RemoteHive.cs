using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Vibe.Registry.Remote;

// A hive that lives in a daemon (fhived), used exactly like a local one. The daemon checks everything against
// this process's identity as the kernel reports it. Calls block until the daemon answers.
public sealed class RemoteHive : IHive, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly string _socketPath;
    private readonly TimeSpan _timeout;
    private readonly object _sendLock = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<(byte status, byte[] body)>> _pending = new();
    private readonly Channel<RegistryChange> _events = Channel.CreateUnbounded<RegistryChange>(new UnboundedChannelOptions { SingleReader = true });
    private EventHandler<RegistryChange>? _changed;
    private Socket? _socket;
    private NetworkStream? _stream;
    private volatile bool _connected;
    private bool _wantEvents;
    private uint _nextId;
    private bool _disposed;

    public string Name { get; private set; } = string.Empty;

    // What the daemon sees this process as: the uid and gid it applies permissions for.
    public uint Uid { get; private set; }
    public uint Gid { get; private set; }
    public bool IsReadOnly { get; private set; }

    private RemoteHive(string socketPath, TimeSpan timeout)
    {
        _socketPath = socketPath;
        _timeout = timeout;
        _ = Task.Run(DispatchEventsAsync);   // events are delivered one at a time, in order, off the reader thread
    }

    // Throws IOException when no daemon is listening: callers fall back to the file.
    public static RemoteHive Connect(string socketPath, TimeSpan? timeout = null)
    {
        var hive = new RemoteHive(socketPath, timeout ?? DefaultTimeout);
        try
        {
            hive.ConnectCore();
        }
        catch
        {
            hive.Dispose();
            throw;
        }
        return hive;
    }

    public event EventHandler<RegistryChange>? Changed
    {
        add
        {
            _changed += value;
            Subscribe();
        }
        remove => _changed -= value;
    }

    public void Dispose()
    {
        _disposed = true;
        _events.Writer.TryComplete();
        Disconnect(new ObjectDisposedException(nameof(RemoteHive)));
    }

    // ---- IHive ----

    public bool KeyExists(string path) => new WireReader(Call(Wire.KeyExists, w => w.String(path))).Bool();
    public void CreateKey(string path) => Call(Wire.CreateKey, w => w.String(path));
    public bool DeleteKey(string path) => new WireReader(Call(Wire.DeleteKey, w => w.String(path))).Bool();

    public IReadOnlyList<string> GetSubKeyNames(string path)
    {
        var reader = new WireReader(Call(Wire.GetSubKeyNames, w => w.String(path)));
        var count = reader.U32();
        var names = new List<string>();
        for (uint i = 0; i < count; i++)
            names.Add(reader.String());
        return names;
    }

    public RegistryKeyInfo GetKeyInfo(string path)
    {
        var r = new WireReader(Call(Wire.GetKeyInfo, w => w.String(path)));
        var flags = (RegistryKeyFlags)r.U32();
        var created = new DateTime(r.I64(), DateTimeKind.Utc);
        var modified = new DateTime(r.I64(), DateTimeKind.Utc);
        return new RegistryKeyInfo(flags, created, modified, r.I32(), r.I32(), r.Perms());
    }

    public void SetKeyFlags(string path, RegistryKeyFlags flags) => Call(Wire.SetKeyFlags, w => w.String(path).U32((uint)flags));
    public PosixPermissions GetKeyPermissions(string path) => new WireReader(Call(Wire.GetKeyPermissions, w => w.String(path))).Perms();
    public void SetKeyPermissions(string path, PosixPermissions permissions) => Call(Wire.SetKeyPermissions, w => w.String(path).Perms(permissions));

    public RegistryValue? GetValue(string path, string name)
    {
        var r = new WireReader(Call(Wire.GetValue, w => w.String(path).String(name)));
        return r.Bool() ? r.Value() : null;
    }

    public IReadOnlyList<RegistryValue> GetValues(string path)
    {
        var r = new WireReader(Call(Wire.GetValues, w => w.String(path)));
        var count = r.U32();
        var values = new List<RegistryValue>();
        for (uint i = 0; i < count; i++)
            values.Add(r.Value());
        return values;
    }

    public PosixPermissions? GetValuePermissions(string path, string name)
    {
        var r = new WireReader(Call(Wire.GetValuePermissions, w => w.String(path).String(name)));
        return r.Bool() ? r.Perms() : null;
    }

    public void SetValue(string path, string name, RegistryValueKind kind, object? data)
    {
        // Validate and normalise here, like a local hive would, so a bad value fails before it is sent.
        var value = new RegistryValue(name, kind, data);
        Call(Wire.SetValue, w => w.String(path).String(name).U16((ushort)kind).Bytes(ValueCodec.Encode(kind, value.RawData)));
    }

    public void SetValuePermissions(string path, string name, PosixPermissions permissions)
        => Call(Wire.SetValuePermissions, w => w.String(path).String(name).Perms(permissions));

    public bool DeleteValue(string path, string name) => new WireReader(Call(Wire.DeleteValue, w => w.String(path).String(name))).Bool();

    // ---- connection ----

    private void ConnectCore()
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(_socketPath));
        }
        catch (SocketException e)
        {
            socket.Dispose();
            throw new IOException($"Cannot reach the registry daemon at {_socketPath}: {e.Message}", e);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        _connected = true;
        _ = Task.Run(() => ReadLoopAsync(_stream));

        var hello = Request(Wire.Hello, w => w.U16(Wire.Version));
        var reader = new WireReader(hello);
        reader.U16();
        Name = reader.String();
        Uid = reader.U32();
        Gid = reader.U32();
        IsReadOnly = reader.Bool();

        if (_wantEvents)
            Request(Wire.Subscribe, w => w.String(string.Empty));
    }

    private void Subscribe()
    {
        _wantEvents = true;
        try
        {
            if (_connected)
                Request(Wire.Subscribe, w => w.String(string.Empty));
        }
        catch (IOException)
        {
            // not connected right now: the next call reconnects and subscribes then
        }
    }

    private byte[] Call(byte opcode, Action<WireWriter>? build = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RemoteHive));
        lock (_sendLock)   // reconnecting is rare and must not happen twice at once
        {
            if (!_connected)
                ConnectCore();
        }
        return Request(opcode, build);
    }

    private byte[] Request(byte opcode, Action<WireWriter>? build)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<(byte, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var body = new WireWriter();
            build?.Invoke(body);
            var frame = WireWriter.Frame(opcode, id, body.ToArray());
            lock (_sendLock)
                _stream!.Write(frame);

            if (!completion.Task.Wait(_timeout))
                throw new IOException($"The registry daemon did not answer within {_timeout.TotalSeconds:0} seconds.");
            var (status, reply) = completion.Task.Result;
            if (status != Wire.Ok)
                throw MapError(status, new WireReader(reply).String());
            return reply;
        }
        catch (AggregateException e) when (e.InnerException is not null)
        {
            throw e.InnerException;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private static Exception MapError(byte status, string message) => status switch
    {
        Wire.NotFound => new KeyNotFoundException(message),
        Wire.Denied => new UnauthorizedAccessException(message),
        Wire.BadRequest => new ArgumentException(message),
        Wire.Unsupported => new ProtocolException(message),
        _ => new IOException("The registry daemon reported an error: " + message),
    };

    private async Task ReadLoopAsync(NetworkStream stream)
    {
        try
        {
            while (await FrameIO.ReadAsync(stream, CancellationToken.None) is var (opcode, requestId, body))
            {
                if (opcode == Wire.Reply)
                {
                    if (_pending.TryGetValue(requestId, out var completion))
                    {
                        // body: status byte, then the result (or the error message)
                        completion.TrySetResult((body[0], body[1..]));
                    }
                }
                else if (opcode == Wire.Event)
                {
                    var r = new WireReader(body);
                    var kind = (RegistryChangeKind)r.U8();
                    var path = r.String();
                    var hasName = r.Bool();
                    var name = r.String();
                    _events.Writer.TryWrite(new RegistryChange(kind, path, hasName ? name : null));
                }
            }
            Disconnect(new IOException("The registry daemon closed the connection."));
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Disconnect(new IOException("Lost the connection to the registry daemon: " + e.Message, e));
        }
    }

    private async Task DispatchEventsAsync()
    {
        await foreach (var change in _events.Reader.ReadAllAsync())
        {
            try
            {
                _changed?.Invoke(this, change);
            }
            catch (Exception)
            {
                // a handler's bug must not stop the others from being told
            }
        }
    }

    private void Disconnect(Exception reason)
    {
        _connected = false;
        foreach (var completion in _pending.Values)
            completion.TrySetException(reason);
        try { _socket?.Dispose(); } catch (ObjectDisposedException) { }
    }
}
