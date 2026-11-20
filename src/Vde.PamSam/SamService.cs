using Vibe.Registry;
using Vibe.Registry.Remote;

namespace Vde.PamSam;

// PAMSAM: serves HKLM\SAM. The SAM hive is filled from the account files (SamBuilder), refilled whenever they
// change, and served with the registry daemon's own protocol on a socket, so the permissions in the hive are
// applied to each client by its real uid and groups. Clients see it as a key of HKLM (the registry library
// mounts the socket there).
public sealed class SamService : IDisposable
{
    public const string DefaultSocketPath = "/run/pamsam/sam.sock";

    private readonly GeneratedHiveService _service;
    private readonly string[] _files;

    public SamService(AccountDirectory? accounts = null, string passwd = "/etc/passwd", string group = "/etc/group", string shadow = "/etc/shadow",
        Func<System.Net.Sockets.Socket, RegistryIdentity>? identityOf = null)
    {
        var directory = accounts ?? new AccountDirectory(passwd, group);
        _files = [passwd, group, shadow];
        _service = new GeneratedHiveService("SAM", hive => SamBuilder.Fill(hive, directory, shadow), identityOf);
    }

    public Hive Hive => _service.Hive;
    public HiveServer Server => _service.Server;

    public void Refresh() => _service.Refresh();

    // The account files change by being replaced (vipw, useradd, passwd), so their folder is watched.
    public void Watch() => _service.Watch(_files);

    public void Dispose() => _service.Dispose();
}
