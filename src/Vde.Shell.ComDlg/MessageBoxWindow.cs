using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vde.Shell.Controls;

namespace Vde.Shell.ComDlg;

public enum MessageButtons { Ok, OkCancel, YesNo, YesNoCancel, RetryCancel }
public enum MessageIcon { None, Information, Warning, Error, Question }
public enum DialogResult { None, Ok, Cancel, Yes, No, Retry }

// "Are you sure?", "Done", "Something went wrong": a message, an icon and the buttons that make sense.
// Closing the window with the X, or pressing Escape, gives the cancelling answer (No when there is no Cancel).
public sealed class MessageBoxWindow : Window
{
    public MessageBoxWindow(string text, string title, MessageButtons buttons, MessageIcon icon, DialogResult defaultButton = DialogResult.None)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        MinWidth = 320; MaxWidth = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 440 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        if (IconName(icon) is { } name)
            top.Children.Add(new ShellIcon { IconName = name, IconSize = 32, VerticalAlignment = VerticalAlignment.Top });
        top.Children.Add(message);

        var cancelAnswer = buttons switch
        {
            MessageButtons.Ok => DialogResult.Ok,
            MessageButtons.YesNo => DialogResult.No,
            _ => DialogResult.Cancel,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var answers = Answers(buttons);
        var first = defaultButton != DialogResult.None && answers.Contains(defaultButton) ? defaultButton : answers[0];
        foreach (var answer in answers)
        {
            var button = DialogHost.Button(Label(answer), () => Finish(answer), isDefault: answer == first, isCancel: answer == cancelAnswer);
            button.Tag = answer;
            row.Children.Add(button);
        }
        CancelAnswer = cancelAnswer;

        Content = new StackPanel { Margin = new Thickness(20), Spacing = 20, Children = { top, row } };
        Closing += (_, _) => Result = Result == DialogResult.None ? cancelAnswer : Result;
    }

    public DialogResult Result { get; private set; }
    public DialogResult CancelAnswer { get; }
    public IReadOnlyList<DialogResult> Buttons => ((Panel)((StackPanel)Content!).Children[1]).Children.OfType<Button>().Select(b => (DialogResult)b.Tag!).ToList();

    // What a click on that button does; also how tests press them.
    public void Press(DialogResult answer) => Finish(answer);

    private void Finish(DialogResult answer)
    {
        Result = answer;
        Close(answer);
    }

    private static DialogResult[] Answers(MessageButtons buttons) => buttons switch
    {
        MessageButtons.OkCancel => [DialogResult.Ok, DialogResult.Cancel],
        MessageButtons.YesNo => [DialogResult.Yes, DialogResult.No],
        MessageButtons.YesNoCancel => [DialogResult.Yes, DialogResult.No, DialogResult.Cancel],
        MessageButtons.RetryCancel => [DialogResult.Retry, DialogResult.Cancel],
        _ => [DialogResult.Ok],
    };

    private static string Label(DialogResult r) => r.ToString() == "Ok" ? "OK" : r.ToString();

    private static string? IconName(MessageIcon icon) => icon switch
    {
        MessageIcon.Information => "dialog-information",
        MessageIcon.Warning => "dialog-warning",
        MessageIcon.Error => "dialog-error",
        MessageIcon.Question => "dialog-question",
        _ => null,
    };
}
