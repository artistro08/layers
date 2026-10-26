// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
//
// Ported from PowerToys: src/settings-ui/Settings.UI.Library/HotkeySettings.cs (https://github.com/microsoft/PowerToys).
// By way of MagicBuds, whose changes are kept: no JSON or conflict properties (the app stores Hotkey text and checks
// conflicts itself), IsValid also requires Ctrl, Alt, or Win and a key Hotkey accepts (Shift+J is a capital J),
// Windows' own Win+L and Alt+F4 are refused, key names come from Hotkey's table instead of the keyboard layout, and
// ToString joins with "+".

using System.Globalization;

namespace Layers.Core.Logic;

/// <summary>
/// A key combination as the shortcut picker tracks it.
/// </summary>
/// <remarks>
/// Win, Ctrl, Alt, Shift, and one key (a Win32 virtual-key code, 0 for none). <see cref="ToHotkey"/> turns a valid
/// one into the <see cref="Hotkey"/> the app registers.
/// </remarks>
public sealed record HotkeySettings
{
    private const int VkTab = 0x09;

    /// <summary>Creates an empty combination.</summary>
    public HotkeySettings()
    {
    }

    /// <summary>Creates a combination.</summary>
    /// <param name="win">Whether the Windows key is held.</param>
    /// <param name="ctrl">Whether Ctrl is held.</param>
    /// <param name="alt">Whether Alt is held.</param>
    /// <param name="shift">Whether Shift is held.</param>
    /// <param name="code">The key's virtual-key code, 0 for none.</param>
    public HotkeySettings(bool win, bool ctrl, bool alt, bool shift, int code)
    {
        Win   = win;
        Ctrl  = ctrl;
        Alt   = alt;
        Shift = shift;
        Code  = code;
    }

    /// <summary>Gets or sets a value indicating whether the Windows key is held.</summary>
    public bool Win { get; set; }

    /// <summary>Gets or sets a value indicating whether Ctrl is held.</summary>
    public bool Ctrl { get; set; }

    /// <summary>Gets or sets a value indicating whether Alt is held.</summary>
    public bool Alt { get; set; }

    /// <summary>Gets or sets a value indicating whether Shift is held.</summary>
    public bool Shift { get; set; }

    /// <summary>Gets or sets the key's virtual-key code, 0 for none.</summary>
    public int Code { get; set; }

    /// <summary>
    /// Gets a value indicating whether the combination can collide with AltGr.
    /// </summary>
    /// <remarks>
    /// Ctrl and Alt without Win act as AltGr on some international keyboards, so the shortcut can take away
    /// characters typed with AltGr.
    /// </remarks>
    public bool IsAltGrRisk => Ctrl && Alt && !Win && Code > 0;

    /// <summary>Makes the picker's combination from a saved shortcut.</summary>
    /// <param name="hotkey">The shortcut.</param>
    /// <returns>The combination.</returns>
    public static HotkeySettings FromHotkey(Hotkey hotkey) => new(
        hotkey.Modifiers.HasFlag(HotkeyModifiers.Win),
        hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl),
        hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt),
        hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift),
        hotkey.Key);

    /// <summary>Gets the shortcut to register, or <see langword="null"/> when the combination is not <see cref="IsValid"/>.</summary>
    /// <returns>The shortcut, or <see langword="null"/>.</returns>
    public Hotkey? ToHotkey() => IsValid() && Hotkey.TryCreate(Modifiers, Code, out var hotkey) ? hotkey : null;

    /// <summary>
    /// Gets the key caps in PowerToys' order.
    /// </summary>
    /// <remarks>
    /// Win (92) and Shift (16) as key codes so they draw as glyphs, Ctrl and Alt as text, then the key.
    /// </remarks>
    /// <returns>The key caps.</returns>
    public List<object> GetKeysList()
    {
        List<object> shortcutList = [];

        if (Win)
        {
            shortcutList.Add(92);
        }

        if (Ctrl)
        {
            shortcutList.Add("Ctrl");
        }

        if (Alt)
        {
            shortcutList.Add("Alt");
        }

        if (Shift)
        {
            shortcutList.Add(16);
        }

        if (Code > 0)
        {
            switch (Code)
            {
                // Arrow Keys Draw As Glyphs
                case 37:
                case 38:
                case 39:
                case 40:
                    shortcutList.Add(Code);
                    break;
                default:
                    shortcutList.Add(KeyName(Code));
                    break;
            }
        }

        return shortcutList;
    }

    /// <summary>
    /// Tells whether the app can register this combination.
    /// </summary>
    /// <remarks>
    /// Ctrl, Alt, or Win (Shift optional) with a letter, a number, F1 to F24, Space, or Delete, and not Win+L or
    /// Alt+F4.
    /// </remarks>
    /// <returns><see langword="true"/> when valid.</returns>
    public bool IsValid()
    {
        if (IsAccessibleShortcut())
        {
            return false;
        }

        if (!(Alt || Ctrl || Win) || Code == 0 || !Hotkey.TryCreate(Modifiers, Code, out var hotkey))
        {
            return false;
        }

        return hotkey.ToString() is not ("Win+L" or "Alt+F4");
    }

    /// <summary>
    /// Explains why the picker refuses the combination.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> when it is valid or nothing is pressed yet.
    /// </remarks>
    /// <returns>The reason, or <see langword="null"/>.</returns>
    public string? InvalidReason()
    {
        if (IsEmpty() || IsValid())
        {
            return null;
        }

        if (!(Alt || Ctrl || Win))
        {
            return "Invalid shortcut. It must start with the Windows key, Ctrl, or Alt.";
        }

        // Windows' Own: Win+L And Alt+F4
        if (ToString() is "Win+L" or "Alt+F4")
        {
            return $"Invalid shortcut. Windows keeps {ToString()} for itself.";
        }

        return "Invalid shortcut. It must end with a letter, a number, an F key, Space, or Delete.";
    }

    /// <summary>Tells whether no modifier and no key are set.</summary>
    /// <returns><see langword="true"/> when empty.</returns>
    public bool IsEmpty() => !Alt && !Ctrl && !Win && !Shift && Code == 0;

    /// <summary>Tells whether this is Tab or Shift+Tab, which move focus and are never a shortcut.</summary>
    /// <returns><see langword="true"/> for Tab or Shift+Tab.</returns>
    public bool IsAccessibleShortcut() =>
        (!Alt && !Ctrl && !Win && Shift && Code == VkTab)
        || (!Alt && !Ctrl && !Win && !Shift && Code == VkTab);

    /// <summary>Formats the key caps' text joined with "+", for example "Win+Shift+F7". Empty for no keys.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        List<string> parts = [];
        if (Win)
        {
            parts.Add("Win");
        }

        if (Ctrl)
        {
            parts.Add("Ctrl");
        }

        if (Alt)
        {
            parts.Add("Alt");
        }

        if (Shift)
        {
            parts.Add("Shift");
        }

        if (Code > 0)
        {
            parts.Add(KeyName(Code));
        }

        return string.Join("+", parts);
    }

    private HotkeyModifiers Modifiers =>
        (Win ? HotkeyModifiers.Win : 0) | (Ctrl ? HotkeyModifiers.Ctrl : 0) | (Alt ? HotkeyModifiers.Alt : 0) | (Shift ? HotkeyModifiers.Shift : 0);

    // A Key's Name: Hotkey's For The Keys A Shortcut May Use, A Plain Name For Common Others
    private static string KeyName(int key) => Hotkey.KeyName(key) ?? key switch
    {
        0x08  => "Backspace",
        VkTab => "Tab",
        0x0D  => "Enter",
        0x1B  => "Esc",
        0x21  => "Page Up",
        0x22  => "Page Down",
        0x23  => "End",
        0x24  => "Home",
        0x25  => "Left",
        0x26  => "Up",
        0x27  => "Right",
        0x28  => "Down",
        0x2D  => "Insert",
        _     => string.Create(CultureInfo.InvariantCulture, $"Key {key:X2}"),
    };
}
