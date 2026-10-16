using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Vde.Shell;

namespace Vde.Shell.ComDlg;

// Progress of a file operation (copy, move): what is being worked on, a bar, and Cancel.
// Feed it from the operation's IProgress; cancelling signals the token the operation was started with.
public sealed class ProgressWindow : Window, IProgress<FileProgress>
{
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Width = 380, Height = 14 };
    private readonly TextBlock _what = new() { TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, MaxWidth = 380 };
    private readonly TextBlock _detail = new() { FontSize = 12, Opacity = 0.7 };
    private readonly CancellationTokenSource _cancel = new();

    public ProgressWindow(string title)
    {
        Title = title;
        Width = 440; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = DialogHost.Button("Cancel", () => _cancel.Cancel(), isCancel: true);
        Content = new StackPanel { Margin = new Thickness(20), Spacing = 8, Children = { _what, _bar, _detail, DialogHost.ButtonRow(cancel) } };
        Closing += (_, _) => _cancel.Cancel();   // closing the window cancels the work it shows
    }

    public CancellationToken Token => _cancel.Token;
    public bool IsCancelled => _cancel.IsCancellationRequested;
    public double Fraction => _bar.Value;
    public string Current => _what.Text ?? "";
    public string Detail => _detail.Text ?? "";

    // Safe to call from the worker thread.
    public void Report(FileProgress value)
    {
        void Apply()
        {
            _what.Text = Path.GetFileName(value.CurrentPath);
            _bar.Value = value.BytesTotal > 0 ? Math.Clamp((double)value.BytesDone / value.BytesTotal, 0, 1) : value.ItemsTotal > 0 ? (double)value.ItemsDone / value.ItemsTotal : 0;
            _detail.Text = $"{value.ItemsDone} of {value.ItemsTotal} items · {ByteFormat.Format(value.BytesDone)} of {ByteFormat.Format(value.BytesTotal)}";
        }
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Apply();
        else Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
    }
}
