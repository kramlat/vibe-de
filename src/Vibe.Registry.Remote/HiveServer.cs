using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Vibe.Registry.Remote;

// Serves one hive on a Unix socket. Every request is carried out as the identity the kernel reports for the
// connected process (see PeerCredentials), so the hive's permissions bind clients that cannot be trusted to
// check them themselves. The hive is usually opened shared, so that tools which read the file directly stay
// consistent with what the daemon writes.
public sealed class HiveServer : IDisposable
{
    public const int MaxConnections = 256;
    private const int EventBacklog = 1024;

    private readonly Hive _hive;
    private readonly Func<Socket, RegistryIdentity> _identityOf;
    private readonly Func<RegistryIdentity, bool> _accept;
    private readonly List<Connection> _connections = new();
    private readonly CancellationTokenSource _stopping = new();
    private Socket? _listener;
    private string? _socketPath;

    // identityOf and accept exist for tests and for the user daemon (which only serves its own user);
    // by default the peer's real credentials are used and everyone may connect.
    public HiveServer(Hive hive, Func<Socket, RegistryIdentity>? identityOf = null, Func<RegistryIdentity, bool>? accept = null)
    {
        _hive = hive;
        _identityOf = identityOf ?? PeerCredentials.Read;
        _accept = accept ?? (_ => true);
        _hive.Changed += OnChanged;
    }

    public int ConnectionCount
    {
        get { lock (_connections) return _connections.Count; }
    }

    // Creates the socket with exactly this permission (no moment when it is more open) and starts listening.
    // A leftover socket file from a daemon that died is replaced; a live one is an error.
    public void Listen(string socketPath, UnixFileMode mode)
    {
        socketPath = Path.GetFullPath(socketPath);
        var directory = Path.GetDirectoryName(socketPath)!;
        if (!Directory.Exists(directory))
        {
            var dirMode = mode;
            if ((mode & UnixFileMode.UserRead) != 0) dirMode |= UnixFileMode.UserExecute;
            if ((mode & UnixFileMode.GroupRead) != 0) dirMode |= UnixFileMode.GroupExecute;
            if ((mode & UnixFileMode.OtherRead) != 0) dirMode |= UnixFileMode.OtherExecute;
            Directory.CreateDirectory(directory, dirMode);
        }

        if (File.Exists(socketPath))
        {
            if (IsAlive(socketPath))
                throw new IOException($"Another daemon is already listening on {socketPath}.");
            File.Delete(socketPath);
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var previous = umask(0x1FF & ~(uint)mode);   // the socket file is created with `mode`
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        }
        finally
        {
            umask(previous);
        }
        File.SetUnixFileMode(socketPath, mode);
        _socketPath = socketPath;
        Listen(listener);
    }

    // For a socket that is already bound: socket activation, or a test.
    public void Listen(Socket listener)
    {
        listener.Listen(128);
        _listener = listener;
    }

    // The accept loop. Returns when cancelled or disposed.
    public async Task RunAsync(CancellationToken cancel = default)
    {
        var listener = _listener ?? throw new InvalidOperationException("Call Listen first.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _stopping.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var client = await listener.AcceptAsync(linked.Token);
                _ = Task.Run(() => ServeAsync(client, linked.Token));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // stopping
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _hive.Changed -= OnChanged;
        _listener?.Dispose();
        lock (_connections)
            foreach (var connection in _connections.ToList())
                connection.Close();
        if (_socketPath is not null)
        {
            try { File.Delete(_socketPath); } catch (IOException) { }
        }
    }

    // ---- one client ----

    private async Task ServeAsync(Socket socket, CancellationToken cancel)
    {
        Connection? connection = null;
        try
        {
            var identity = _identityOf(socket);
            if (!_accept(identity))
                return;

            connection = new Connection(socket, identity, _hive.As(identity));
            lock (_connections)
            {
                if (_connections.Count >= MaxConnections)
                    return;   // refuse quietly: the client sees the connection close
                _connections.Add(connection);
            }

            var writing = connection.WriteLoopAsync(cancel);
            await ReadLoopAsync(connection, cancel);
            connection.Outgoing.Writer.TryComplete();
            await writing;
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // a client that vanishes or misbehaves only loses its own connection
        }
        finally
        {
            if (connection is not null)
            {
                lock (_connections) _connections.Remove(connection);
                connection.Close();
            }
            else
            {
                socket.Dispose();
            }
        }
    }

    private async Task ReadLoopAsync(Connection connection, CancellationToken cancel)
    {
        bool greeted = false;
        while (await FrameIO.ReadAsync(connection.Stream, cancel) is var (opcode, requestId, body))
        {
            if (!greeted && opcode != Wire.Hello)
                throw new ProtocolException("The first message must be Hello.");

            var reply = Handle(connection, opcode, body, out var status);
            greeted = true;
            if (!connection.Send(WireWriter.Frame(Wire.Reply, requestId, new WireWriter().U8(status).ToArray().Concat(reply).ToArray())))
                return;   // too slow to keep up
            if (status == Wire.Unsupported)
                return;   // wrong protocol version: nothing more to say
        }
    }

    private byte[] Handle(Connection connection, byte opcode, byte[] body, out byte status)
    {
        status = Wire.Ok;
        var view = connection.View;
        var input = new WireReader(body);
        try
        {
            var output = new WireWriter();
            switch (opcode)
            {
                case Wire.Hello:
                    var version = input.U16();
                    input.ExpectEnd();
                    if (version != Wire.Version)
                    {
                        status = Wire.Unsupported;
                        return new WireWriter().String($"Protocol version {version} is not supported (this is {Wire.Version}).").ToArray();
                    }
                    output.U16(Wire.Version).String(_hive.Name).U32(connection.Identity.Uid).U32(connection.Identity.Gid).Bool(_hive.IsReadOnly);
                    break;

                case Wire.KeyExists:
                    output.Bool(view.KeyExists(ReadPath(input)));
                    break;
                case Wire.CreateKey:
                    view.CreateKey(ReadPath(input));
                    break;
                case Wire.DeleteKey:
                    output.Bool(view.DeleteKey(ReadPath(input)));
                    break;
                case Wire.GetSubKeyNames:
                    var names = view.GetSubKeyNames(ReadPath(input));
                    output.U32((uint)names.Count);
                    foreach (var name in names)
                        output.String(name);
                    break;
                case Wire.GetKeyInfo:
                    var info = view.GetKeyInfo(ReadPath(input));
                    output.U32((uint)info.Flags).I64(info.CreatedUtc.Ticks).I64(info.ModifiedUtc.Ticks)
                        .I32(info.SubKeyCount).I32(info.ValueCount).Perms(info.Permissions);
                    break;
                case Wire.SetKeyFlags:
                    var flagsPath = input.String();
                    var flags = (RegistryKeyFlags)input.U32();
                    input.ExpectEnd();
                    view.SetKeyFlags(flagsPath, flags);
                    break;
                case Wire.GetKeyPermissions:
                    output.Perms(view.GetKeyPermissions(ReadPath(input)));
                    break;
                case Wire.SetKeyPermissions:
                    var keyPath = input.String();
                    var keyPerms = input.Perms();
                    input.ExpectEnd();
                    view.SetKeyPermissions(keyPath, keyPerms);
                    break;

                case Wire.GetValue:
                    var valuePath = input.String();
                    var valueName = input.String();
                    input.ExpectEnd();
                    var value = view.GetValue(valuePath, valueName);
                    output.Bool(value is not null);
                    if (value is not null)
                        output.Value(value);
                    break;
                case Wire.GetValues:
                    var values = view.GetValues(ReadPath(input));
                    output.U32((uint)values.Count);
                    foreach (var v in values)
                        output.Value(v);
                    break;
                case Wire.GetValuePermissions:
                    var permsPath = input.String();
                    var permsName = input.String();
                    input.ExpectEnd();
                    var perms = view.GetValuePermissions(permsPath, permsName);
                    output.Bool(perms is not null);
                    if (perms is { } p)
                        output.Perms(p);
                    break;
                case Wire.SetValue:
                    var setPath = input.String();
                    var setName = input.String();
                    var kind = (RegistryValueKind)input.U16();
                    var data = input.Bytes();
                    input.ExpectEnd();
                    if (!Enum.IsDefined(kind))
                        throw new ProtocolException($"Unknown value kind {(ushort)kind}.");
                    view.SetValue(setPath, setName, kind, ValueCodec.Decode(kind, data));
                    break;
                case Wire.SetValuePermissions:
                    var vpPath = input.String();
                    var vpName = input.String();
                    var vpPerms = input.Perms();
                    input.ExpectEnd();
                    view.SetValuePermissions(vpPath, vpName, vpPerms);
                    break;
                case Wire.DeleteValue:
                    var delPath = input.String();
                    var delName = input.String();
                    input.ExpectEnd();
                    output.Bool(view.DeleteValue(delPath, delName));
                    break;

                case Wire.Subscribe:
                    connection.SubscribedPath = ReadPath(input);
                    break;
                case Wire.Unsubscribe:
                    input.ExpectEnd();
                    connection.SubscribedPath = null;
                    break;

                default:
                    throw new ProtocolException($"Unknown request {opcode:x2}.");
            }
            return output.ToArray();
        }
        catch (KeyNotFoundException e) { return Error(Wire.NotFound, e.Message, out status); }
        catch (UnauthorizedAccessException e) { return Error(Wire.Denied, e.Message, out status); }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidDataException or ProtocolException or OverflowException)
        {
            return Error(Wire.BadRequest, e.Message, out status);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            return Error(Wire.Internal, e.Message, out status);
        }
    }

    private static string ReadPath(WireReader input)
    {
        var path = input.String();
        input.ExpectEnd();
        return path;
    }

    private static byte[] Error(byte code, string message, out byte status)
    {
        status = code;
        return new WireWriter().String(message).ToArray();
    }

    // ---- events ----

    private void OnChanged(object? sender, RegistryChange change)
    {
        Connection[] subscribers;
        lock (_connections)
            subscribers = _connections.Where(c => c.SubscribedPath is not null).ToArray();

        foreach (var connection in subscribers)
        {
            var path = connection.SubscribedPath;
            if (path is null || !change.IsUnder(path) || !CanSee(connection, change))
                continue;

            var body = new WireWriter().U8((byte)change.Kind).String(change.KeyPath).Bool(change.ValueName is not null).String(change.ValueName ?? "").ToArray();
            if (!connection.Send(WireWriter.Frame(Wire.Event, 0, body)))
                connection.Close();   // a client that cannot keep up is dropped, not allowed to stall writers
        }
    }

    // A client only hears about keys it could look at itself: it must be able to search down to the key
    // (for a deleted key, down to where it was).
    private static bool CanSee(Connection connection, RegistryChange change)
    {
        var probe = change.KeyPath;
        if (change.Kind == RegistryChangeKind.KeyDeleted)
        {
            int cut = probe.LastIndexOf('\\');
            probe = cut < 0 ? string.Empty : probe[..cut];
        }
        return connection.View.KeyExists(probe);
    }

    // ---- helpers ----

    private static bool IsAlive(string path)
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    [DllImport("libc")]
    private static extern uint umask(uint mask);

    private sealed class Connection
    {
        private readonly Socket _socket;

        public Connection(Socket socket, RegistryIdentity identity, IHive view)
        {
            _socket = socket;
            Stream = new NetworkStream(socket, ownsSocket: false);
            Identity = identity;
            View = view;
        }

        public NetworkStream Stream { get; }
        public RegistryIdentity Identity { get; }
        public IHive View { get; }
        public volatile string? SubscribedPath;

        // Everything sent to the client, replies and events alike, goes through here so writes never interleave.
        public Channel<byte[]> Outgoing { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(EventBacklog)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        public bool Send(byte[] frame) => Outgoing.Writer.TryWrite(frame);

        public async Task WriteLoopAsync(CancellationToken cancel)
        {
            try
            {
                await foreach (var frame in Outgoing.Reader.ReadAllAsync(cancel))
                    await Stream.WriteAsync(frame, cancel);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                Close();
            }
        }

        public void Close()
        {
            Outgoing.Writer.TryComplete();
            try { _socket.Shutdown(SocketShutdown.Both); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
            _socket.Dispose();
        }
    }
}
