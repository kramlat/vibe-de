using Vde.Freedesktop;

namespace VExplore.Tests;

// The shell parses the mime and desktop-entry databases once per process and only re-parses a slot after
// its lifetime lapses: they were rebuilt on every window open and every launch click before.
public class ShellDataTests
{
    [Fact]
    public void The_mime_database_is_built_once_and_handed_out_again()
    {
        var first = ShellData.Mime();
        var again = ShellData.Mime();
        Assert.Same(first, again);
    }

    [Fact]
    public void The_entry_database_is_built_once_per_locale_list()
    {
        var first = ShellData.Apps(Locale.FromEnvironment());
        var again = ShellData.Apps(Locale.FromEnvironment());
        Assert.Same(first, again);
        Assert.NotEmpty(first.Entries);                                   // the system's applications are in
        Assert.NotSame(first, ShellData.Apps([]));                        // launching's empty locale list parses its own
    }
}
