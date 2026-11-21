using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Vde.Shell;

namespace Vde.Shell.Controls;

// Draws a widget card an app sent (see WidgetCard) with the shell's own controls: nothing the app sends can do more than show text,
// a picture, a bar, a list or press a button, and a press only tells the app which action it was.
public static class WidgetCardView
{
    public static Control Build(WidgetCard card, Action<string> action)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var row in card.Rows)
            switch (row)
            {
                case TextRow t:
                    panel.Children.Add(new TextBlock { Text = t.Text, TextWrapping = TextWrapping.Wrap, FontSize = t.Size switch { "large" => 26, "small" => 11, _ => 13 }, Opacity = t.Muted ? 0.7 : 1 });
                    break;
                case ImageRow i:
                    panel.Children.Add(Picture(i));
                    break;
                case ProgressRow p:
                    if (p.Label.Length > 0) panel.Children.Add(new TextBlock { Text = p.Label, FontSize = 12 });
                    panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = p.Value, Height = 6 });
                    break;
                case ButtonRow b:
                    var button = new Button { Content = b.Text, Tag = b.Action, HorizontalAlignment = HorizontalAlignment.Left };
                    button.Click += (_, _) => action(b.Action);
                    panel.Children.Add(button);
                    break;
                case ListRow l:
                    foreach (var item in l.Items)
                    {
                        var entry = new Button
                        {
                            Tag = item.Action, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                            Content = new StackPanel { Children = { new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = item.Subtitle, FontSize = 11, Opacity = 0.7, IsVisible = item.Subtitle.Length > 0 } } },
                        };
                        entry.Click += (_, _) => action(item.Action);
                        panel.Children.Add(entry);
                    }
                    break;
            }
        return panel;
    }

    // An absolute path to a picture the widget can read, or the name of an icon.
    private static Control Picture(ImageRow row)
    {
        if (Path.IsPathRooted(row.Path) && File.Exists(row.Path))
            try
            {
                using var stream = File.OpenRead(row.Path);
                return new Image { Source = Bitmap.DecodeToHeight(stream, row.Height * 2), Height = row.Height, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
            }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException) { }
        return new ShellIcon { IconNames = [row.Path], IconSize = Math.Min(row.Height, 64), HorizontalAlignment = HorizontalAlignment.Left };
    }
}
