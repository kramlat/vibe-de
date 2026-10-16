using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Vde.Shell;
using Vde.Components;
using Vde.Components;
using Vde.Shell.ComDlg;

namespace Vde.Shell.ComDlg.Tests;

public class DialogTests
{
    private static KnownFolders Folders(string home)
    {
        var env = new Dictionary<string, string> { ["HOME"] = home, ["XDG_CONFIG_HOME"] = Path.Combine(home, "cfg") };
        return new KnownFolders(new Vde.Freedesktop.XdgDirectories(k => env.GetValueOrDefault(k)));
    }

    [AvaloniaTheory]
    [InlineData(MessageButtons.Ok, new[] { "Ok" }, "Ok")]
    [InlineData(MessageButtons.OkCancel, new[] { "Ok", "Cancel" }, "Cancel")]
    [InlineData(MessageButtons.YesNo, new[] { "Yes", "No" }, "No")]
    [InlineData(MessageButtons.YesNoCancel, new[] { "Yes", "No", "Cancel" }, "Cancel")]
    [InlineData(MessageButtons.RetryCancel, new[] { "Retry", "Cancel" }, "Cancel")]
    public void A_message_box_offers_the_right_buttons_and_closing_it_gives_the_cancelling_answer(MessageButtons buttons, string[] expected, string closeAnswer)
    {
        var box = new MessageBoxWindow("Really?", "Confirm", buttons, MessageIcon.Question);
        box.Show();

        Assert.Equal(expected, box.Buttons.Select(b => b.ToString()));
        box.Close();
        Assert.Equal(closeAnswer, box.Result.ToString());
    }

    [AvaloniaFact]
    public void Pressing_a_button_is_the_answer_and_the_default_button_can_be_chosen()
    {
        var box = new MessageBoxWindow("Replace it?", "Confirm", MessageButtons.YesNo, MessageIcon.Warning, DialogResult.No);
        box.Show();
        var no = box.GetVisualDescendants().OfType<Button>().Single(b => (DialogResult)b.Tag! == DialogResult.No);
        var yes = box.GetVisualDescendants().OfType<Button>().Single(b => (DialogResult)b.Tag! == DialogResult.Yes);
        Assert.True(no.IsDefault);
        Assert.False(yes.IsDefault);

        box.Press(DialogResult.Yes);
        Assert.Equal(DialogResult.Yes, box.Result);
    }

    [AvaloniaFact]
    public void Input_returns_the_text_refuses_what_validation_rejects_and_cancel_gives_null()
    {
        var window = new InputWindow("Name:", "Rename", "old", validate: t => t.Contains('/') ? "No slashes." : null);
        window.Show();
        Assert.Equal("old", window.Text);

        window.Text = "a/b";
        window.OkButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Null(window.Result);
        Assert.Contains("No slashes.", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

        window.Text = "fine";
        window.OkButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("fine", window.Result);

        var cancelled = new InputWindow("x", "x");
        cancelled.Show();
        cancelled.Close();
        Assert.Null(cancelled.Result);
    }

    [AvaloniaFact]
    public void A_password_box_hides_what_is_typed()
    {
        var window = new InputWindow("Password:", "Unlock", password: true);
        window.Show();
        Assert.Equal('•', window.GetVisualDescendants().OfType<TextBox>().Single().PasswordChar);
    }

    [AvaloniaFact]
    public void The_elevation_prompt_shows_the_exact_command_wipes_nothing_early_and_cancel_means_no()
    {
        var request = new ElevationRequest("Edit with Thing", "edit", "/f", ["/usr/bin/thing", "--edit", "my file.txt"], "it is not executable by you (mode 0644)");
        var window = new ElevationPromptWindow(request, 1, null);
        window.Show();

        Assert.Equal("/usr/bin/thing --edit 'my file.txt'", window.CommandBox.Text);
        Assert.True(window.CommandBox.IsReadOnly);
        Assert.Contains("Allow \"Edit with Thing\" to make changes?", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

        window.Accept();                                                         // no password yet: stays open and says so
        Assert.Null(window.Response);
        Assert.Contains("Type your password.", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

        window.Password = "hunter2";
        window.Accept();
        Assert.True(window.Response!.Approved);
        Assert.Equal("hunter2", new string(window.Response.Password!));
        Assert.Equal("", window.Password);                                       // the control no longer holds it

        var closed = new ElevationPromptWindow(request, 2, "Incorrect password. Try again.");
        closed.Show();
        Assert.Contains("attempt 2 of 3", closed.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).First(t => t is not null && t.StartsWith("Password")));
        closed.Close();
        Assert.False(closed.Response!.Approved);                                 // closing the window is saying no
    }

    [AvaloniaFact]
    public void Progress_follows_a_file_operation_and_cancelling_signals_its_token()
    {
        var window = new ProgressWindow("Copying");
        window.Show();

        window.Report(new FileProgress("/a/big file.iso", 50, 200, 1, 4));
        Assert.Equal(0.25, window.Fraction, 3);
        Assert.Equal("big file.iso", window.Current);
        Assert.Contains("1 of 4 items", window.Detail);

        Assert.False(window.Token.IsCancellationRequested);
        window.GetVisualDescendants().OfType<Button>().Single().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(window.IsCancelled);
    }

    [AvaloniaFact]
    public async Task Open_shows_only_files_the_filter_allows_fills_the_name_from_the_selection_and_accepts()
    {
        using var d = new Dir();
        var options = new FileDialogOptions { InitialFolder = d.Docs, Filters = FileFilter.ParseList("Text|*.txt|All|*") };
        var window = new FileDialogWindow(FileDialogMode.Open, options, Folders(d.Root));
        window.Show();

        Assert.Equal(new[] { "sub", "a.txt", "b b.txt" }, window.Entries.Select(e => e.Name));   // pic.png filtered out
        window.FilterIndex = 1;
        Assert.Contains(window.Entries, e => e.Name == "pic.png");
        window.FilterIndex = 0;

        window.List.SelectAll();
        window.FileName = "a.txt";
        await window.AcceptAsync();
        Assert.Equal(new[] { Path.Combine(d.Docs, "a.txt") }, window.Paths);
    }

    [AvaloniaFact]
    public async Task Open_navigates_into_a_typed_folder_and_explains_a_missing_file()
    {
        using var d = new Dir();
        var window = new FileDialogWindow(FileDialogMode.Open, new FileDialogOptions { InitialFolder = d.Docs }, Folders(d.Root));
        window.Show();

        window.FileName = "sub";
        await window.AcceptAsync();
        Assert.Equal(Path.Combine(d.Docs, "sub"), window.CurrentFolder);
        Assert.Null(window.Paths);                                                  // still open: it went into the folder

        window.FileName = "nope.txt";
        await window.AcceptAsync();
        Assert.Contains("was not found", window.Message);
        Assert.Null(window.Paths);
    }

    [AvaloniaFact]
    public async Task Save_asks_before_replacing_and_only_saves_on_yes()
    {
        using var d = new Dir();
        var asked = new List<string>();
        bool answer = false;
        var window = new FileDialogWindow(FileDialogMode.Save, new FileDialogOptions { InitialFolder = d.Docs, DefaultExtension = "txt" }, Folders(d.Root),
            askOverwrite: text => { asked.Add(text); return Task.FromResult(answer); });
        window.Show();

        window.FileName = "a";                                                      // becomes a.txt, which exists
        await window.AcceptAsync();
        Assert.Single(asked);
        Assert.Null(window.Paths);                                                  // answered no: nothing chosen

        answer = true;
        await window.AcceptAsync();
        Assert.Equal(new[] { Path.Combine(d.Docs, "a.txt") }, window.Paths);

        var fresh = new FileDialogWindow(FileDialogMode.Save, new FileDialogOptions { InitialFolder = d.Docs, DefaultExtension = "txt" }, Folders(d.Root), askOverwrite: _ => Task.FromResult(false));
        fresh.Show();
        fresh.FileName = "brand-new";
        await fresh.AcceptAsync();
        Assert.Equal(new[] { Path.Combine(d.Docs, "brand-new.txt") }, fresh.Paths);   // no question for a new file
    }

    [AvaloniaFact]
    public async Task The_folder_picker_lists_only_folders_and_returns_the_chosen_one_or_the_current_one()
    {
        using var d = new Dir();
        var window = new FileDialogWindow(FileDialogMode.Folder, new FileDialogOptions { InitialFolder = d.Docs }, Folders(d.Root));
        window.Show();

        Assert.Equal(new[] { "sub" }, window.Entries.Select(e => e.Name));
        window.FileName = "";
        await window.AcceptAsync();
        Assert.Equal(new[] { d.Docs }, window.Paths);                               // nothing typed: the folder you are in

        var again = new FileDialogWindow(FileDialogMode.Folder, new FileDialogOptions { InitialFolder = d.Docs }, Folders(d.Root));
        again.Show();
        again.FileName = "sub";
        await again.AcceptAsync();
        Assert.Equal(new[] { Path.Combine(d.Docs, "sub") }, again.Paths);
        again.FileName = "a.txt";                                                    // a file is not a folder
        var third = new FileDialogWindow(FileDialogMode.Folder, new FileDialogOptions { InitialFolder = d.Docs }, Folders(d.Root));
        third.Show(); third.FileName = "a.txt"; await third.AcceptAsync();
        Assert.Contains("is not a folder", third.Message);
    }
}

public class ElevationFlowTests
{
    private sealed class FakeBackend(params ElevationOutcome[] outcomes) : IElevationBackend
    {
        private readonly Queue<ElevationOutcome> _outcomes = new(outcomes);
        public List<string> Passwords { get; } = new();
        public bool CanElevate(Vibe.Registry.RegistryIdentity identity) => true;
        public Task<ElevationOutcome> RunAsync(IReadOnlyList<string> commandLine, ReadOnlyMemory<char> password, CancellationToken cancel)
        {
            Passwords.Add(new string(password.Span));
            return Task.FromResult(_outcomes.Dequeue());
        }
    }

    [AvaloniaFact]
    public async Task A_user_answering_the_dialog_drives_the_shells_whole_elevation_flow()
    {
        var shownTexts = new List<List<string>>();
        var commands = new List<string>();
        var passwords = new Queue<string?>(["wrong", "right"]);
        var prompt = new ElevationPromptDialog(() => null)
        {
            Shown = w =>
            {
                shownTexts.Add(w.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList());
                commands.Add(w.CommandBox.Text!);
                w.Password = passwords.Dequeue()!;
                w.Accept();
            },
        };
        var backend = new FakeBackend(new ElevationOutcome(ElevationStatus.WrongPassword), ElevationOutcome.Success);
        var request = new ElevationRequest("Do it", "open", "/f", ["/bin/true", "x y"], "not executable by you");

        // the shell's retry loop, as ShellExecute runs it: ask, try, ask again with the error, try again
        var first = await prompt.AskAsync(request, 1, null, CancellationToken.None);
        var outcome1 = await backend.RunAsync(request.CommandLine, first.Password!, CancellationToken.None);
        var second = await prompt.AskAsync(request, 2, "Incorrect password. Try again.", CancellationToken.None);
        var outcome2 = await backend.RunAsync(request.CommandLine, second.Password!, CancellationToken.None);

        Assert.Equal(new[] { "wrong", "right" }, backend.Passwords);
        Assert.Equal(ElevationStatus.WrongPassword, outcome1.Status);
        Assert.Equal(ElevationStatus.Success, outcome2.Status);
        Assert.Contains("Your password", shownTexts[0]);
        Assert.DoesNotContain(shownTexts[0], t => t.StartsWith("Incorrect"));
        Assert.Contains("Password (attempt 2 of 3)", shownTexts[1]);
        Assert.Contains("Incorrect password. Try again.", shownTexts[1]);   // the retry says what went wrong
        Assert.Equal("/bin/true 'x y'", commands[0]);
    }

    [AvaloniaFact]
    public async Task Cancelling_the_token_closes_the_prompt_as_a_no()
    {
        using var cancel = new CancellationTokenSource();
        var prompt = new ElevationPromptDialog(() => null) { Shown = _ => cancel.Cancel() };

        var response = await prompt.AskAsync(new ElevationRequest("x", "o", "/f", ["/bin/true"], "r"), 1, null, cancel.Token);

        Assert.False(response.Approved);
    }
}

public class ElevationLabelTests
{
    [AvaloniaTheory]
    [InlineData(null, 1, "Your password")]
    [InlineData("deck", 1, "Password for deck")]
    [InlineData("root", 2, "Password for root (attempt 2 of 3)")]
    [InlineData(null, 3, "Password (attempt 3 of 3)")]
    public void The_prompt_says_whose_password_it_wants(string? account, int attempt, string expected)
    {
        var request = new ElevationRequest("x", "o", "/f", ["/bin/true"], "r") { Account = account };
        var window = new ElevationPromptWindow(request, attempt, null);
        window.Show();

        Assert.Contains(expected, window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
    }
}

public class PropertySheetTests
{
    public sealed class Page(string title, Func<bool>? apply = null, int order = 100) : IPropertyPageProvider
    {
        public IReadOnlyList<PropertyPageInfo> GetPages(PropertyTarget target)
            => [new PropertyPageInfo(title, () => new TextBlock { Text = "plugin content" }, apply, order)];
    }

    public sealed class FineProvider : IPropertyPageProvider
    {
        public IReadOnlyList<PropertyPageInfo> GetPages(PropertyTarget target)
            => [new PropertyPageInfo("Extra", () => new TextBlock { Text = "extra for " + target.Paths.Count }, () => false, Order: 5)];
    }

    public sealed class BrokenProvider : IPropertyPageProvider
    {
        public IReadOnlyList<PropertyPageInfo> GetPages(PropertyTarget target) => throw new InvalidOperationException("plugin bug");
    }

    public static ElevationOptions? Elevation { get; set; }

    private static PropertySheetContext Context(Dir d, params string[] names) => new()
    {
        Target = new PropertyTarget(names.Select(n => Path.Combine(d.Docs, n)).ToList(), "text/plain"),
        Actions = new FileActions(new Trash()),
        BuiltInProviders = [("{22222222-0000-4000-8000-0000000000FF}", new PosixPermissionsPages([(RegistryIdentityCurrent.Gid, "mine")], elevation: Elevation))],
    };

    private static class RegistryIdentityCurrent { public static Vibe.Registry.RegistryIdentity Value => Vibe.Registry.RegistryIdentity.Current; public static uint Gid => Value.Gid; }

    private static ushort Mode(string path) => (ushort)((int)File.GetUnixFileMode(path) & 0x1FF);

    [AvaloniaFact]
    public async Task A_single_file_gets_general_permissions_and_details_tabs_with_real_values()
    {
        using var d = new Dir();
        var window = new PropertySheetWindow(Context(d, "a.txt"));
        window.Show();

        Assert.Equal(new[] { "General", "Permissions", "Details" }, window.Pages.Select(p => p.Title));
        Assert.Equal("a.txt Properties", window.Title);
        var general = window.Page<GeneralPage>();
        await general.SizeTask;
        Assert.Equal("a.txt", general.Name);
        Assert.Equal("1 byte", general.SizeText);
        Assert.Contains(window.Page<DetailsPage>().Rows, r => r.key == "Full path" && r.value == Path.Combine(d.Docs, "a.txt"));
        Assert.Contains(window.Page<DetailsPage>().Rows, r => r.key == "Permissions" && r.value.Contains("rw"));
        Assert.False(window.Pages.Any(p => p.IsDirty));
    }

    [AvaloniaFact]
    public async Task A_folder_shows_what_it_contains_and_several_items_show_a_combined_size_and_no_details()
    {
        using var d = new Dir();
        var folder = new PropertySheetWindow(Context(d, "sub")); folder.Show();
        await folder.Page<GeneralPage>().SizeTask;
        Assert.Equal("0 files, 0 folders", folder.Page<GeneralPage>().ContainsText);

        var two = new PropertySheetWindow(Context(d, "a.txt", "b b.txt")); two.Show();
        await two.Page<GeneralPage>().SizeTask;
        Assert.Equal("2 items Properties", two.Title);
        Assert.Equal("2 items", two.Page<GeneralPage>().Name);
        Assert.Equal("2 bytes", two.Page<GeneralPage>().SizeText);
        Assert.Empty(two.Page<DetailsPage>().Rows);
    }

    [AvaloniaFact]
    public void General_renames_refuses_bad_names_and_toggles_read_only()
    {
        using var d = new Dir();
        var window = new PropertySheetWindow(Context(d, "a.txt")); window.Show();
        var general = window.Page<GeneralPage>();

        general.Name = "bad/name";
        Assert.True(general.IsDirty);
        Assert.False(window.ApplyAll());
        Assert.Contains("cannot contain", general.Error);
        Assert.True(File.Exists(Path.Combine(d.Docs, "a.txt")));

        general.Name = "renamed.txt";
        general.ReadOnly = true;
        Assert.True(window.ApplyAll());
        var renamed = Path.Combine(d.Docs, "renamed.txt");
        Assert.True(File.Exists(renamed));
        Assert.Equal(0, Mode(renamed) & 0x92);                       // no write bit for anyone
    }

    [AvaloniaFact]
    public void Permissions_change_only_what_the_user_changed_and_folders_can_apply_recursively()
    {
        using var d = new Dir();
        var file = Path.Combine(d.Docs, "a.txt");
        File.SetUnixFileMode(file, (UnixFileMode)Convert.ToInt32("640", 8));
        var window = new PropertySheetWindow(Context(d, "a.txt")); window.Show();
        var page = window.Page<PermissionsPage>();

        Assert.True(page.Get(UnixFileMode.GroupRead));
        Assert.False(page.Get(UnixFileMode.OtherRead));
        page.Set(UnixFileMode.OtherRead, true);
        page.Set(UnixFileMode.GroupRead, false);
        Assert.True(window.ApplyAll());
        Assert.Equal("0604", Convert.ToString(Mode(file), 8).PadLeft(4, '0'));

        var sub = Path.Combine(d.Docs, "sub"); File.WriteAllText(Path.Combine(sub, "inner.txt"), "x");
        File.SetUnixFileMode(Path.Combine(sub, "inner.txt"), (UnixFileMode)Convert.ToInt32("600", 8));
        var folder = new PropertySheetWindow(Context(d, "sub")); folder.Show();
        var fp = folder.Page<PermissionsPage>();
        fp.Set(UnixFileMode.OtherRead, true);
        fp.Recursive = true;
        Assert.True(folder.ApplyAll());
        Assert.Equal("0644", Convert.ToString(Mode(Path.Combine(sub, "inner.txt")), 8).PadLeft(4, '0'));   // the contents now match the folder: group and others can read, and the file did not become a program
    }

    [AvaloniaFact]
    public void A_selection_shows_mixed_boxes_and_leaves_mixed_ones_alone_unless_touched()
    {
        using var d = new Dir();
        var a = Path.Combine(d.Docs, "a.txt"); var b = Path.Combine(d.Docs, "b b.txt");
        File.SetUnixFileMode(a, (UnixFileMode)Convert.ToInt32("640", 8));
        File.SetUnixFileMode(b, (UnixFileMode)Convert.ToInt32("604", 8));
        var window = new PropertySheetWindow(Context(d, "a.txt", "b b.txt")); window.Show();
        var page = window.Page<PermissionsPage>();

        Assert.Null(page.Get(UnixFileMode.GroupRead));                               // a has it, b does not: mixed
        Assert.Null(page.Get(UnixFileMode.OtherRead));
        Assert.True(page.Get(UnixFileMode.UserRead));                                // both have it
        Assert.False(page.IsDirty);

        page.Set(UnixFileMode.UserExecute, true);                                    // only this is changed
        Assert.True(window.ApplyAll());
        Assert.Equal("0740", Convert.ToString(Mode(a), 8).PadLeft(4, '0'));
        Assert.Equal("0704", Convert.ToString(Mode(b), 8).PadLeft(4, '0'));           // the mixed bits stayed as they were on each
    }

    [AvaloniaFact]
    public void Giving_a_file_to_a_group_you_belong_to_works()
    {
        using var d = new Dir();
        var window = new PropertySheetWindow(Context(d, "a.txt")); window.Show();
        var page = window.Page<PermissionsPage>();

        page.SelectGroup("mine");
        Assert.True(window.ApplyAll());
        Assert.Equal(RegistryIdentityCurrent.Gid, FileProperties.Read(Path.Combine(d.Docs, "a.txt")).Gid);
    }

    [AvaloniaFact]
    public void Registered_handlers_add_tabs_in_order_a_refusing_tab_keeps_the_sheet_open_and_bad_ones_only_leave_a_note()
    {
        using var d = new Dir();
        var dll = typeof(PropertySheetTests).Assembly.Location;
        var hive = new Vibe.Registry.Hive("HKCU") { Identity = Vibe.Registry.RegistryIdentity.Current };
        var hkcr = new Vibe.Registry.ClassesRoot(hive);
        string Reg(string clsid, Type type, bool executable = true)
        {
            ComponentRegistrar.Register(hkcr, new ComponentInfo(clsid, type.FullName!, dll, "t", ThreadingModel.Free));
            hkcr.SetValue($@"*\{PropertyHandlers.HandlersKey}\{type.Name}", "", Vibe.Registry.RegistryValueKind.String, clsid);
            if (!executable)
            {
                var server = $@"CLSID\{clsid}\InprocServer32";
                hkcr.SetValuePermissions(server, "", hkcr.GetValuePermissions(server, "")!.Value.WithMode(Vibe.Registry.PosixPermissions.ValueDefault));
            }
            return clsid;
        }
        Reg("{22222222-0000-4000-8000-000000000001}", typeof(FineProvider));
        Reg("{22222222-0000-4000-8000-000000000002}", typeof(BrokenProvider));
        Reg("{22222222-0000-4000-8000-000000000003}", typeof(Page), executable: false);   // registered, not trusted
        var context = new PropertySheetContext
        {
            Target = new PropertyTarget([Path.Combine(d.Docs, "a.txt")], "text/plain"), Actions = new FileActions(new Trash()),
            Handlers = new PropertyHandlers(hkcr), Loader = new ComponentLoader(),
        };

        var window = new PropertySheetWindow(context); window.Show();

        Assert.Equal(3, window.Pages.Count);                                                  // General, Extra (order 5, from the registry), Details
        Assert.Equal("Extra", window.Pages[1].Title);                                       // sorted by Order: after General (0), before Details (20)
        Assert.Equal(2, window.Problems.Count);
        Assert.Contains(window.Problems, p => p.Contains("BrokenProvider") && p.Contains("plugin bug"));
        Assert.Contains(window.Problems, p => p.Contains("not loaded") && p.Contains("not executable"));
        Assert.False(window.ApplyAll());                                                     // the plugin's tab said no: the window stays open
        Assert.IsType<TextBlock>(window.Pages[1].Content);
    }
}

public class PropertySheetScreenshots
{
    [AvaloniaFact]
    public async Task Properties_screenshot()
    {
        var dir = Environment.GetEnvironmentVariable("VDE_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Vde.Shell.Controls.ShellIconProvider.Default = new Vde.Shell.Controls.ShellIconProvider();
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects", "vibe-de", "README.md");
        var xdg = new Vde.Freedesktop.XdgDirectories();
        var types = new FileTypes(new MimeDatabase(xdg), new Vibe.Registry.ClassesRoot(new Vibe.Registry.Hive("x")), Vde.Freedesktop.Locale.FromEnvironment());
        var names = UserNames.System;
        var context = new PropertySheetContext
        {
            Target = new PropertyTarget([target], "text/markdown"), Types = types, Actions = new FileActions(new Trash()),
            BuiltInProviders = [("{22222222-0000-4000-8000-0000000000FF}", new PosixPermissionsPages(elevation: null))],
            OpensWith = _ => "Kate", ChangeOpensWith = _ => { },
        };
        var window = new PropertySheetWindow(context);
        window.Show();
        await window.Page<GeneralPage>().SizeTask;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!.Save(Path.Combine(dir, "props-general.png"));
        window.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
        Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!.Save(Path.Combine(dir, "props-permissions.png"));
    }
}

public class TakeOwnershipTests
{
    private sealed class Backend(bool canElevate = true) : IElevationBackend
    {
        public List<IReadOnlyList<string>> Ran { get; } = new();
        public bool CanElevate(Vibe.Registry.RegistryIdentity identity) => canElevate;
        public string? PasswordAccount(Vibe.Registry.RegistryIdentity identity) => "root";
        public Task<ElevationOutcome> RunAsync(IReadOnlyList<string> commandLine, ReadOnlyMemory<char> password, CancellationToken cancel)
        {
            Ran.Add(commandLine);
            return Task.FromResult(ElevationOutcome.Success);
        }
    }

    private sealed class Prompt(bool approve) : IElevationPrompt
    {
        public ElevationRequest? Seen;
        public Task<ElevationResponse> AskAsync(ElevationRequest request, int attempt, string? previousError, CancellationToken cancel)
        {
            Seen = request;
            return Task.FromResult(approve ? new ElevationResponse(true, "pw".ToCharArray()) : ElevationResponse.Cancel);
        }
    }

    // Pretends to be user 4242, so that the files (owned by the real test user) are "somebody else's".
    private static (PropertySheetWindow window, Backend backend, Prompt prompt) Open(Dir d, string name, bool approve = true, bool canElevate = true, bool withElevation = true)
    {
        var backend = new Backend(canElevate); var prompt = new Prompt(approve);
        var elevation = withElevation ? new ElevationOptions(backend, prompt, new Vibe.Registry.RegistryIdentity(4242, 4242)) : null;
        var context = new PropertySheetContext
        {
            Target = new PropertyTarget([Path.Combine(d.Docs, name)], ""),
            BuiltInProviders = [("{22222222-0000-4000-8000-0000000000FF}", new PosixPermissionsPages([(1, "g")], elevation: elevation, findExecutable: _ => "/usr/bin/chown"))],
        };
        var window = new PropertySheetWindow(context);
        window.Show();
        return (window, backend, prompt);
    }

    [AvaloniaFact]
    public async Task Take_ownership_shows_the_exact_chown_command_asks_for_the_right_password_and_runs_it()
    {
        using var d = new Dir();
        var (window, backend, prompt) = Open(d, "a.txt");
        var page = window.Page<PermissionsPage>();

        Assert.True(page.CanTakeOwnership);
        Assert.True(await page.TakeOwnershipAsync());

        Assert.Equal("Take ownership", prompt.Seen!.Label);
        Assert.Equal("root", prompt.Seen.Account);                                     // sudo wants root's password here
        Assert.Equal(new[] { "/usr/bin/chown", "-h", "--", "4242", Path.Combine(d.Docs, "a.txt") }, prompt.Seen.CommandLine);
        Assert.Equal(prompt.Seen.CommandLine, Assert.Single(backend.Ran));
        Assert.Equal("Ownership was changed.", page.Info);
    }

    [AvaloniaFact]
    public async Task A_folder_with_apply_to_everything_inside_is_taken_recursively_and_links_are_not_followed()
    {
        using var d = new Dir();
        var (window, backend, _) = Open(d, "sub");
        var page = window.Page<PermissionsPage>();

        await page.TakeOwnershipAsync();
        Assert.DoesNotContain("-R", backend.Ran[0]);

        page.Recursive = true;
        await page.TakeOwnershipAsync();
        Assert.Equal(new[] { "/usr/bin/chown", "-h", "-R", "--", "4242", Path.Combine(d.Docs, "sub") }, backend.Ran[1]);   // -h with -R: links are changed themselves, never followed out
    }

    [AvaloniaFact]
    public async Task Cancelling_changes_nothing_and_a_user_who_may_not_elevate_is_told_without_any_prompt()
    {
        using var d = new Dir();
        var (cancelled, cb, _) = Open(d, "a.txt", approve: false);
        var page = cancelled.Page<PermissionsPage>();
        Assert.False(await page.TakeOwnershipAsync());
        Assert.Equal("Cancelled. Nothing was changed.", page.Info);
        Assert.Empty(cb.Ran);

        var (denied, db, dp) = Open(d, "a.txt", canElevate: false);
        var deniedPage = denied.Page<PermissionsPage>();
        Assert.False(await deniedPage.TakeOwnershipAsync());
        Assert.Contains("not allowed to elevate", deniedPage.Info);
        Assert.Null(dp.Seen);                                                          // rejected before anything was shown
        Assert.Empty(db.Ran);
    }

    [AvaloniaFact]
    public void The_button_is_only_offered_when_it_would_change_something_and_there_is_a_way_to_ask()
    {
        using var d = new Dir();
        var (without, _, _) = Open(d, "a.txt", withElevation: false);
        Assert.False(without.Page<PermissionsPage>().CanTakeOwnership);               // no elevation available

        var mine = new PropertySheetContext
        {
            Target = new PropertyTarget([Path.Combine(d.Docs, "a.txt")], ""),
            BuiltInProviders = [("{22222222-0000-4000-8000-0000000000FF}", new PosixPermissionsPages(elevation: new ElevationOptions(new Backend(), new Prompt(true), Vibe.Registry.RegistryIdentity.Current)))],
        };
        var owned = new PropertySheetWindow(mine); owned.Show();
        Assert.False(owned.Page<PermissionsPage>().CanTakeOwnership);                  // already the owner: nothing to take
    }
}

public class OpenWithWindowTests
{
    private static (OpenWithWindow window, FileAssociations assoc, Vde.Freedesktop.DesktopEntryDatabase apps, string root) Make()
    {
        var root = Path.Combine(Path.GetTempPath(), "openwith-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "applications")); Directory.CreateDirectory(Path.Combine(root, "mime"));
        File.WriteAllText(Path.Combine(root, "mime", "globs2"), "50:text/plain:*.txt\n");
        foreach (var (id, name, mime) in new[] { ("kate", "Kate", "text/plain;"), ("nano", "Nano", "text/plain;"), ("gimp", "GIMP", "image/png;") })
            File.WriteAllText(Path.Combine(root, "applications", id + ".desktop"), $"[Desktop Entry]\nType=Application\nName={name}\nExec={id} %f\nMimeType={mime}\n");
        var env = new Dictionary<string, string> { ["HOME"] = root, ["XDG_DATA_HOME"] = root, ["XDG_DATA_DIRS"] = "/nonexistent", ["XDG_CONFIG_HOME"] = Path.Combine(root, "cfg") };
        var xdg = new Vde.Freedesktop.XdgDirectories(k => env.GetValueOrDefault(k));
        var apps = Vde.Freedesktop.DesktopEntryDatabase.Load(xdg, []);
        var assoc = new FileAssociations(xdg, new MimeDatabase(xdg), apps, ["VDE"], new Vibe.Registry.ClassesRoot(new Vibe.Registry.Hive("x") { Identity = Vibe.Registry.RegistryIdentity.Root }), false);
        var window = new OpenWithWindow(new OpenWithModel(assoc, apps, ["VDE"], "text/plain"), "text files");
        window.Show();
        return (window, assoc, apps, root);
    }

    [AvaloniaFact]
    public void The_chooser_lists_recommended_then_other_apps_filters_and_returns_the_choice()
    {
        var (window, _, apps, root) = Make();
        try
        {
            Assert.Equal(new[] { "Kate", "Nano", "GIMP" }, window.Names);
            Assert.False(window.OpenEnabled);                                                  // nothing chosen yet

            window.Search("gim");
            Assert.Equal(new[] { "GIMP" }, window.Names);

            window.Choose(apps.Find("gimp")!);
            Assert.True(window.OpenEnabled);
            window.Accept();
            Assert.Equal("GIMP", window.Result!.Name);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public void Always_changes_the_default_and_cancelling_gives_nothing()
    {
        var (window, assoc, apps, root) = Make();
        try
        {
            window.Choose(apps.Find("nano")!);
            window.SetAlways(true);
            window.Accept();
            Assert.Equal("Nano", assoc.GetDefault("text/plain")!.Name);
            Assert.Equal("Nano", window.Result!.Name);

            var (cancelled, assoc2, _, root2) = Make();
            cancelled.Close();
            Assert.Null(cancelled.Result);
            Directory.Delete(root2, recursive: true);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
