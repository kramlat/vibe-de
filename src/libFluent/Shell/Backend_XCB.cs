public sealed class X11BackendBinding :
    ICompositorBridge,
    IAbstractInputHandler,
    IAbstractCharHandler
{
    // X11 connection
    private IntPtr _display;
    private int _screen;

    // Input extensions
    private IntPtr _xim;
    private IntPtr _xic;

    // XInput2
    private int _xiOpcode;

    // Virtual input (XTest)
    private bool _xtestAvailable;

    // Window management
    private Dictionary<IntPtr, X11WindowInfo> _windows = new();

    public string ServiceName => "com.fluent.Compositor.X11";
    public string ObjectPath => "/com/fluent/Compositor/X11";
    public string DisplayName => "X11 Backend Binding";
}
