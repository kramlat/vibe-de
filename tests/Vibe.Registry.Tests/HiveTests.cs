using Vibe.Registry;

namespace Vibe.Registry.Tests;

public class HiveTests
{
    public static IEnumerable<object?[]> AllKinds() => new[]
    {
        new object?[] { RegistryValueKind.None, null },
        new object?[] { RegistryValueKind.String, "héllo wörld ✓" },
        new object?[] { RegistryValueKind.ExpandString, "%HOME%/x" },
        new object?[] { RegistryValueKind.Binary, new byte[] { 0, 1, 2, 255 } },
        new object?[] { RegistryValueKind.DWord, 0xDEADBEEFu },
        new object?[] { RegistryValueKind.DWordBigEndian, 0x01020304u },
        new object?[] { RegistryValueKind.Link, "Software\\Other" },
        new object?[] { RegistryValueKind.MultiString, new[] { "a", "bb", "ccc" } },
        new object?[] { RegistryValueKind.ResourceList, new byte[] { 9 } },
        new object?[] { RegistryValueKind.FullResourceDescriptor, new byte[] { 8 } },
        new object?[] { RegistryValueKind.ResourceRequirementsList, new byte[] { 7 } },
        new object?[] { RegistryValueKind.QWord, 0x0123456789ABCDEFul },
        new object?[] { RegistryValueKind.Bool, true },
        new object?[] { RegistryValueKind.Int32, -42 },
        new object?[] { RegistryValueKind.Int64, long.MinValue },
        new object?[] { RegistryValueKind.Float, 1.5f },
        new object?[] { RegistryValueKind.Double, Math.PI },
        new object?[] { RegistryValueKind.Decimal, -123456.789m },
        new object?[] { RegistryValueKind.Date, new DateOnly(2026, 10, 11) },
        new object?[] { RegistryValueKind.Time, new TimeOnly(13, 37, 1, 500) },
        new object?[] { RegistryValueKind.DateTime, new DateTime(2026, 10, 11, 4, 54, 0, DateTimeKind.Utc) },
        new object?[] { RegistryValueKind.Guid, Guid.Parse("6f1f1f4e-8d3b-4a53-9c3f-0e0d6b1a2c3d") },
        new object?[] { RegistryValueKind.Json, "{\"a\":[1,2]}" },
        new object?[] { RegistryValueKind.Yaml, "a: 1\n" },
        new object?[] { RegistryValueKind.Xml, "<a/>" },
    };

    private static Hive RoundTrip(Hive hive)
    {
        var ms = new MemoryStream();
        hive.Write(ms);
        ms.Position = 0;
        return Hive.Read(hive.Name, ms);
    }

    private static void AssertSameData(object? expected, object? actual)
    {
        if (expected is Array e)
            Assert.Equal(e, (Array)actual!);
        else
            Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Fhive_round_trips_every_kind(RegistryValueKind kind, object? data)
    {
        var hive = new Hive("t");
        hive.SetValue("A\\B", "v", kind, data);

        var back = RoundTrip(hive).GetValue("A\\B", "v");

        Assert.NotNull(back);
        Assert.Equal(kind, back.Kind);
        AssertSameData(data, back.Data);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Reg_file_round_trips_every_kind(RegistryValueKind kind, object? data)
    {
        var hive = new Hive("t");
        hive.SetValue("A\\B", "v", kind, data);
        hive.SetValue("A\\B", "", RegistryValueKind.String, "default");

        var ms = new MemoryStream();
        RegFile.FromHive(hive, "", "HKEY_CURRENT_USER").Save(ms);
        ms.Position = 0;
        var other = new Hive("t2");
        var applied = RegFile.Load(ms).ApplyTo(other, "HKEY_CURRENT_USER");

        Assert.Equal(3, applied); // root, A, A\B
        var back = other.GetValue("A\\B", "v");
        Assert.NotNull(back);
        Assert.Equal(kind, back.Kind);
        AssertSameData(data, back.Data);
        Assert.Equal("default", other.GetValue("A\\B", "")!.Data);
    }

    [Fact]
    public void Paths_are_case_insensitive_and_tolerate_edge_slashes()
    {
        var hive = new Hive("t");
        hive.SetValue("\\Software\\Vibe\\", "Theme", RegistryValueKind.String, "dark");

        Assert.Equal("dark", hive.GetValue("software\\VIBE", "THEME")!.Data);
        Assert.True(hive.KeyExists("SOFTWARE"));
        Assert.False(hive.KeyExists("Software\\Nope"));
    }

    [Fact]
    public void Empty_path_segment_is_rejected()
        => Assert.Throws<ArgumentException>(() => new Hive("t").CreateKey("A\\\\B"));

    [Fact]
    public void Wrong_data_type_is_rejected()
        => Assert.Throws<ArgumentException>(() => new Hive("t").SetValue("A", "v", RegistryValueKind.DWord, "text"));

    [Fact]
    public void MultiString_rejects_empty_entries()
        => Assert.Throws<ArgumentException>(() => new RegistryValue("v", RegistryValueKind.MultiString, new[] { "a", "" }));

    [Fact]
    public void Returned_arrays_are_copies()
    {
        var hive = new Hive("t");
        var input = new byte[] { 1, 2, 3 };
        hive.SetValue("A", "v", RegistryValueKind.Binary, input);
        input[0] = 99;

        var first = (byte[])hive.GetValue("A", "v")!.Data!;
        first[1] = 99;

        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])hive.GetValue("A", "v")!.Data!);
    }

    [Fact]
    public void Delete_key_removes_subtree_and_root_cannot_be_deleted()
    {
        var hive = new Hive("t");
        hive.SetValue("A\\B\\C", "v", RegistryValueKind.Bool, true);

        Assert.True(hive.DeleteKey("A\\B"));
        Assert.False(hive.KeyExists("A\\B\\C"));
        Assert.False(hive.DeleteKey("A\\B"));
        Assert.Throws<ArgumentException>(() => hive.DeleteKey(""));
        Assert.Throws<KeyNotFoundException>(() => hive.GetSubKeyNames("Nope"));
    }

    [Fact]
    public void Changed_events_fire_once_per_real_change()
    {
        var hive = new Hive("t");
        var seen = new List<RegistryChange>();
        hive.Changed += (_, c) => seen.Add(c);

        hive.SetValue("A\\B", "v", RegistryValueKind.Int32, 1);
        hive.SetValue("A\\B", "v", RegistryValueKind.Int32, 1);   // identical: no event
        hive.SetValue("A\\B", "v", RegistryValueKind.Int32, 2);
        hive.DeleteValue("A\\B", "v");
        hive.DeleteKey("A");

        Assert.Equal(new[]
        {
            new RegistryChange(RegistryChangeKind.KeyCreated, "A"),
            new RegistryChange(RegistryChangeKind.KeyCreated, "A\\B"),
            new RegistryChange(RegistryChangeKind.ValueSet, "A\\B", "v"),
            new RegistryChange(RegistryChangeKind.ValueSet, "A\\B", "v"),
            new RegistryChange(RegistryChangeKind.ValueDeleted, "A\\B", "v"),
            new RegistryChange(RegistryChangeKind.KeyDeleted, "A"),
        }, seen);
        Assert.True(seen[2].IsUnder("a"));
        Assert.False(seen[2].IsUnder("A\\C"));
    }

    [Fact]
    public void Volatile_keys_are_not_saved()
    {
        var hive = new Hive("t");
        hive.SetValue("Keep", "v", RegistryValueKind.Bool, true);
        hive.SetValue("Run\\Deep", "v", RegistryValueKind.Bool, true);
        hive.SetKeyFlags("Run", RegistryKeyFlags.Volatile);

        var back = RoundTrip(hive);

        Assert.True(back.KeyExists("Keep"));
        Assert.False(back.KeyExists("Run"));
    }

    [Fact]
    public void Corrupt_files_are_rejected()
    {
        var hive = new Hive("t");
        hive.SetValue("A", "v", RegistryValueKind.String, "hello");
        var ms = new MemoryStream();
        hive.Write(ms);
        var good = ms.ToArray();

        Assert.Throws<InvalidDataException>(() => Hive.Read("t", new MemoryStream(good[..10])));
        Assert.Throws<InvalidDataException>(() => Hive.Read("t", new MemoryStream(good[..^1])));

        var flipped = (byte[])good.Clone();
        flipped[^1] ^= 0xFF;   // checksum no longer matches
        Assert.Throws<InvalidDataException>(() => Hive.Read("t", new MemoryStream(flipped)));

        var badMagic = (byte[])good.Clone();
        badMagic[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => Hive.Read("t", new MemoryStream(badMagic)));
    }

    [Fact]
    public void Save_and_load_use_the_file_and_clear_dirty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vibe-hive-{Guid.NewGuid():N}.fhive");
        try
        {
            var hive = Hive.Load("t", path);   // missing file: empty hive bound to path
            Assert.False(hive.IsDirty);

            hive.SetValue("A", "v", RegistryValueKind.QWord, 7ul);
            Assert.True(hive.IsDirty);
            hive.Save();
            Assert.False(hive.IsDirty);
            Assert.False(File.Exists(path + ".tmp"));

            Assert.Equal(7ul, Hive.Load("t", path).GetValue("A", "v")!.Data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Concurrent_writers_do_not_lose_updates()
    {
        var hive = new Hive("t");
        Parallel.For(0, 8, t =>
        {
            for (int i = 0; i < 200; i++)
                hive.SetValue($"T{t}\\K{i}", "v", RegistryValueKind.Int32, i);
        });

        for (int t = 0; t < 8; t++)
            Assert.Equal(200, hive.GetSubKeyNames($"T{t}").Count);
    }

    [Fact]
    public void Reads_a_hand_written_regedit_file()
    {
        const string text = """
            Windows Registry Editor Version 5.00

            ; a comment
            [HKEY_CURRENT_USER\Software\Demo]
            @="default"
            "Path"="C:\\Program Files\\Demo"
            "Quote"="say \"hi\""
            "Count"=dword:0000002a
            "Blob"=hex:01,02,03,\
              04,05
            "Multi"=hex(7):61,00,00,00,62,00,00,00,00,00
            "Big"=hex(b):01,00,00,00,00,00,00,00
            "Gone"=-

            [HKEY_LOCAL_MACHINE\Other]
            "x"=dword:00000001

            [-HKEY_CURRENT_USER\Software\Old]
            """;
        var hive = new Hive("t");
        hive.SetValue("Software\\Demo", "Gone", RegistryValueKind.Bool, true);
        hive.SetValue("Software\\Old", "v", RegistryValueKind.Bool, true);

        var applied = RegFile.Parse(text).ApplyTo(hive, "HKEY_CURRENT_USER");

        Assert.Equal(2, applied);   // HKLM section skipped
        Assert.Equal("default", hive.GetValue("Software\\Demo", "")!.Data);
        Assert.Equal("C:\\Program Files\\Demo", hive.GetValue("Software\\Demo", "Path")!.Data);
        Assert.Equal("say \"hi\"", hive.GetValue("Software\\Demo", "Quote")!.Data);
        Assert.Equal(42u, hive.GetValue("Software\\Demo", "Count")!.Data);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, (byte[])hive.GetValue("Software\\Demo", "Blob")!.Data!);
        Assert.Equal(new[] { "a", "b" }, (string[])hive.GetValue("Software\\Demo", "Multi")!.Data!);
        Assert.Equal(1ul, hive.GetValue("Software\\Demo", "Big")!.Data);
        Assert.Null(hive.GetValue("Software\\Demo", "Gone"));
        Assert.False(hive.KeyExists("Software\\Old"));
    }

    [Theory]
    [InlineData("not a reg file")]
    [InlineData("Windows Registry Editor Version 5.00\n\"x\"=dword:1")]                     // value before section
    [InlineData("Windows Registry Editor Version 5.00\n[K]\n\"x\"=dword:zz")]
    [InlineData("Windows Registry Editor Version 5.00\n[K]\n\"x\"=hex(4):01,02")]           // dword needs 4 bytes
    [InlineData("Windows Registry Editor Version 5.00\n[K]\n\"unterminated=dword:1")]
    public void Malformed_reg_files_throw_FormatException(string text)
        => Assert.Throws<FormatException>(() => RegFile.Parse(text));

    [Fact]
    public void Written_reg_file_has_regedit_framing()
    {
        var hive = new Hive("t");
        hive.SetValue("A", "Blob", RegistryValueKind.Binary, Enumerable.Range(0, 60).Select(i => (byte)i).ToArray());
        var ms = new MemoryStream();
        RegFile.FromHive(hive, "A", "HKEY_CURRENT_USER").Save(ms);
        var bytes = ms.ToArray();

        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);   // UTF-16 LE BOM
        var text = System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        Assert.StartsWith("Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\A]\r\n\"Blob\"=hex:00,01", text);
        Assert.All(text.Split("\r\n"), l => Assert.True(l.Length <= 80, l));
    }
}
