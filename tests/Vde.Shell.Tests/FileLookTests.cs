using System.Security.Cryptography;
using System.Text;
using Vde.Freedesktop;
using Vde.Shell;
using Xunit;

namespace Vde.Shell.Tests;

public sealed class FileLookTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("filelook").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void LauncherUsesItsOwnIconAndName()
    {
        var path = Path.Combine(_dir, "app.desktop");
        File.WriteAllText(path, "[Desktop Entry]\nType=Application\nName=My App\nIcon=firefox\nExec=true\n");
        var look = FileLook.Launcher(path, []);
        Assert.Equal("My App", look!.Name);
        Assert.Equal("firefox", look.IconNames![0]);
        Assert.False(look.Hidden);
    }

    [Fact]
    public void HiddenLauncherIsFlagged_AndNonLaunchersAreNull()
    {
        var path = Path.Combine(_dir, "gone.desktop");
        File.WriteAllText(path, "[Desktop Entry]\nType=Application\nName=X\nHidden=true\n");
        Assert.True(FileLook.Launcher(path, [])!.Hidden);
        var text = Path.Combine(_dir, "a.txt");
        File.WriteAllText(text, "hi");
        Assert.Null(FileLook.Launcher(text, []));
        var broken = Path.Combine(_dir, "bad.desktop");
        File.WriteAllText(broken, "nonsense");
        Assert.Null(FileLook.Launcher(broken, []));
    }

    [Fact]
    public void ThumbnailIsFoundByUriHash()
    {
        var cache = Path.Combine(_dir, "cache");
        var xdg = new XdgDirectories(n => n == "XDG_CACHE_HOME" ? cache : n == "HOME" ? _dir : null);
        var image = Path.Combine(_dir, "pic.png");
        File.WriteAllText(image, "x");
        Assert.Null(FileLook.Thumbnail(image, xdg));
        var name = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(new Uri(image).AbsoluteUri))).ToLowerInvariant() + ".png";
        Directory.CreateDirectory(Path.Combine(cache, "thumbnails", "normal"));
        var thumb = Path.Combine(cache, "thumbnails", "normal", name);
        File.WriteAllText(thumb, "t");
        Assert.Equal(thumb, FileLook.Thumbnail(image, xdg));
        File.SetLastWriteTimeUtc(image, DateTime.UtcNow.AddHours(1));   // the picture changed after the thumbnail: stale
        Assert.Null(FileLook.Thumbnail(image, xdg));
    }
}
