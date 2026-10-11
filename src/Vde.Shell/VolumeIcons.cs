namespace Vde.Shell;

public static class VolumeIcons
{
    // freedesktop icon names, most specific first.
    public static IReadOnlyList<string> Names(this Volume volume) => volume.Kind switch
    {
        VolumeKind.Optical => ["media-optical", "drive-optical"],
        VolumeKind.Removable => ["drive-removable-media-usb", "drive-removable-media"],
        VolumeKind.Network => ["folder-remote", "network-server"],
        _ when volume.MountPoint == "/" => ["drive-harddisk-root", "drive-harddisk"],
        _ => ["drive-harddisk"],
    };

    public static IReadOnlyList<string> Names(this NetworkShare share) => ["folder-remote", "network-server"];
}
