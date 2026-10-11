using System.Net.Sockets;
using Vibe.Registry;
using Vibe.Registry.Remote;

namespace Vibe.Registry.Remote.Tests;

// A daemon on a temporary socket. By default it uses the real kernel credentials of each client; with
// Identities set, the next connections are given those identities instead, so one test process can play
// several users.
internal sealed class Daemon : IDisposable
{
    private readonly Queue<RegistryIdentity>? _identities;
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _keepFiles;

    public string Dir { get; }
    public string Socket => Path.Combine(Dir, "run", "fhive.sock");
    public string HivePath => Path.Combine(Dir, "hive", "User.fhive");
    public Hive Hive { get; }
    public HiveServer Server { get; }
    public Task Running { get; }

    public Daemon(IEnumerable<RegistryIdentity>? identities = null, bool readOnly = false, UnixFileMode? socketMode = null,
        Func<RegistryIdentity, bool>? accept = null, string? dir = null, bool watch = false, bool keepFiles = false)
    {
        _keepFiles = keepFiles;
        Dir = dir ?? Path.Combine(Path.GetTempPath(), "fhive-" + Guid.NewGuid().ToString("N"));
        _identities = identities is null ? null : new Queue<RegistryIdentity>(identities);

        if (readOnly)
            using (var seed = Hive.OpenShared("HKCU", HivePath, watch: false))
                seed.SetValue("Seed", "v", RegistryValueKind.Bool, true);
        Hive = Hive.OpenShared("HKCU", HivePath, watch: watch, readOnly: readOnly);

        Func<Socket, RegistryIdentity>? resolver = _identities is null ? null : _ =>
        {
            lock (_identities) return _identities.Dequeue();
        };
        Server = new HiveServer(Hive, resolver, accept);
        Server.Listen(Socket, socketMode ?? (UnixFileMode.UserRead | UnixFileMode.UserWrite));
        Running = Server.RunAsync(_stop.Token);
    }

    public RemoteHive Connect() => RemoteHive.Connect(Socket, TimeSpan.FromSeconds(10));

    public void Dispose()
    {
        _stop.Cancel();
        Server.Dispose();
        Hive.Dispose();
        if (!_keepFiles && Directory.Exists(Dir))
            Directory.Delete(Dir, recursive: true);
    }
}

public class RemoteTests
{
    private static readonly RegistryIdentity Alice = new(1000, 1000, [100]);
    private static readonly RegistryIdentity Bob = new(1001, 1001, [100]);
    private static readonly RegistryIdentity Carol = new(1002, 1002);

    private static ushort M(string octal) => PosixPermissions.ParseMode(octal);

    private static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting for " + what);
            Thread.Sleep(10);
        }
    }

    // A daemon where Alice owns an open root; clients are Alice, Bob and Carol in connection order.
    private static Daemon ThreeUsers()
    {
        var daemon = new Daemon([Alice, Bob, Carol]);
        daemon.Hive.Identity = RegistryIdentity.Root;
        daemon.Hive.SetKeyPermissions("", new PosixPermissions(0, 0, M("777")));
        return daemon;
    }

    [Fact]
    public void The_daemon_sees_the_real_kernel_credentials_of_the_client()
    {
        using var daemon = new Daemon();   // default resolver: SO_PEERCRED + SO_PEERGROUPS
        using var client = daemon.Connect();

        var me = RegistryIdentity.Current;
        Assert.Equal(me.Uid, client.Uid);
        Assert.Equal(me.Gid, client.Gid);
        Assert.Equal(daemon.Hive.Name, client.Name);
        Assert.False(client.IsReadOnly);

        var seen = PeerCredentials.Read(RawConnect(daemon.Socket, out var socket));
        socket.Dispose();
        Assert.Equal(me.Uid, seen.Uid);
        Assert.Equal(me.Groups.OrderBy(g => g), seen.Groups.OrderBy(g => g));   // the supplementary groups too
    }

    private static Socket RawConnect(string path, out Socket socket)
    {
        socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(path));
        return socket;
    }

    [Fact]
    public void Every_operation_works_through_the_socket_like_it_does_locally()
    {
        using var daemon = new Daemon();
        using var client = daemon.Connect();

        client.CreateKey(@"A\B");
        Assert.True(client.KeyExists(@"A\B"));
        Assert.False(client.KeyExists("Nope"));
        client.SetValue(@"A\B", "s", RegistryValueKind.String, "héllo ✓");
        client.SetValue(@"A\B", "n", RegistryValueKind.DWord, 42u);
        client.SetValue(@"A\B", "m", RegistryValueKind.MultiString, new[] { "a", "b" });
        client.SetValue(@"A\B", "g", RegistryValueKind.Guid, Guid.Parse("6f1f1f4e-8d3b-4a53-9c3f-0e0d6b1a2c3d"));
        client.SetValue(@"A\B", "", RegistryValueKind.String, "default");

        Assert.Equal("héllo ✓", client.GetValue(@"A\B", "s")!.Data);
        Assert.Equal(42u, client.GetValue(@"A\B", "n")!.Data);
        Assert.Equal(new[] { "a", "b" }, (string[])client.GetValue(@"A\B", "m")!.Data!);
        Assert.Equal("default", client.GetValue(@"A\B", "")!.Data);
        Assert.Null(client.GetValue(@"A\B", "missing"));
        Assert.Null(client.GetValue("Nope", "x"));
        Assert.Equal(new[] { "", "g", "m", "n", "s" }, client.GetValues(@"A\B").Select(v => v.Name));
        Assert.Equal(new[] { "B" }, client.GetSubKeyNames("A"));

        var info = client.GetKeyInfo(@"A\B");
        Assert.Equal(5, info.ValueCount);
        Assert.Equal(PosixPermissions.KeyDefault, info.Permissions.Mode);

        client.SetKeyFlags("A", RegistryKeyFlags.Hidden);
        Assert.Equal(RegistryKeyFlags.Hidden, client.GetKeyInfo("A").Flags);

        var me = RegistryIdentity.Current;
        client.SetValuePermissions(@"A\B", "n", new PosixPermissions(me.Uid, me.Gid, M("600")));
        Assert.Equal(M("600"), client.GetValuePermissions(@"A\B", "n")!.Value.Mode);
        Assert.Null(client.GetValuePermissions(@"A\B", "missing"));
        client.SetKeyPermissions("A", new PosixPermissions(me.Uid, me.Gid, M("750")));
        Assert.Equal(M("750"), client.GetKeyPermissions("A").Mode);

        Assert.True(client.DeleteValue(@"A\B", "s"));
        Assert.False(client.DeleteValue(@"A\B", "s"));
        Assert.True(client.DeleteKey("A"));
        Assert.False(client.DeleteKey("A"));

        // and it all really happened in the daemon's hive
        Assert.False(daemon.Hive.KeyExists("A"));
    }

    [Fact]
    public void Every_value_kind_survives_the_trip()
    {
        using var daemon = new Daemon();
        using var client = daemon.Connect();
        var samples = new (RegistryValueKind, object?)[]
        {
            (RegistryValueKind.None, null), (RegistryValueKind.ExpandString, "%HOME%"), (RegistryValueKind.Binary, new byte[] { 0, 255, 7 }),
            (RegistryValueKind.DWordBigEndian, 0x01020304u), (RegistryValueKind.Link, "x"), (RegistryValueKind.QWord, ulong.MaxValue),
            (RegistryValueKind.Bool, true), (RegistryValueKind.Int32, -5), (RegistryValueKind.Int64, long.MinValue),
            (RegistryValueKind.Float, 1.5f), (RegistryValueKind.Double, Math.PI), (RegistryValueKind.Decimal, -1.25m),
            (RegistryValueKind.Date, new DateOnly(2026, 10, 11)), (RegistryValueKind.Time, new TimeOnly(1, 2, 3)),
            (RegistryValueKind.DateTime, new DateTime(2026, 10, 11, 4, 54, 0, DateTimeKind.Utc)),
            (RegistryValueKind.Json, "{}"), (RegistryValueKind.Yaml, "a: 1"), (RegistryValueKind.Xml, "<a/>"),
        };

        foreach (var (kind, data) in samples)
        {
            client.SetValue("K", kind.ToString(), kind, data);
            var back = client.GetValue("K", kind.ToString())!;
            Assert.Equal(kind, back.Kind);
            if (data is Array expected) Assert.Equal(expected, (Array)back.Data!); else Assert.Equal(data, back.Data);
        }
    }

    [Fact]
    public void Errors_arrive_as_the_exceptions_a_local_hive_throws()
    {
        using var daemon = new Daemon();
        using var client = daemon.Connect();

        Assert.Throws<KeyNotFoundException>(() => client.GetSubKeyNames("Missing"));
        Assert.Throws<KeyNotFoundException>(() => client.GetKeyPermissions("Missing"));
        Assert.Throws<ArgumentException>(() => client.CreateKey(@"A\\B"));                  // empty segment
        Assert.Throws<ArgumentException>(() => client.DeleteKey(""));                        // the root cannot be deleted
        Assert.Throws<ArgumentException>(() => client.SetValue("K", "v", RegistryValueKind.DWord, "text"));   // caught before sending
    }

    [Fact]
    public void Permissions_are_enforced_against_each_connections_identity()
    {
        using var daemon = ThreeUsers();
        using var alice = daemon.Connect();
        using var bob = daemon.Connect();
        using var carol = daemon.Connect();
        Assert.Equal(1000u, alice.Uid);
        Assert.Equal(1001u, bob.Uid);

        alice.SetValue(@"Mine\Inner", "secret", RegistryValueKind.String, "s");
        alice.SetKeyPermissions("Mine", new PosixPermissions(1000, 1000, M("700")));
        alice.SetValue("Shared", "v", RegistryValueKind.String, "ok");
        alice.SetKeyPermissions("Shared", new PosixPermissions(1000, 100, M("775")));   // group 100 may write

        Assert.Equal("s", alice.GetValue(@"Mine\Inner", "secret")!.Data);
        Assert.Throws<UnauthorizedAccessException>(() => bob.GetValue(@"Mine\Inner", "secret"));
        Assert.Throws<UnauthorizedAccessException>(() => carol.GetSubKeyNames("Mine"));
        Assert.False(bob.KeyExists(@"Mine\Inner"));

        bob.SetValue("Shared", "bobs", RegistryValueKind.String, "b");                  // same group: allowed
        Assert.Equal(new PosixPermissions(1001, 1001, M("644")), bob.GetValuePermissions("Shared", "bobs"));   // owned by Bob
        Assert.Throws<UnauthorizedAccessException>(() => carol.SetValue("Shared", "carols", RegistryValueKind.String, "c"));
        Assert.Throws<UnauthorizedAccessException>(() => bob.SetKeyPermissions("Mine", new PosixPermissions(1001, 1001, M("777"))));   // not the owner
    }

    [Fact]
    public async Task Changes_made_by_one_client_are_pushed_to_another_but_only_where_it_may_look()
    {
        using var daemon = ThreeUsers();
        using var alice = daemon.Connect();   // identities are handed out in connect order: Alice, Bob, Carol
        using var bob = daemon.Connect();
        using var carol = daemon.Connect();
        var bobSeen = new List<RegistryChange>();
        var carolSeen = new List<RegistryChange>();
        bob.Changed += (_, c) => { lock (bobSeen) bobSeen.Add(c); };
        carol.Changed += (_, c) => { lock (carolSeen) carolSeen.Add(c); };

        alice.CreateKey(@"Private\Deep");
        alice.SetKeyPermissions("Private", new PosixPermissions(1000, 1000, M("700")));   // from now on closed to everyone but Alice
        alice.SetValue("Marker1", "v", RegistryValueKind.Bool, true);
        WaitFor(() => { lock (carolSeen) return carolSeen.Any(c => c.KeyPath == "Marker1"); }, "Carol to catch up");
        WaitFor(() => { lock (bobSeen) return bobSeen.Any(c => c.KeyPath == "Marker1"); }, "Bob to catch up");
        int carolBefore, bobBefore;
        lock (carolSeen) carolBefore = carolSeen.Count;
        lock (bobSeen) bobBefore = bobSeen.Count;

        // Writes inside the closed key must not be announced to anyone who cannot look into it.
        alice.SetValue(@"Private\Deep", "secret", RegistryValueKind.String, "s");
        alice.SetValue("Marker2", "v", RegistryValueKind.Bool, true);   // a visible change after it: once this arrives, the secret one has been dealt with
        WaitFor(() => { lock (carolSeen) return carolSeen.Any(c => c.KeyPath == "Marker2"); }, "Carol to hear Marker2");
        WaitFor(() => { lock (bobSeen) return bobSeen.Any(c => c.KeyPath == "Marker2"); }, "Bob to hear Marker2");

        lock (carolSeen) Assert.DoesNotContain(carolSeen.Skip(carolBefore), c => c.KeyPath.StartsWith("Private"));
        lock (bobSeen) Assert.DoesNotContain(bobSeen.Skip(bobBefore), c => c.KeyPath.StartsWith("Private"));
        await Task.CompletedTask;
    }

    [Fact]
    public void Changes_made_to_the_hive_file_by_another_process_reach_subscribed_clients()
    {
        using var daemon = new Daemon(watch: true);
        using var client = daemon.Connect();
        var seen = new List<RegistryChange>();
        client.Changed += (_, c) => { lock (seen) seen.Add(c); };

        using (var other = Hive.OpenShared("HKCU", daemon.HivePath, watch: false))   // a tool that edits the file directly
            other.SetValue(@"Software\Vde", "Theme", RegistryValueKind.String, "simple");

        WaitFor(() => { lock (seen) return seen.Any(c => c.Kind == RegistryChangeKind.ValueSet && c.ValueName == "Theme"); }, "the external change");
        Assert.Equal("simple", client.GetValue(@"Software\Vde", "Theme")!.Data);
    }

    [Fact]
    public void A_read_only_hive_can_be_read_but_every_write_is_denied()
    {
        using var daemon = new Daemon(readOnly: true);
        using var client = daemon.Connect();

        Assert.True(client.IsReadOnly);
        Assert.Equal(true, client.GetValue("Seed", "v")!.Data);
        Assert.Throws<UnauthorizedAccessException>(() => client.SetValue("Seed", "v", RegistryValueKind.Bool, false));
        Assert.Throws<UnauthorizedAccessException>(() => client.CreateKey("New"));
    }

    [Fact]
    public void Several_clients_writing_at_once_lose_nothing()
    {
        using var daemon = new Daemon();
        const int clients = 8, each = 40;

        Parallel.For(0, clients, c =>
        {
            using var client = daemon.Connect();
            for (int i = 0; i < each; i++)
                client.SetValue($"C{c}", $"v{i}", RegistryValueKind.Int32, i);
        });

        using var check = daemon.Connect();
        for (int c = 0; c < clients; c++)
            Assert.Equal(each, check.GetValues($"C{c}").Count);
    }

    [Fact]
    public void A_client_keeps_working_after_the_daemon_restarts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fhive-restart-" + Guid.NewGuid().ToString("N"));
        var first = new Daemon(dir: dir, keepFiles: true);
        try
        {
            using var client = first.Connect();
            client.SetValue("K", "v", RegistryValueKind.String, "before");
            first.Dispose();   // the daemon goes away (and its socket file with it)

            Assert.ThrowsAny<IOException>(() => { for (int i = 0; i < 100; i++) { client.GetValue("K", "v"); Thread.Sleep(20); } });

            using var second = new Daemon(dir: dir);   // and comes back
            Assert.Equal("before", client.GetValue("K", "v")!.Data);   // the hive file kept the data; the client reconnects by itself
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_socket_gets_exactly_the_requested_permission_and_stale_sockets_are_replaced_live_ones_refused()
    {
        using var daemon = new Daemon(socketMode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                                                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);   // 0666, as the system daemon uses

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                     | UnixFileMode.OtherRead | UnixFileMode.OtherWrite, File.GetUnixFileMode(daemon.Socket));
        Assert.Throws<IOException>(() => new HiveServer(daemon.Hive).Listen(daemon.Socket, UnixFileMode.UserRead | UnixFileMode.UserWrite));   // someone is listening

        var stale = Path.Combine(daemon.Dir, "stale.sock");
        File.WriteAllText(stale, "left behind by a daemon that died");
        using var server = new HiveServer(daemon.Hive);
        server.Listen(stale, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // replaces it
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(stale));
    }

    [Fact]
    public void A_client_that_is_not_allowed_is_turned_away()
    {
        using var daemon = new Daemon([Alice, Bob], accept: id => id.Uid == 1000);

        using var alice = daemon.Connect();
        Assert.Throws<IOException>(() => daemon.Connect());   // Bob: the daemon closes the connection
        alice.SetValue("K", "v", RegistryValueKind.Bool, true);   // and Alice is unaffected
    }

    [Fact]
    public void An_ordinary_registry_consumer_works_over_the_socket()
    {
        using var daemon = new Daemon();
        using var client = daemon.Connect();
        var hkcr = new ClassesRoot(client);

        hkcr.SetValue(".thing", "Content Type", RegistryValueKind.String, "application/x-thing");
        Assert.Equal("application/x-thing", hkcr.GetValue(".thing", "Content Type")!.Data);
        Assert.Equal("application/x-thing", new ClassesRoot(daemon.Hive).GetValue(".thing", "Content Type")!.Data);
    }
}

public class ProtocolRobustnessTests
{
    // Talks raw bytes to the daemon, to see that nothing a client sends can hurt it.
    private static byte[] Bytes(params byte[] bytes) => bytes;

    private static byte[] Frame(byte opcode, uint id, byte[] body) => WireWriter.Frame(opcode, id, body);

    private static bool ServerClosed(Socket socket)
    {
        socket.ReceiveTimeout = 5000;
        try
        {
            var buffer = new byte[256];
            while (true)
                if (socket.Receive(buffer) == 0)
                    return true;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static Socket Connect(Daemon daemon)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(daemon.Socket));
        return socket;
    }

    [Fact]
    public void Garbage_and_protocol_violations_close_only_that_connection()
    {
        using var daemon = new Daemon();
        using var healthy = daemon.Connect();

        var attacks = new (string name, byte[] data)[]
        {
            ("absurd length", Bytes(0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3)),
            ("zero length", Bytes(0, 0, 0, 0)),
            ("a request before hello", Frame(Wire.KeyExists, 1, new WireWriter().String("A").ToArray())),
            ("an unknown opcode after hello", Frame(Wire.Hello, 1, new WireWriter().U16(Wire.Version).ToArray())
                .Concat(Frame(0x77, 2, [])).ToArray()),
            ("a truncated body", Frame(Wire.Hello, 1, new WireWriter().U16(Wire.Version).ToArray())
                .Concat(Frame(Wire.GetValue, 2, [5, 0, 0, 0, 65])).ToArray()),
        };

        foreach (var (name, data) in attacks)
        {
            using var socket = Connect(daemon);
            socket.Send(data);
            // either the daemon answers with an error and carries on, or it hangs up: it must never hang or crash
            socket.ReceiveTimeout = 3000;
            try { socket.Receive(new byte[256]); } catch (SocketException) { }
            Assert.True(healthy.KeyExists("Anything") == false, $"the daemon stopped serving after: {name}");
        }
    }

    [Fact]
    public void A_wrong_protocol_version_is_refused_with_a_clear_error()
    {
        using var daemon = new Daemon();
        using var socket = Connect(daemon);

        socket.Send(Frame(Wire.Hello, 1, new WireWriter().U16(999).ToArray()));

        var buffer = new byte[512];
        socket.ReceiveTimeout = 5000;
        int n = socket.Receive(buffer);
        Assert.True(n > 9);
        Assert.Equal(Wire.Reply, buffer[4]);
        Assert.Equal(Wire.Unsupported, buffer[9]);   // status byte after the 9-byte header
        Assert.True(ServerClosed(socket));
    }

    [Fact]
    public void A_half_sent_frame_followed_by_a_disconnect_does_not_leak_the_connection()
    {
        using var daemon = new Daemon();
        using (var socket = Connect(daemon))
        {
            socket.Send(Bytes(100, 0, 0, 0, 1, 2, 3));   // promises 100 bytes, sends 3, vanishes
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (daemon.Server.ConnectionCount != 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Assert.Equal(0, daemon.Server.ConnectionCount);
    }

    [Fact]
    public void Values_that_do_not_decode_are_rejected_not_stored()
    {
        using var daemon = new Daemon();
        using var socket = Connect(daemon);
        var hello = Frame(Wire.Hello, 1, new WireWriter().U16(Wire.Version).ToArray());
        // a DWord whose data is 3 bytes long
        var bad = Frame(Wire.SetValue, 2, new WireWriter().String("K").String("v").U16((ushort)RegistryValueKind.DWord).Bytes([1, 2, 3]).ToArray());

        socket.Send(hello.Concat(bad).ToArray());
        socket.ReceiveTimeout = 5000;
        Thread.Sleep(300);

        Assert.False(daemon.Hive.KeyExists("K"));
    }
}
