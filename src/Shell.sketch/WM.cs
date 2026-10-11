public interface ICompositorBridge : IDBusInterface {
    string BackendName { get; }

    // Window/surface lifecycle
    Task<SurfaceHandle> CreateSurfaceAsync(SurfaceRequest request);
    Task DestroySurfaceAsync(string surfaceId);

    // Window management
    Task SetSurfacePositionAsync(string surfaceId, int x, int y);
    Task SetSurfaceSizeAsync(string surfaceId, int width, int height);
    Task SetSurfaceStateAsync(string surfaceId, SurfaceState state);

    // Input
    Task SendInputEventAsync(InputEvent evt);

    // Output/monitor management
    Task<IEnumerable<OutputInfo>> GetOutputsAsync();
    Task SetOutputModeAsync(string outputId, OutputMode mode);

    // Frame callbacks
    Task RequestFrameAsync(string surfaceId);
}

public interface IWindowManager : IDBusInterface {
    // Enumerate all windows known to the WM
    Task<IReadOnlyList<WindowInfo>> GetWindowsAsync();

    // Get a single window by ID
    Task<WindowInfo?> GetWindowAsync(string windowId);

    // Focus management
    Task FocusWindowAsync(string windowId);
    Task<string?> GetFocusedWindowAsync();

    // Window lifecycle
    Task CloseWindowAsync(string windowId);
    Task MinimizeWindowAsync(string windowId);
    Task MaximizeWindowAsync(string windowId);
    Task RestoreWindowAsync(string windowId);

    // Movement + resizing
    Task MoveWindowAsync(string windowId, int x, int y);
    Task ResizeWindowAsync(string windowId, int width, int height);

    // State changes (fullscreen, pinned, floating, tiled, etc.)
    Task SetWindowStateAsync(string windowId, WindowState state);

    // Workspace management
    Task<int> GetWorkspaceAsync(string windowId);
    Task SetWorkspaceAsync(string windowId, int workspaceIndex);
    Task<IReadOnlyList<int>> GetWorkspacesAsync();

    // Events (for task manager, taskbar, overview)
    event EventHandler<WindowCreatedEvent> WindowCreated;
    event EventHandler<WindowDestroyedEvent> WindowDestroyed;
    event EventHandler<WindowStateChangedEvent> WindowStateChanged;
    event EventHandler<WindowFocusChangedEvent> WindowFocusChanged;
}
