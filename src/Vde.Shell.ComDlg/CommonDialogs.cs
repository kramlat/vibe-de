using Avalonia.Controls;
using Vde.Freedesktop;
using Vde.Shell;

namespace Vde.Shell.ComDlg;

// The dialogs applications call, in one place. Every call takes the window it belongs to (so the dialog stays
// on top of it and blocks it); with no owner the dialog just appears.
public static class CommonDialogs
{
    public static async Task<DialogResult> MessageBoxAsync(Window? owner, string text, string title = "VDE", MessageButtons buttons = MessageButtons.Ok,
        MessageIcon icon = MessageIcon.None, DialogResult defaultButton = DialogResult.None)
    {
        var box = new MessageBoxWindow(text, title, buttons, icon, defaultButton);
        await DialogHost.RunAsync(box, owner);
        return box.Result == DialogResult.None ? box.CancelAnswer : box.Result;
    }

    // Null when cancelled. Several paths only with options.Multiselect.
    public static async Task<IReadOnlyList<string>?> OpenFileAsync(Window? owner, KnownFolders folders, FileDialogOptions? options = null, FileTypes? types = null)
        => await RunFile(owner, new FileDialogWindow(FileDialogMode.Open, options ?? new(), folders, types));

    public static async Task<string?> SaveFileAsync(Window? owner, KnownFolders folders, FileDialogOptions? options = null, FileTypes? types = null)
        => (await RunFile(owner, new FileDialogWindow(FileDialogMode.Save, options ?? new(), folders, types)))?.FirstOrDefault();

    public static async Task<string?> PickFolderAsync(Window? owner, KnownFolders folders, FileDialogOptions? options = null)
        => (await RunFile(owner, new FileDialogWindow(FileDialogMode.Folder, options ?? new(), folders)))?.FirstOrDefault();

    // validate returns an explanation when the text is not acceptable, or null when it is.
    public static async Task<string?> InputAsync(Window? owner, string prompt, string title = "VDE", string defaultText = "", Func<string, string?>? validate = null)
    {
        var window = new InputWindow(prompt, title, defaultText, password: false, validate);
        await DialogHost.RunAsync(window, owner);
        return window.Result;
    }

    public static async Task<string?> PasswordAsync(Window? owner, string prompt, string title = "VDE")
    {
        var window = new InputWindow(prompt, title, "", password: true);
        await DialogHost.RunAsync(window, owner);
        return window.Result;
    }

    // The "Open with" chooser. Null when cancelled; with "always" ticked the type's default application is changed too.
    public static async Task<DesktopEntry?> ChooseApplicationAsync(Window? owner, FileAssociations associations, DesktopEntryDatabase apps,
        IReadOnlyList<string> desktops, string mimeType, string description = "this kind of file")
    {
        var window = new OpenWithWindow(new OpenWithModel(associations, apps, desktops, mimeType), description);
        await DialogHost.RunAsync(window, owner);
        return window.Result;
    }

    private static async Task<IReadOnlyList<string>?> RunFile(Window? owner, FileDialogWindow window)
    {
        await DialogHost.RunAsync(window, owner);
        return window.Paths;
    }
}
