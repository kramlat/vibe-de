using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Vde.Shell;
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
