public sealed class WaylandCompositorBridge :
    ICompositorBridge,
    IAbstractInputHandler,
    IAbstractCharHandler {
    public string ServiceName => "com.fluent.Compositor.Wayland";
    public string ObjectPath => "/com/fluent/Compositor/Wayland";
    public string DisplayName => "Wayland Compositor Bridge";

    private WaylandDisplay _display;
    private WaylandRegistry _registry;

    // protocol globals
    private WlCompositor _compositor;
    private XdgWmBase _xdgWmBase;
    private ZwlrLayerShellV1 _layerShell;
    private ZwpTextInputManagerV3 _textInputManager;
    private ZwpInputMethodManagerV2 _inputMethodManager;
    private ZwpVirtualKeyboardManagerV1 _virtualKeyboardManager;
    private ZwpVirtualPointerManagerV1 _virtualPointerManager;

    public async Task InitializeAsync() {
        _display = new WaylandDisplay();
        _registry = _display.GetRegistry();

        _registry.Global += (sender, e) => {
            switch (e.Interface) {
                case "wl_compositor":
                    _compositor = _registry.Bind<WlCompositor>(e.Name, e.Version);
                    break;

                case "xdg_wm_base":
                    _xdgWmBase = _registry.Bind<XdgWmBase>(e.Name, e.Version);
                    break;

                case "zwlr_layer_shell_v1":
                    _layerShell = _registry.Bind<ZwlrLayerShellV1>(e.Name, e.Version);
                    break;

                case "zwp_text_input_manager_v3":
                    _textInputManager = _registry.Bind<ZwpTextInputManagerV3>(e.Name, e.Version);
                    break;

                case "zwp_input_method_manager_v2":
                    _inputMethodManager = _registry.Bind<ZwpInputMethodManagerV2>(e.Name, e.Version);
                    break;

                case "zwp_virtual_keyboard_manager_v1":
                    _virtualKeyboardManager = _registry.Bind<ZwpVirtualKeyboardManagerV1>(e.Name, e.Version);
                    break;

                case "zwp_virtual_pointer_manager_v1":
                    _virtualPointerManager = _registry.Bind<ZwpVirtualPointerManagerV1>(e.Name, e.Version);
                    break;
            }
        };
    }

    public Task ShutdownAsync() {
        _display?.Dispose();
        return Task.CompletedTask;
    }

    public Task<SurfaceHandle> CreateSurfaceAsync(SurfaceRequest request) {
        var wlSurface = _compositor.CreateSurface();

        if (request.Type == SurfaceType.Toplevel) {
            var xdgSurface = _xdgWmBase.GetXdgSurface(wlSurface);
            var toplevel = xdgSurface.GetToplevel();
            return Task.FromResult(new SurfaceHandle(wlSurface, xdgSurface, toplevel));
        }

        if (request.Type == SurfaceType.Layer) {
            var layerSurface = _layerShell.GetLayerSurface(
                wlSurface,
                request.Output,
                request.Layer,
                request.Namespace
            );
            return Task.FromResult(new SurfaceHandle(wlSurface, layerSurface));
        }

        throw new NotSupportedException();
    }

    public Task SendInputEventAsync(InputEvent evt) {
        switch (evt.Kind) {
            case InputEventKind.PointerMotion:
                _virtualPointer.Motion((double)evt.Data["dx"], (double)evt.Data["dy"]);
                break;

            case InputEventKind.PointerButton:
                _virtualPointer.Button((uint)evt.Data["button"], (uint)evt.Data["state"]);
                break;

            case InputEventKind.Key:
                _virtualKeyboard.Key((uint)evt.Data["keycode"], (uint)evt.Data["state"]);
                break;
        }

        return Task.CompletedTask;
    }

    public Task<IEnumerable<OutputInfo>> GetOutputsAsync() {
        return Task.FromResult(_outputs.Select(o => new OutputInfo {
            Id = o.Name,
            Width = o.Width,
            Height = o.Height,
            Scale = o.Scale,
            Transform = o.Transform
        }));
    }
}
