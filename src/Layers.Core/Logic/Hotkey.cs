using System.Globalization;

namespace Layers.Core.Logic;

/// <summary>
/// Modifier keys of a global shortcut.
/// </summary>
/// <remarks>
/// The values are Win32's <c>MOD_*</c> flags, so they go to <c>RegisterHotKey</c> as they are.
/// </remarks>
[Flags]
public enum HotkeyModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Alt (<c>MOD_ALT</c>).</summary>
    Alt = 0x1,

    /// <summary>Ctrl (<c>MOD_CONTROL</c>).</summary>
    Ctrl = 0x2,

    /// <summary>Shift (<c>MOD_SHIFT</c>).</summary>
    Shift = 0x4,

    /// <summary>The Windows key (<c>MOD_WIN</c>).</summary>
    Win = 0x8,
}

/// <summary>
/// A global shortcut such as Ctrl+Alt+J: modifiers plus one key.
/// </summary>
/// <remarks>
/// The key is a Win32 virtual-key code for A to Z, 0 to 9, F1 to F24, Space, or Delete. At least one of Ctrl, Alt,
/// or Win is required, so a shortcut never swallows plain typing (Shift+J is a capital J). Text is always written
/// Ctrl, Alt, Shift, Win, then the key, which is how settings store it. Adapted from MagicBuds.
/// </remarks>
/// <param name="Modifiers">The modifiers held.</param>
/// <param name="Key">The virtual-key code.</param>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int Key)
{
    private const HotkeyModifiers AllModifiers = HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win;
    private const HotkeyModifiers Anchors      = HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win;

    /// <summary>Gets the shortcut used until the user changes it: Win+Shift+L.</summary>
    public static Hotkey Default => new(HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x4C);

    /// <summary>
    /// Checks a pressed combination and makes a shortcut from it.
    /// </summary>
    /// <remarks>
    /// Used by the shortcut dialog, which tracks modifiers and a key separately.
    /// </remarks>
    /// <param name="modifiers">The modifiers held.</param>
    /// <param name="key">The virtual-key code.</param>
    /// <param name="hotkey">The shortcut when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when Ctrl, Alt, or Win is held and the key is one a shortcut may use.</returns>
    public static bool TryCreate(HotkeyModifiers modifiers, int key, out Hotkey hotkey)
    {
        hotkey = new Hotkey(modifiers, key);
        return (modifiers & Anchors) != 0
            && (modifiers & ~AllModifiers) == 0
            && KeyName(key) is not null;
    }

    /// <summary>
    /// Reads text such as "Ctrl+Alt+J".
    /// </summary>
    /// <remarks>
    /// Any order and any case. "Control" and "Windows" work too, and "Del" for Delete. A repeated modifier or a
    /// second key is refused.
    /// </remarks>
    /// <param name="text">The stored text.</param>
    /// <param name="hotkey">The shortcut when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the text is a valid shortcut.</returns>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        int? key      = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim().ToUpperInvariant();
            HotkeyModifiers? modifier = part switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Ctrl,
                "ALT"               => HotkeyModifiers.Alt,
                "SHIFT"             => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS"  => HotkeyModifiers.Win,
                _                   => null,
            };

            // A Modifier, Each Only Once
            if (modifier is { } m)
            {
                if ((modifiers & m) != 0)
                {
                    return false;
                }

                modifiers |= m;
                continue;
            }

            // The One Key
            if (key is not null || KeyCode(part) is not { } code)
            {
                return false;
            }

            key = code;
        }

        return key is { } k && TryCreate(modifiers, k, out hotkey);
    }

    /// <summary>
    /// Formats the shortcut as settings store it.
    /// </summary>
    /// <remarks>
    /// Ctrl, Alt, Shift, Win, then the key, joined with "+".
    /// </remarks>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(Key) ?? "?");
        return string.Join("+", parts);
    }

    // A To Z And 0 To 9 Are Their Own Virtual-Key Codes; F1 To F24 Are 0x70 To 0x87
    internal static string? KeyName(int key) => key switch
    {
        0x20                => "Space",
        0x2E                => "Delete",
        >= 0x41 and <= 0x5A => new string((char)key, 1),
        >= 0x30 and <= 0x39 => new string((char)key, 1),
        >= 0x70 and <= 0x87 => string.Create(CultureInfo.InvariantCulture, $"F{key - 0x6F}"),
        _                   => null,
    };

    private static int? KeyCode(string name)
    {
        // Space And Delete (The Name Is Upper-Cased)
        if (name is "SPACE" or "DELETE" or "DEL")
        {
            return name == "SPACE" ? 0x20 : 0x2E;
        }

        if (name.Length == 1 && (char.IsAsciiLetterUpper(name[0]) || char.IsAsciiDigit(name[0])))
        {
            return name[0];
        }

        if (name.Length is 2 or 3 && name[0] == 'F' && int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 24)
        {
            return 0x6F + n;
        }

        return null;
    }
}
