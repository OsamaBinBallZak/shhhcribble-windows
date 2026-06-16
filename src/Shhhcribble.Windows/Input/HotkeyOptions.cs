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

/// <summary>A selectable global-hotkey preset: a trigger key plus required modifiers.</summary>
public sealed record HotkeyOption(string Id, string Label, uint Vk, HotModifiers Modifiers);

/// <summary>
/// Preset hotkeys — the Windows analogue of the macOS app's hotkey registry.
/// Note Alt+Space is deliberately avoided (it opens the window system menu on
/// Windows); Ctrl+Space is the default.
/// </summary>
public static class HotkeyOptions
{
    public const uint VK_SPACE = 0x20;
    public const uint VK_OEM_3 = 0xC0; // backtick / grave `

    public static readonly IReadOnlyList<HotkeyOption> All = new[]
    {
        new HotkeyOption("ctrlSpace",      "Ctrl + Space",         VK_SPACE, HotModifiers.Ctrl),
        new HotkeyOption("ctrlShiftSpace", "Ctrl + Shift + Space", VK_SPACE, HotModifiers.Ctrl | HotModifiers.Shift),
        new HotkeyOption("altBacktick",    "Alt + ` (backtick)",   VK_OEM_3, HotModifiers.Alt),
        new HotkeyOption("ctrlAltSpace",   "Ctrl + Alt + Space",   VK_SPACE, HotModifiers.Ctrl | HotModifiers.Alt),
    };

    public const string DefaultId = "ctrlSpace";

    public static HotkeyOption ById(string id) => All.FirstOrDefault(h => h.Id == id) ?? All[0];
}
