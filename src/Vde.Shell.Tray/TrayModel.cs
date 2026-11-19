namespace Vde.Shell.Tray;

// The data the shell actually renders, lifted from org.kde.StatusNotifierItem — the
// StatusNotifier spec's icon-, tooltip- and status-shaped surface is kept intact here so the
// DBus wire types stop at the client that reads them. All strings may be empty; a pixmap may be missing.

// An icon sent as raw pixels: ARGB32, four bytes per pixel in A,R,G,B order, rows top to bottom.
public sealed record TrayPixmap(int Width, int Height, byte[] Argb)
{
    public bool IsUsable => Width > 0 && Height > 0 && Argb.Length >= Width * Height * 4;
}

// The hover tooltip: an icon (name or pixels), a title line and a body line.
public sealed record TrayTooltip(string IconName, TrayPixmap? Icon, string Title, string Body)
{
    public string FirstLine => Title is not null && Title.Length > 0 ? Title : Body;
    public string SecondLine => Title is not null && Title.Length > 0 ? Body : "";
}

// One tray icon in the taskbar. Which image to draw follows the usual convention: the attention
// icon while Status is NeedsAttention, otherwise the plain icon.
public sealed record TrayIcon(
    string Service,   // the bus name the item lives at
    string Path)      // the object path, as registered
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Category { get; init; } = "";
    public string Status { get; init; } = "Active";   // Active, Passive or NeedsAttention
    public string IconName { get; init; } = "";
    public TrayPixmap? IconPixmap { get; init; }
    public string AttentionIconName { get; init; } = "";
    public TrayPixmap? AttentionIconPixmap { get; init; }
    public TrayTooltip? ToolTip { get; init; }

    public string BestIconName
        => Status == "NeedsAttention" && AttentionIconName.Length > 0 ? AttentionIconName : IconName;

    public TrayPixmap? BestPixmap
        => Status == "NeedsAttention" && (AttentionIconPixmap?.IsUsable ?? false) ? AttentionIconPixmap
         : IconPixmap?.IsUsable ?? false ? IconPixmap
         : Status == "NeedsAttention" ? AttentionIconPixmap
         : null;

    // What the tooltip line under the clock says when the app sent none: the application id.
    public string DisplayName => ToolTip?.FirstLine is { Length: > 0 } title ? title
        : Title.Length > 0 ? Title
        : Id;
}

// One menu row. Separators have only their flag; toggles carry their checked state.
public sealed record TrayMenuItem(
    int Id,
    string Label,
    bool Enabled = true,
    bool Visible = true,
    bool IsSeparator = false,
    int ToggleState = -1,          // -1 no toggle, 0 unchecked, 1 checked
    string IconName = "",
    IReadOnlyList<TrayMenuItem>? Children = null)
{
    public static readonly TrayMenuItem Separator = new(-1, "", IsSeparator: true);
    public IReadOnlyList<TrayMenuItem> Children { get; init; } = Children ?? [];
    public bool IsSubmenu => Children.Count > 0;
    public string DisplayLabel => Label.Replace("__", "\u0000").Replace("_", "").Replace("\u0000", "_");
}
