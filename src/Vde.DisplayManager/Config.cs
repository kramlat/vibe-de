using Vde.Freedesktop;
using Vibe.Registry;

namespace Vde.DisplayManager;

// HKLM\Software\Vde\DisplayManager, with the defaults that make an untouched install work.
public sealed record DmConfig
{
    public const string KeyPath = @"Software\Vde\DisplayManager";

    public int Vt { get; init; } = 1;
    public string GreeterUser { get; init; } = "vdedm";
    public string GreeterHome { get; init; } = "/var/lib/vdedm";
    // The compositor with the greeter inside: no server-side decorations, one program, no session around it.
    public IReadOnlyList<string> GreeterCommand { get; init; } = ["vwm", "-d", "-w", "wayland-vdedm", "-s", "vdedm-greeter"];
    public string SocketPath { get; init; } = "/run/vdedm/greeter.sock";
    public string PamService { get; init; } = "vdedm";
    public string GreeterPamService { get; init; } = "vdedm-greeter";
    public string AutologinPamService { get; init; } = "vdedm-autologin";
    public string DefaultSession { get; init; } = SessionDirectory.DefaultId;
    public string? AutoLoginUser { get; init; }       // set: this user is logged in at boot without a password
    public string? AutoLoginSession { get; init; }
    public bool AutoLoginOnce { get; init; } = true;   // only the first time after boot: logging out shows the greeter
    public bool Enabled { get; init; } = true;

    public static DmConfig Load(IHive? hklm)
    {
        var d = new DmConfig();
        if (hklm is null) return d;
        string? Text(string name) => hklm.GetValue(KeyPath, name) is { Kind: RegistryValueKind.String, Data: string { Length: > 0 } s } ? s : null;
        long? Number(string name) => RegistryNumber.Get(hklm, KeyPath, name);
        return d with
        {
            Vt = Number("Vt") is { } vt and >= 1 and <= 63 ? (int)vt : d.Vt,
            GreeterUser = Text("GreeterUser") ?? d.GreeterUser,
            GreeterCommand = Text("GreeterCommand") is { } c && ExecLine.Tokenize(c) is { Count: > 0 } argv ? argv : d.GreeterCommand,
            DefaultSession = Text("DefaultSession") ?? d.DefaultSession,
            AutoLoginUser = Text("AutoLoginUser"),
            AutoLoginSession = Text("AutoLoginSession"),
            AutoLoginOnce = Number("AutoLoginOnce") is not 0,
            Enabled = Number("Enabled") is not 0,
        };
    }
}
