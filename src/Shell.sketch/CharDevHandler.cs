public interface IAbstractCharHandler : IDBusInterface {
    // Unique ID for routing
    string HandlerId { get; }

    // Human-readable name
    string DisplayName { get; }

    // Called when this handler becomes active (IME switch)
    Task ActivateAsync();

    // Called when this handler is deactivated
    Task DeactivateAsync();

    // Composition events (preedit)
    Task SendCompositionUpdateAsync(CharCompositionState state);

    // Commit final text
    Task CommitTextAsync(string text);

    // Optional: request to show IME candidate UI
    Task ShowCandidatesAsync(CharCandidateList candidates);

    // Optional: hide IME UI
    Task HideCandidatesAsync();

    // Optional: configuration UI
    Task<object?> CreateConfigPanelAsync();
}

public sealed class CharCompositionState {
    public string PreeditText { get; set; } = "";
    public int CursorPosition { get; set; }
    public bool IsVisible { get; set; }
}

public sealed class CharCandidateList {
    public IReadOnlyList<string> Candidates { get; set; } = Array.Empty<string>();
    public int SelectedIndex { get; set; }
}

public enum VirtualKey {
    VK_NONE = 0,

    // Letters
    VK_A, VK_B, VK_C, VK_D, VK_E, VK_F, VK_G,
    VK_H, VK_I, VK_J, VK_K, VK_L, VK_M, VK_N,
    VK_O, VK_P, VK_Q, VK_R, VK_S, VK_T, VK_U,
    VK_V, VK_W, VK_X, VK_Y, VK_Z,

    // Number row
    VK_0, VK_1, VK_2, VK_3, VK_4,
    VK_5, VK_6, VK_7, VK_8, VK_9,

    // Symbol keys (main section)
    VK_MINUS, // -
    VK_EQUAL, // =
    VK_LEFT_BRACKET, // [
    VK_RIGHT_BRACKET, // ]
    VK_BACKSLASH, // \
    VK_SEMICOLON, // ;
    VK_APOSTROPHE, // '
    VK_GRAVE, // `
    VK_COMMA, // ,
    VK_PERIOD, // .
    VK_SLASH, // /

    // Function keys
    VK_F1, VK_F2, VK_F3, VK_F4, VK_F5, VK_F6,
    VK_F7, VK_F8, VK_F9, VK_F10, VK_F11, VK_F12,
    VK_F13, VK_F14, VK_F15, VK_F16, VK_F17, VK_F18,
    VK_F19, VK_F20, VK_F21, VK_F22, VK_F23, VK_F24,

    // Modifiers (generic)
    VK_SHIFT,
    VK_CONTROL,
    VK_ALT,
    VK_SUPER,
    VK_META,

    // Modifiers (side-specific)
    VK_LEFT_SHIFT,
    VK_RIGHT_SHIFT,
    VK_LEFT_CONTROL,
    VK_RIGHT_CONTROL,
    VK_LEFT_ALT,
    VK_RIGHT_ALT,
    VK_LEFT_SUPER,
    VK_RIGHT_SUPER,
    VK_LEFT_META,
    VK_RIGHT_META,

    // Lock keys
    VK_CAPS_LOCK,
    VK_NUM_LOCK,
    VK_SCROLL_LOCK,

    // Navigation
    VK_LEFT,
    VK_RIGHT,
    VK_UP,
    VK_DOWN,
    VK_HOME,
    VK_END,
    VK_PAGE_UP,
    VK_PAGE_DOWN,
    VK_INSERT,
    VK_DELETE,

    // System keys
    VK_ESCAPE,
    VK_BACKSPACE,
    VK_TAB,
    VK_ENTER,
    VK_SPACE,
    VK_PRINT_SCREEN,
    VK_PAUSE,          // Pause
    VK_BREAK,          // Break (often same scancode as Pause)
    VK_CONTEXT_MENU,
    VK_APPLICATION,
    VK_SYSRQ,

    // Numpad
    VK_NUMPAD_0,
    VK_NUMPAD_1,
    VK_NUMPAD_2,
    VK_NUMPAD_3,
    VK_NUMPAD_4,
    VK_NUMPAD_5,
    VK_NUMPAD_6,
    VK_NUMPAD_7,
    VK_NUMPAD_8,
    VK_NUMPAD_9,
    VK_NUMPAD_DECIMAL,
    VK_NUMPAD_ENTER,
    VK_NUMPAD_ADD,
    VK_NUMPAD_SUBTRACT,
    VK_NUMPAD_MULTIPLY,
    VK_NUMPAD_DIVIDE,
    VK_NUMPAD_EQUAL,

    // OEM keys
    VK_OEM_1,
    VK_OEM_2,
    VK_OEM_3,
    VK_OEM_4,
    VK_OEM_5,
    VK_OEM_6,
    VK_OEM_7,
    VK_OEM_8,
    VK_OEM_PLUS,
    VK_OEM_MINUS,
    VK_OEM_COMMA,
    VK_OEM_PERIOD,
    VK_OEM_SLASH,
    VK_OEM_BACKSLASH,
    VK_OEM_PIPE,
    VK_OEM_QUOTE,
    VK_OEM_TILDE,

    // International / IME keys
    VK_KANA,
    VK_KANJI,
    VK_HANJA,
    VK_HANGUL,
    VK_HENKAN,
    VK_MUHENKAN,
    VK_ZENKAKU_HANKAKU,
    VK_ROMAJI,
    VK_KATAKANA,
    VK_HIRAGANA,
    VK_CONVERT,
    VK_NONCONVERT,
    VK_MODECHANGE,

    // Dead keys
    VK_DEAD_ACUTE,
    VK_DEAD_GRAVE,
    VK_DEAD_CIRCUMFLEX,
    VK_DEAD_TILDE,
    VK_DEAD_MACRON,
    VK_DEAD_BREVE,
    VK_DEAD_DOT,
    VK_DEAD_DIAERESIS,
    VK_DEAD_RING,
    VK_DEAD_CARON,
    VK_DEAD_CEDILLA,
    VK_DEAD_OGONEK,
    VK_DEAD_DOUBLE_ACUTE,

    // Media keys
    VK_VOLUME_UP,
    VK_VOLUME_DOWN,
    VK_VOLUME_MUTE,
    VK_MEDIA_PLAY,
    VK_MEDIA_STOP,
    VK_MEDIA_NEXT,
    VK_MEDIA_PREVIOUS,

    // Browser keys
    VK_BROWSER_BACK,
    VK_BROWSER_FORWARD,
    VK_BROWSER_REFRESH,
    VK_BROWSER_HOME,

    // App launch keys
    VK_LAUNCH_MAIL,
    VK_LAUNCH_APP1,
    VK_LAUNCH_APP2,

        // Gamepad face buttons (universal)
    VK_GAMEPAD_SOUTH,      // A (Xbox), Cross (PS), B (Nintendo)
    VK_GAMEPAD_EAST,       // B (Xbox), Circle (PS), A (Nintendo)
    VK_GAMEPAD_WEST,       // X (Xbox), Square (PS), Y (Nintendo)
    VK_GAMEPAD_NORTH,      // Y (Xbox), Triangle (PS), X (Nintendo)

    // Gamepad shoulders
    VK_GAMEPAD_L1,
    VK_GAMEPAD_R1,

    // Gamepad triggers
    VK_GAMEPAD_L2,
    VK_GAMEPAD_R2,

    // Gamepad stick buttons
    VK_GAMEPAD_L3,
    VK_GAMEPAD_R3,

    // Gamepad D-Pad
    VK_GAMEPAD_DPAD_UP,
    VK_GAMEPAD_DPAD_DOWN,
    VK_GAMEPAD_DPAD_LEFT,
    VK_GAMEPAD_DPAD_RIGHT,

    // Gamepad menu buttons
    VK_GAMEPAD_START,          // Options / Menu / +
    VK_GAMEPAD_SELECT,         // Back / Share / -
    VK_GAMEPAD_HOME,           // Xbox / PS / Home
    VK_GAMEPAD_CAPTURE,        // Screenshot / Share
    VK_GAMEPAD_QUICKACCESS,    // Quick Access Menu (… button)

    // Steam Deck rear grip buttons
    VK_GAMEPAD_L4,
    VK_GAMEPAD_L5,
    VK_GAMEPAD_R4,
    VK_GAMEPAD_R5,

    // Steam Deck trackpads
    VK_GAMEPAD_LPAD_CLICK,
    VK_GAMEPAD_RPAD_CLICK,
    VK_GAMEPAD_LPAD_TOUCH,
    VK_GAMEPAD_RPAD_TOUCH,

    // PlayStation-specific extras
    VK_GAMEPAD_TOUCHPAD_CLICK,
    VK_GAMEPAD_TOUCHPAD_TOUCH,
    VK_GAMEPAD_MIC_MUTE,

    // Nintendo-specific extras
    VK_GAMEPAD_SL,
    VK_GAMEPAD_SR,

    // Digitalized stick directions
    VK_GAMEPAD_LSTICK_UP,
    VK_GAMEPAD_LSTICK_DOWN,
    VK_GAMEPAD_LSTICK_LEFT,
    VK_GAMEPAD_LSTICK_RIGHT,

    VK_GAMEPAD_RSTICK_UP,
    VK_GAMEPAD_RSTICK_DOWN,
    VK_GAMEPAD_RSTICK_LEFT,
    VK_GAMEPAD_RSTICK_RIGHT
}

public enum InputDeviceType
{
    Keyboard,
    Gamepad,
    Mouse,
    Touch,
    Stylus,
    Unknown
}

public static class EvdevKeyConverter {
    private static readonly Dictionary<int, VirtualKey> EvdevToVk = new()
    {
        {  0, VirtualKey.VK_NONE },
        {  1, VirtualKey.VK_ESCAPE },
        {  2, VirtualKey.VK_1 },
        {  3, VirtualKey.VK_2 },
        {  4, VirtualKey.VK_3 },
        {  5, VirtualKey.VK_4 },
        {  6, VirtualKey.VK_5 },
        {  7, VirtualKey.VK_6 },
        {  8, VirtualKey.VK_7 },
        {  9, VirtualKey.VK_8 },
        { 10, VirtualKey.VK_9 },
        { 11, VirtualKey.VK_0 },
        { 12, VirtualKey.VK_MINUS },
        { 13, VirtualKey.VK_EQUAL },
        { 14, VirtualKey.VK_BACKSPACE },
        { 15, VirtualKey.VK_TAB },
        { 16, VirtualKey.VK_Q },
        { 17, VirtualKey.VK_W },
        { 18, VirtualKey.VK_E },
        { 19, VirtualKey.VK_R },
        { 20, VirtualKey.VK_T },
        { 21, VirtualKey.VK_Y },
        { 22, VirtualKey.VK_U },
        { 23, VirtualKey.VK_I },
        { 24, VirtualKey.VK_O },
        { 25, VirtualKey.VK_P },
        { 26, VirtualKey.VK_LEFT_BRACKET },
        { 27, VirtualKey.VK_RIGHT_BRACKET },
        { 28, VirtualKey.VK_ENTER },
        { 29, VirtualKey.VK_LEFT_CONTROL },
        // ...
        // Fill in using KEY_* constants
    };

    private static readonly Dictionary<VirtualKey, int> VkToEvdev =
        EvdevToVk.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

    public static VirtualKey FromEvdev(int evdevCode)
        => EvdevToVk.TryGetValue(evdevCode, out var vk) ? vk : VirtualKey.VK_NONE;

    public static int ToEvdev(VirtualKey vk)
        => VkToEvdev.TryGetValue(vk, out var code) ? code : -1;
}

public static class X11KeyConverter
{
    private static readonly Dictionary<uint, VirtualKey> KeysymToVk = new()
    {
        { 0xFF1B, VirtualKey.VK_ESCAPE },      // XK_Escape
        { 0xFF08, VirtualKey.VK_BACKSPACE },   // XK_BackSpace
        { 0xFF09, VirtualKey.VK_TAB },         // XK_Tab
        { 0xFF0D, VirtualKey.VK_ENTER },       // XK_Return
        { 0x20,   VirtualKey.VK_SPACE },       // XK_space
        // ...
        // Add XK_A, XK_B, XK_C, etc.
    };

    private static readonly Dictionary<VirtualKey, uint> VkToKeysym =
        KeysymToVk.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

    public static VirtualKey FromKeysym(uint keysym)
        => KeysymToVk.TryGetValue(keysym, out var vk) ? vk : VirtualKey.VK_NONE;

    public static uint ToKeysym(VirtualKey vk)
        => VkToKeysym.TryGetValue(vk, out var ks) ? ks : 0;
}

public static class SdlGamepadConverter
{
    private static readonly Dictionary<int, VirtualKey> SdlButtonToVk = new()
    {
        // (int)SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A → VK_GAMEPAD_SOUTH, etc.
        // { (int)SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A, VirtualKey.VK_GAMEPAD_SOUTH },
        // { (int)SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_B, VirtualKey.VK_GAMEPAD_EAST },
        // ...
    };

    private static readonly Dictionary<VirtualKey, int> VkToSdlButton =
        SdlButtonToVk.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

    public static VirtualKey FromSdlButton(int button)
        => SdlButtonToVk.TryGetValue(button, out var vk) ? vk : VirtualKey.VK_NONE;

    public static int ToSdlButton(VirtualKey vk)
        => VkToSdlButton.TryGetValue(vk, out var btn) ? btn : -1;
}

public static class SdlGamepadConverter
{
    private static readonly Dictionary<int, VirtualKey> SdlButtonToVk = new()
    {
        // (int)SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A → VK_GAMEPAD_SOUTH, etc.
        // { (int)SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A, VirtualKey.VK_GAMEPAD_SOUTH },
        // { (int)SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_B, VirtualKey.VK_GAMEPAD_EAST },
        // ...
    };

    private static readonly Dictionary<VirtualKey, int> VkToSdlButton =
        SdlButtonToVk.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

    public static VirtualKey FromSdlButton(int button)
        => SdlButtonToVk.TryGetValue(button, out var vk) ? vk : VirtualKey.VK_NONE;

    public static int ToSdlButton(VirtualKey vk)
        => VkToSdlButton.TryGetValue(vk, out var btn) ? btn : -1;
}

public static class EvdevGamepadConverter
{
    private static readonly Dictionary<int, VirtualKey> EvdevButtonToVk = new()
    {
        { BTN_SOUTH, VirtualKey.VK_GAMEPAD_SOUTH },
        { BTN_EAST, VirtualKey.VK_GAMEPAD_EAST },
        { BTN_WEST, VirtualKey.VK_GAMEPAD_WEST },
        { BTN_NORTH, VirtualKey.VK_GAMEPAD_NORTH },

        { BTN_TL, VirtualKey.VK_GAMEPAD_L1 },
        { BTN_TR, VirtualKey.VK_GAMEPAD_R1 },
        { BTN_TL2, VirtualKey.VK_GAMEPAD_L2 },
        { BTN_TR2, VirtualKey.VK_GAMEPAD_R2 },

        { BTN_THUMBL, VirtualKey.VK_GAMEPAD_L3 },
        { BTN_THUMBR, VirtualKey.VK_GAMEPAD_R3 },

        { BTN_DPAD_UP, VirtualKey.VK_GAMEPAD_DPAD_UP },
        { BTN_DPAD_DOWN, VirtualKey.VK_GAMEPAD_DPAD_DOWN },
        { BTN_DPAD_LEFT, VirtualKey.VK_GAMEPAD_DPAD_LEFT },
        { BTN_DPAD_RIGHT, VirtualKey.VK_GAMEPAD_DPAD_RIGHT },
    };

    public static VirtualKey FromEvdevButton(int code)
        => EvdevButtonToVk.TryGetValue(code, out var vk) ? vk : VirtualKey.VK_NONE;
}

public static class GamepadConverter
{
    private static readonly Dictionary<int, VirtualKey> SdlToVk = new()
    {
        { (int)SDL_GameControllerButton.A, VirtualKey.VK_GAMEPAD_SOUTH },
        { (int)SDL_GameControllerButton.B, VirtualKey.VK_GAMEPAD_EAST },
        { (int)SDL_GameControllerButton.X, VirtualKey.VK_GAMEPAD_WEST },
        { (int)SDL_GameControllerButton.Y, VirtualKey.VK_GAMEPAD_NORTH },
        { (int)SDL_GameControllerButton.DPAD_UP, VirtualKey.VK_GAMEPAD_DPAD_UP },
        { (int)SDL_GameControllerButton.DPAD_DOWN, VirtualKey.VK_GAMEPAD_DPAD_DOWN },
        // ...
        // Add Steam Deck specifics via Steam Input API later
    };

    private static readonly Dictionary<VirtualKey, int> VkToSdl =
        SdlToVk.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

    public static VirtualKey FromSdl(int sdlButton)
        => SdlToVk.TryGetValue(sdlButton, out var vk) ? vk : VirtualKey.VK_NONE;

    public static int ToSdl(VirtualKey vk)
        => VkToSdl.TryGetValue(vk, out var btn) ? btn : -1;
}

public static VirtualKey ConvertLinuxInput(InputEvent ev, InputDevice dev)
{
    var type = DetectEvdevDevice(dev);
    return UnifiedKeyConverter.FromEvdev(ev.code, type);
}

public static class UnifiedKeyConverter
{
    public static VirtualKey FromEvdev(int code, InputDeviceType type)
    {
        return type switch
        {
            InputDeviceType.Keyboard => EvdevKeyConverter.FromEvdev(code),
            InputDeviceType.Gamepad  => EvdevGamepadConverter.FromEvdevButton(code),
            _ => VirtualKey.VK_NONE
        };
    }

    public static VirtualKey FromX11(uint keysym)
        => X11KeyConverter.FromKeysym(keysym);

    public static VirtualKey FromSdlScancode(int scancode)
        => SdlKeyboardConverter.FromScancode(scancode);

    public static VirtualKey FromSdlButton(int button)
        => SdlGamepadConverter.FromSdlButton(button);

    // Reverse direction
    public static int ToEvdev(VirtualKey vk, InputDeviceType type)
    {
        return type switch
        {
            InputDeviceType.Keyboard => EvdevKeyConverter.ToEvdev(vk),
            InputDeviceType.Gamepad  => EvdevGamepadConverter.ToEvdevButton(vk),
            _ => -1
        };
    }

    public static uint ToX11(VirtualKey vk)
        => X11KeyConverter.ToKeysym(vk);

    public static int ToSdlScancode(VirtualKey vk)
        => SdlKeyboardConverter.ToScancode(vk);

    public static int ToSdlButton(VirtualKey vk)
        => SdlGamepadConverter.ToSdlButton(vk);

    public static InputDeviceType DetectEvdevDevice(InputDevice dev) {
        if (dev.SupportsEventType(EV_ABS) &&
            dev.SupportsKey(BTN_SOUTH))
            return InputDeviceType.Gamepad;

        if (dev.SupportsKey(KEY_A) &&
            dev.SupportsKey(KEY_Z))
            return InputDeviceType.Keyboard;

        return InputDeviceType.Unknown;
    }

    public static VirtualKey FromSdlEvent(SDL_Event ev) {
        if (ev.type == SDL_KEYDOWN || ev.type == SDL_KEYUP)
            return FromSdlScancode(ev.key.keysym.scancode);

        if (ev.type == SDL_CONTROLLERBUTTONDOWN || ev.type == SDL_CONTROLLERBUTTONUP)
            return FromSdlButton(ev.cbutton.button);

        return VirtualKey.VK_NONE;
    }
}
