using Vde.Components;
using Vde.Shell;
using Vde.Shell.Notifications;
using Vibe.Registry;
using Vibe.Registry.Remote;

namespace VExplore;

// The shell host: one process that owns what the user sees (taskbar, notification flyout, file manager) and
// loads the components registered with RegSvrVDE. This is the wiring; the windows come on top of it.
public sealed class ExplorerHost : IDisposable
{
    private readonly RegistryStore _store;

    public ExplorerHost(RegistryStore store)
    {
        _store = store;
        Notifications = new NotificationService();
        NotificationBinding = Notifications.BindTo(store.CurrentUser);
        Catalog = new ComponentCatalog(store.ClassesRoot);
    }

    public IHive CurrentUser => _store.CurrentUser;
    public IHive LocalMachine => _store.LocalMachine;
    public ClassesRoot Classes => _store.ClassesRoot;

    // The model behind the toast popups and the taskbar's Notification Center flyout.
    public NotificationService Notifications { get; }
    public IDisposable NotificationBinding { get; }
    public ComponentCatalog Catalog { get; }

    // Loads trusted components (property sheet handlers, later portal backends and search plugins).
    public ComponentLoader Loader { get; } = new();
    public PropertyHandlers PropertyHandlers => new(Classes);

    // Components the host may load: registered, executable entry, assembly file safe. Others are reported, not loaded.
    public IReadOnlyList<ComponentRegistration> LoadableComponents => Catalog.All().Where(c => c.Trust.IsTrusted).ToList();
    public IReadOnlyList<ComponentRegistration> RefusedComponents => Catalog.All().Where(c => !c.Trust.IsTrusted).ToList();

    public static ExplorerHost Open() => new(FhiveClient.OpenStore());

    public void Dispose()
    {
        NotificationBinding.Dispose();
        _store.Dispose();
    }
}
