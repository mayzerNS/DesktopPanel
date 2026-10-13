using System.Windows.Input;

namespace DesktopPanel.Core.Hotkeys;

/// <summary>Кнопка мыши в сочетании: нажатие колеса, боковые (4 — «назад», 5 — «вперёд»), прокрутка колеса.</summary>
public enum MouseKey { None, Middle, X1, X2, WheelUp, WheelDown }

/// <summary>
/// Сочетание: модификаторы + клавиша или кнопка мыши. В конфиге хранится строкой "Win+C", "Ctrl+Alt+F9", "Mouse4",
/// "Alt+WheelUp". Кнопки мыши ловит MouseHotkeys (Raw Input, без хука).
/// </summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key, MouseKey Mouse = MouseKey.None)
{
    // флаги RegisterHotKey
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    public bool IsEmpty => Key == Key.None && Mouse == MouseKey.None;

    /// <summary>Кнопка мыши, а не клавиша.</summary>
    public bool IsMouse => Mouse != MouseKey.None;

    private static readonly (MouseKey Key, string Token, string Loc)[] MouseNames =
    {
        (MouseKey.Middle, "MouseMiddle", "mouse.middle"), (MouseKey.X1, "Mouse4", "mouse.x1"), (MouseKey.X2, "Mouse5", "mouse.x2"),
        (MouseKey.WheelUp, "WheelUp", "mouse.wheel_up"), (MouseKey.WheelDown, "WheelDown", "mouse.wheel_down"),
    };

    /// <summary>Для показа: модификаторы + клавиша, кнопки мыши — словами («Мышь 4», «Колесо ↑»).</summary>
    public string Display
    {
        get
        {
            if (!IsMouse) return ToString();
            var mk = Mouse;
            var name = Localization.Loc.T(MouseNames.First(m => m.Key == mk).Loc);
            var mods = ModsText(Modifiers);
            return mods.Length > 0 ? mods + "+" + name : name;
        }
    }

    private static string ModsText(ModifierKeys m)
    {
        var parts = new List<string>();
        if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        return string.Join("+", parts);
    }

    public uint NativeModifiers
    {
        get
        {
            uint m = MOD_NOREPEAT; // зажатая клавиша не шлёт событие снова и снова
            if (Modifiers.HasFlag(ModifierKeys.Alt)) m |= MOD_ALT;
            if (Modifiers.HasFlag(ModifierKeys.Control)) m |= MOD_CONTROL;
            if (Modifiers.HasFlag(ModifierKeys.Shift)) m |= MOD_SHIFT;
            if (Modifiers.HasFlag(ModifierKeys.Windows)) m |= MOD_WIN;
            return m;
        }
    }

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        if (IsEmpty) return "";
        var mods = ModsText(Modifiers);
        var mk = Mouse;
        var key = IsMouse ? MouseNames.First(m => m.Key == mk).Token : KeyName(Key);
        return mods.Length > 0 ? mods + "+" + key : key;
    }

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        var mods = ModifierKeys.None;
        var key = Key.None;
        var mouse = MouseKey.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "win": mods |= ModifierKeys.Windows; break;
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                default:
                    var m = MouseNames.FirstOrDefault(x => string.Equals(x.Token, raw, StringComparison.OrdinalIgnoreCase));
                    if (m.Key != MouseKey.None) mouse = m.Key;
                    else key = ParseKey(raw);
                    break;
            }
        }
        return new Hotkey(mods, mouse != MouseKey.None ? Key.None : key, mouse);
    }

    /// <summary>Клавиши, которые можно назначить без Ctrl/Alt/Shift/Win (они ничего не печатают).</summary>
    public static bool CanBeAlone(Key k) => k is >= Key.F1 and <= Key.F24 or Key.Pause or Key.Scroll
        or Key.MediaPlayPause or Key.MediaNextTrack or Key.MediaPreviousTrack or Key.MediaStop;

    /// <summary>
    /// Сочетание можно назначать: есть модификатор или клавиша из CanBeAlone. Кнопки мыши — только с модификатором:
    /// сами по себе колесо и боковые нужны в играх и браузере, а подавить их обычное действие без хука мыши нельзя.
    /// </summary>
    public bool IsValid => IsMouse
        ? Modifiers != ModifierKeys.None
        : !IsEmpty && (Modifiers != ModifierKeys.None || CanBeAlone(Key));

    /// <summary>Клавиши-модификаторы сами по себе сочетанием не считаются.</summary>
    public static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (int)(k - Key.NumPad0),
        Key.OemTilde => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Oem2 => "/",
        Key.Oem1 => ";",
        Key.Oem7 => "'",
        Key.Oem4 => "[",
        Key.Oem6 => "]",
        Key.Oem5 => "\\",
        Key.Space => "Space",
        Key.Return => "Enter",
        _ => k.ToString(),
    };

    private static Key ParseKey(string s)
    {
        if (s.Length == 1 && char.IsDigit(s[0])) return Key.D0 + (s[0] - '0');
        if (s.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && s.Length == 4 && char.IsDigit(s[3]))
            return Key.NumPad0 + (s[3] - '0');
        var special = s switch
        {
            "`" => Key.OemTilde, "-" => Key.OemMinus, "=" => Key.OemPlus, "," => Key.OemComma, "." => Key.OemPeriod,
            "/" => Key.Oem2, ";" => Key.Oem1, "'" => Key.Oem7, "[" => Key.Oem4, "]" => Key.Oem6, "\\" => Key.Oem5,
            "Enter" => Key.Return,
            _ => Key.None,
        };
        if (special != Key.None) return special;
        return Enum.TryParse<Key>(s, ignoreCase: true, out var k) ? k : Key.None;
    }
}
