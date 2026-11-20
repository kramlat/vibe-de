using Avalonia.Headless.XUnit;
using VExplore.Windows;
using Vibe.Registry;

namespace VExplore.Tests;

// The registry editor's model: what a value shows as in the Data column and back into data, renaming a key,
// and .reg import and export. The window tests after them run headlessly with two in-memory hives.
public class RegEditModelTests
{
    [Fact]
    public void Values_show_per_kind_and_parse_back()
    {
        Assert.Equal("hello", RegEditModel.Format(RegistryValueKind.String, "hello"));
        Assert.Equal("0x000000ff (255)", RegEditModel.Format(RegistryValueKind.DWord, 255u));
        Assert.Equal("0x00000000000000ff (255)", RegEditModel.Format(RegistryValueKind.QWord, 255ul));
        Assert.Equal("0A FF 01", RegEditModel.Format(RegistryValueKind.Binary, new byte[] { 0x0A, 0xFF, 0x01 }));
        Assert.Equal("a\nb", RegEditModel.Format(RegistryValueKind.MultiString, new[] { "a", "b" }));
        Assert.Equal("True", RegEditModel.Format(RegistryValueKind.Bool, true));
        Assert.Equal("(no data)", RegEditModel.Format(RegistryValueKind.None, null));
        Assert.Equal("0x0000001a", RegEditModel.ParseableText(RegistryValueKind.DWord, 26u));
    }

    [Fact]
    public void Parsing_takes_decimal_or_hex_numbers_hex_bytes_and_strips_blank_lines()
    {
        Assert.Equal(255u, RegEditModel.Parse(RegistryValueKind.DWord, "0xFF"));
        Assert.Equal(255u, RegEditModel.Parse(RegistryValueKind.DWord, "255"));
        Assert.Equal(0xdeadbeefu, RegEditModel.Parse(RegistryValueKind.DWord, "0xDEADBEEF"));
        Assert.Equal(255ul, RegEditModel.Parse(RegistryValueKind.QWord, "0xFF"));
        Assert.Equal(-1, RegEditModel.Parse(RegistryValueKind.Int32, "-1"));
        Assert.Equal(new byte[] { 0x0A, 0xFF, 0x01 }, RegEditModel.Parse(RegistryValueKind.Binary, "0a, ff-01"));
        Assert.Equal(Array.Empty<byte>(), RegEditModel.Parse(RegistryValueKind.Binary, ""));
        Assert.Equal(new[] { "a", "b" }, RegEditModel.Parse(RegistryValueKind.MultiString, "a\nb\n\n"));
        Assert.True((bool)RegEditModel.Parse(RegistryValueKind.Bool, "1")!);
        Assert.Equal("expand %PATH% here", RegEditModel.Parse(RegistryValueKind.ExpandString, "expand %PATH% here"));
    }

    [Fact]
    public void Bad_text_is_refused()
    {
        Assert.Throws<ArgumentException>(() => RegEditModel.Parse(RegistryValueKind.DWord, "-1"));     // unsigned
        Assert.Throws<ArgumentException>(() => RegEditModel.Parse(RegistryValueKind.DWord, "0xzz"));
        Assert.Throws<ArgumentException>(() => RegEditModel.Parse(RegistryValueKind.Binary, "0ab"));   // odd digit count
    }

    [Fact]
    public void Key_names_refuse_empty_and_backslashes()
    {
        Assert.NotNull(RegEditModel.NameProblem(""));
        Assert.NotNull(RegEditModel.NameProblem(@"a\b"));
        Assert.Null(RegEditModel.NameProblem("Fine name"));
    }

    [Fact]
    public void Renaming_moves_the_key_with_its_values_and_sub_keys_and_leaves_the_old_name_gone()
    {
        var hive = new Hive("HKCU");
        hive.SetValue(@"Software\Vendor\Tool\1.0", "Version", RegistryValueKind.DWord, 3u);
        hive.SetValue(@"Software\Vendor\Tool", "ToolValue", RegistryValueKind.String, "x");
        hive.SetValue(@"Software\Vendor", "", RegistryValueKind.String, "default");

        RegEditModel.Rename(hive, @"Software\Vendor\Tool", "Kit");

        Assert.False(hive.KeyExists(@"Software\Vendor\Tool"));
        Assert.Equal(3u, hive.GetValue(@"Software\Vendor\Kit\1.0", "Version")!.Data);
        Assert.Equal("x", hive.GetValue(@"Software\Vendor\Kit", "ToolValue")!.Data);

        hive.SetValue(@"Software\Vendor\Other", "v", RegistryValueKind.String, "y");
        Assert.Throws<ArgumentException>(() => RegEditModel.Rename(hive, @"Software\Vendor\Kit", "Other")); // the sibling: exists
        Assert.Throws<ArgumentException>(() => RegEditModel.Rename(hive, "", "x"));                     // the root
    }

    [Fact]
    public void Export_and_import_round_trip_between_the_hives_and_a_reg_file()
    {
        var user = new Hive("HKCU");
        var machine = new Hive("HKLM");
        user.SetValue(@"Software\Vde\Taskbar", "Height", RegistryValueKind.DWord, 48u);
        user.SetValue(@"Software\Vde\Taskbar", "Pinned", RegistryValueKind.MultiString, new[] { "vterm", "vexplore" });
        machine.SetValue(@"Software\Vde\Windows", "TitlebarDoubleClick", RegistryValueKind.String, "Maximize");

        var file = Path.Combine(Path.GetTempPath(), "vde-regedit-" + Guid.NewGuid().ToString("N") + ".reg");
        try
        {
            RegEditModel.Export(user, @"Software\Vde\Taskbar", file);   // the key itself only
            var text = File.ReadAllText(file);
            Assert.Contains(@"[HKEY_CURRENT_USER\Software\Vde\Taskbar]", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"Software\Vde\Windows", text, StringComparison.OrdinalIgnoreCase);

            // Import into a fresh pair of hives: the section lands under the same root.
            var user2 = new Hive("HKCU");
            var machine2 = new Hive("HKLM");
            Assert.Equal(1, RegEditModel.Import(user2, machine2, RegFile.Load(file)));
            Assert.Equal(48u, user2.GetValue(@"Software\Vde\Taskbar", "Height")!.Data);
            Assert.Equal(new[] { "vterm", "vexplore" }, user2.GetValue(@"Software\Vde\Taskbar", "Pinned")!.Data);
            Assert.False(machine2.KeyExists("Software"));

            // A machine-rooted file goes to the system hive, not the user's.
            var machineFile = Path.Combine(Path.GetTempPath(), "vde-regedit-" + Guid.NewGuid().ToString("N") + ".reg");
            try
            {
                RegEditModel.Export(machine, "Software", machineFile);
                Assert.Equal(3, RegEditModel.Import(user2, machine2, RegFile.Load(machineFile)));   // Software and all below it
                Assert.Equal("Maximize", machine2.GetValue(@"Software\Vde\Windows", "TitlebarDoubleClick")!.Data);
            }
            finally { File.Delete(machineFile); }
        }
        finally { File.Delete(file); }
    }
}

public class RegEditWindowTests
{
    [AvaloniaFact]
    public void The_tree_shows_the_roots_and_selecting_a_key_shows_its_values()
    {
        var user = new Hive("HKCU");
        var machine = new Hive("HKLM");
        user.SetValue(@"Software\Vde\Taskbar", "Height", RegistryValueKind.DWord, 48u);
        user.SetValue(@"Software\Vde\Taskbar", "", RegistryValueKind.String, "the default value");

        var window = new RegEditWindow(user, machine);
        window.Show();

        Assert.Equal([RegEditModel.UserRoot, RegEditModel.MachineRoot], window.VisiblePaths);
        Assert.True(window.SelectPath(@"HKEY_CURRENT_USER\Software\Vde\Taskbar"));
        Assert.Equal(@"HKEY_CURRENT_USER\Software\Vde\Taskbar", window.SelectedPath);
        Assert.Equal(["", "Height"], window.ShownValues);   // "" is the default value; the row shows it as "(Default)"
        Assert.Equal("0x00000030 (48)", window.ShownValueRows.First(r => r.Name == "Height").Data);

        Assert.False(window.SelectPath(@"HKEY_CURRENT_USER\Nope"));   // unknown, so the selection stays
        Assert.Equal(@"HKEY_CURRENT_USER\Software\Vde\Taskbar", window.SelectedPath);
    }

    [AvaloniaFact]
    public void Key_children_load_lazily_on_expand()
    {
        var user = new Hive("HKCU");
        var machine = new Hive("HKLM");
        user.CreateKey(@"Software\Deep\Deeper\Deepest");

        var window = new RegEditWindow(user, machine);
        window.Show();

        Assert.Equal([RegEditModel.UserRoot, RegEditModel.MachineRoot], window.VisiblePaths);   // nothing opened yet
        Assert.True(window.SelectPath(@"HKEY_CURRENT_USER\Software\Deep"));
        Assert.Equal(
            [RegEditModel.UserRoot, @"HKEY_CURRENT_USER\Software", @"HKEY_CURRENT_USER\Software\Deep", RegEditModel.MachineRoot],
            window.VisiblePaths);
    }

    [AvaloniaFact]
    public void New_key_new_value_edit_and_delete_go_through_the_hive_and_the_list()
    {
        var user = new Hive("HKCU");
        var machine = new Hive("HKLM");
        var window = new RegEditWindow(user, machine);
        window.Show();

        var created = window.CreateKey("HKEY_CURRENT_USER", "Test");
        Assert.Equal(@"HKEY_CURRENT_USER\Test", created);
        Assert.True(user.KeyExists("Test"));

        window.SetValueText(RegistryValueKind.DWord, "Count", "0x2A");
        Assert.Equal(0x2au, user.GetValue("Test", "Count")!.Data);
        Assert.Equal(["Count"], window.ShownValues);

        // A new value starts with its kind's default data, as regedit's does.
        var before = window.ShownValues;
        window.NewValue(RegistryValueKind.String, interactive: false);
        Assert.Equal("", user.GetValue("Test", "New value")!.Data);
        Assert.Equal(before.Append("New value").ToList(), window.ShownValues.ToList());

        Assert.Throws<ArgumentException>(() => window.SetValueText(RegistryValueKind.DWord, "Count", "nope"));

        window.SetValueText(RegistryValueKind.MultiString, "Lines", "one\ntwo");
        Assert.Equal(new[] { "one", "two" }, user.GetValue("Test", "Lines")!.Data);

        user.SetValue("Test", "Old", RegistryValueKind.String, "moved");
        window.RenameValue("Old", "New");
        Assert.Equal("moved", user.GetValue("Test", "New")!.Data);
        Assert.Null(user.GetValue("Test", "Old"));
    }

    [AvaloniaFact]
    public void Deleting_the_selected_key_returns_the_selection_to_its_parent()
    {
        var user = new Hive("HKCU");
        var machine = new Hive("HKLM");
        var window = new RegEditWindow(user, machine);
        window.Show();
        window.CreateKey("HKEY_CURRENT_USER", "Test");

        window.DeleteSelectedKey();
        Assert.False(user.KeyExists("Test"));
        Assert.Equal("HKEY_CURRENT_USER", window.SelectedPath);
        Assert.Equal([RegEditModel.UserRoot, RegEditModel.MachineRoot], window.VisiblePaths);
    }

    [AvaloniaFact]
    public void The_address_line_names_the_selected_key_the_regedit_way()
    {
        var user = new Hive("HKCU");
        var machine = new Hive("HKLM");
        user.CreateKey(@"Software\Vde\Taskbar");

        var window = new RegEditWindow(user, machine);
        window.Show();

        Assert.Equal("Computer\\HKEY_CURRENT_USER", window.AddressText);
        Assert.True(window.SelectPath(@"HKEY_CURRENT_USER\Software\Vde\Taskbar"));
        Assert.Equal("Computer\\HKEY_CURRENT_USER\\Software\\Vde\\Taskbar", window.AddressText);
    }

    [AvaloniaFact]
    public void Favorites_are_bookmarks_in_the_user_hive()
    {
        var user = new Hive("HKCU");
        var machine = new Hive("HKLM");
        user.CreateKey(@"Software\Vde\Taskbar");

        var window = new RegEditWindow(user, machine);
        window.Show();
        Assert.True(window.SelectPath(@"HKEY_CURRENT_USER\Software\Vde\Taskbar"));
        window.AddFavorite("Taskbar");

        Assert.Equal(@"HKEY_CURRENT_USER\Software\Vde\Taskbar",
            user.GetValue(@"Software\Vde\Regedit\Favorites", "Taskbar")!.Data);

        Assert.Throws<ArgumentException>(() => window.AddFavorite("bad\\name"));
    }
}
