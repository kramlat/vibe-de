using Vde.Shell.ComDlg;

namespace Vde.Shell.ComDlg.Tests;

public sealed class Dir : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "comdlg-" + Guid.NewGuid().ToString("N"));
    public Dir()
    {
        Directory.CreateDirectory(Path.Combine(Root, "docs", "sub"));
        File.WriteAllText(Path.Combine(Root, "docs", "a.txt"), "a");
        File.WriteAllText(Path.Combine(Root, "docs", "b b.txt"), "b");
        File.WriteAllText(Path.Combine(Root, "docs", "pic.png"), "p");
    }
    public string Docs => Path.Combine(Root, "docs");
    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public class RulesTests
{
    [Fact]
    public void Filters_parse_match_case_insensitively_and_give_a_default_extension()
    {
        var filters = FileFilter.ParseList("Text files|*.txt;*.md|All files|*|dangling name");
        Assert.Equal(new[] { "Text files", "All files" }, filters.Select(f => f.Name));
        Assert.True(filters[0].Matches("Notes.TXT"));
        Assert.True(filters[0].Matches("readme.md"));
        Assert.False(filters[0].Matches("pic.png"));
        Assert.True(filters[1].Matches("anything.at.all"));
        Assert.Equal("txt", filters[0].DefaultExtension);
        Assert.Null(filters[1].DefaultExtension);
        Assert.True(new FileFilter("x", ["data?.bin"]).Matches("data1.bin"));
        Assert.Empty(FileFilter.ParseList(""));
    }

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("\"b b.txt\"", "b b.txt")]
    public void A_single_name_may_be_quoted(string typed, string expected)
        => Assert.Equal(new[] { expected }, FileDialogRules.SplitNames(typed));

    [Fact]
    public void Several_names_are_split_on_spaces_outside_quotes()
        => Assert.Equal(new[] { "a.txt", "b b.txt", "c" }, FileDialogRules.SplitNames("a.txt \"b b.txt\"  c"));

    [Theory]
    [InlineData("", "Type a file name.")]
    [InlineData("..", "not a valid file name")]
    [InlineData("a/b", "cannot contain")]
    public void Bad_names_are_explained(string name, string expected)
        => Assert.Contains(expected, FileDialogRules.NameProblem(name));

    [Fact]
    public void Names_longer_than_the_filesystem_allows_are_refused()
    {
        Assert.Null(FileDialogRules.NameProblem(new string('a', 255)));
        Assert.Contains("too long", FileDialogRules.NameProblem(new string('a', 256)));
        Assert.Contains("too long", FileDialogRules.NameProblem(new string('é', 128)));   // 256 bytes in UTF-8
    }

    [Fact]
    public void Open_accepts_existing_files_navigates_into_folders_and_refuses_the_rest()
    {
        using var d = new Dir();
        var single = new FileDialogOptions();
        var multi = new FileDialogOptions { Multiselect = true };

        var ok = FileDialogRules.ResolveOpen(d.Docs, "a.txt", single);
        Assert.Equal(Resolve.Accept, ok.Kind);
        Assert.Equal(new[] { Path.Combine(d.Docs, "a.txt") }, ok.Paths);
        Assert.Equal(Resolve.Navigate, FileDialogRules.ResolveOpen(d.Docs, "sub", single).Kind);
        Assert.Equal(Resolve.Navigate, FileDialogRules.ResolveOpen(d.Docs, "..", single).Kind);
        Assert.Contains("not found", FileDialogRules.ResolveOpen(d.Docs, "missing.txt", single).Message);
        Assert.Contains("single file", FileDialogRules.ResolveOpen(d.Docs, "a.txt \"b b.txt\"", single).Message);
        Assert.Equal(2, FileDialogRules.ResolveOpen(d.Docs, "a.txt \"b b.txt\"", multi).Paths.Count);
        Assert.Contains("is a folder", FileDialogRules.ResolveOpen(d.Docs, "a.txt sub", multi).Message);
        Assert.Equal(Resolve.Invalid, FileDialogRules.ResolveOpen(d.Docs, "  ", single).Kind);
    }

    [Fact]
    public void Save_adds_the_default_extension_asks_before_overwriting_and_checks_the_folder()
    {
        using var d = new Dir();
        var options = new FileDialogOptions { DefaultExtension = "txt" };

        Assert.Equal(Path.Combine(d.Docs, "new.txt"), FileDialogRules.ResolveSave(d.Docs, "new", null, options).Paths[0]);
        Assert.Equal(Path.Combine(d.Docs, "new.md"), FileDialogRules.ResolveSave(d.Docs, "new.md", null, options).Paths[0]);     // an extension already there is kept
        Assert.Equal(Path.Combine(d.Docs, "x.txt"), FileDialogRules.ResolveSave(d.Docs, "x", new FileFilter("T", ["*.txt"]), new FileDialogOptions()).Paths[0]);   // from the filter

        var again = FileDialogRules.ResolveSave(d.Docs, "a.txt", null, options);
        Assert.Equal(Resolve.ConfirmOverwrite, again.Kind);
        Assert.Contains("already exists", again.Message);
        Assert.Equal(Resolve.Accept, FileDialogRules.ResolveSave(d.Docs, "a.txt", null, new FileDialogOptions { ConfirmOverwrite = false }).Kind);

        Assert.Equal(Resolve.Navigate, FileDialogRules.ResolveSave(d.Docs, "sub", null, options).Kind);
        Assert.Contains("does not exist", FileDialogRules.ResolveSave(d.Docs, "nowhere/x.txt", null, options).Message);
        Assert.Equal(Resolve.Invalid, FileDialogRules.ResolveSave(d.Docs, "\"\"", null, options).Kind);
        Assert.Equal(Path.Combine(d.Docs, "sub", "deep.txt"), FileDialogRules.ResolveSave(d.Docs, "sub/deep", null, options).Paths[0]);   // a relative path works
    }
}
