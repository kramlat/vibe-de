public interface IAbstractInputHandler : IDBusInterface
{
    // Unique ID for routing
    string HandlerId { get; }

    // Human-readable name
    string DisplayName { get; }

    // What kind of input this handler produces (text, pointer, gesture, etc.)
    InputHandlerType HandlerType { get; }

    // Called when the handler is activated (IME focus, VR session start, etc.)
    Task ActivateAsync();

    // Called when the handler is deactivated
    Task DeactivateAsync();

    // Main event stream
    Task SendInputEventAsync(InputEvent evt);

    // Optional: configuration UI
    Task<object?> CreateConfigPanelAsync();
}

public sealed class InputEvent
{
    public string Source { get; set; } = "";
    public InputEventKind Kind { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();
}
