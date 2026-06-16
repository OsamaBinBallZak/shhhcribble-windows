namespace Shhhcribble.Windows.Input;

[Flags]
public enum HotModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A selectable global-hotkey preset: a trigger key plus required modifiers.
/// <para>
/// <c>Symbol</c> is the compact Unicode representation used in the tray usage-hint
/// ("Tap Ctrl+Space to start…") and the Settings About caption — analogous to the
/// macOS <c>HotkeyOption.symbol</c> field (e.g. "⌥Space").  Keep it short so it
/// fits inline in a menu item or a single sentence.
/// </para>
/// </summary>
public sealed record HotkeyOption(string Id, string Label, string Symbol, uint Vk, HotModifiers Modifiers);

/// <summary>
/// Preset hotkeys — the Windows analogue of the macOS app's hotkey registry.
/// Note Alt+Space is deliberately avoided (it opens the window system menu on
/// Windows); Ctrl+Space is the default (stands in for macOS "optSpace").
/// Ctrl+Shift+Space is a deliberate Windows addition for IME environments where
/// Ctrl+Space conflicts.
/// </summary>
public static class HotkeyOptions
{
    public const uint VK_SPACE = 0x20;
    public const uint VK_OEM_3 = 0xC0; // backtick / grave `

    public static readonly IReadOnlyList<HotkeyOption> All = new[]
    {
        new HotkeyOption("ctrlSpace",      "Ctrl + Space",         "Ctrl+Space",       VK_SPACE, HotModifiers.Ctrl),
        new HotkeyOption("ctrlShiftSpace", "Ctrl + Shift + Space", "Ctrl+Shift+Space", VK_SPACE, HotModifiers.Ctrl | HotModifiers.Shift),
        new HotkeyOption("altBacktick",    "Alt + ` (backtick)",   "Alt+`",            VK_OEM_3, HotModifiers.Alt),
        new HotkeyOption("ctrlAltSpace",   "Ctrl + Alt + Space",   "Ctrl+Alt+Space",   VK_SPACE, HotModifiers.Ctrl | HotModifiers.Alt),
    };

    public const string DefaultId = "ctrlSpace";

    public static HotkeyOption ById(string id) => All.FirstOrDefault(h => h.Id == id) ?? All[0];
}
