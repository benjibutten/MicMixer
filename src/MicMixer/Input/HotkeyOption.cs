using System.Windows.Input;

namespace MicMixer.Input;

public enum HotkeyDeviceKind
{
    Keyboard,
    Mouse
}

public enum MouseHotkeyCode
{
    LeftButton = 1,
    RightButton = 2,
    MiddleButton = 3,
    XButton1 = 4,
    XButton2 = 5
}

public sealed class HotkeyBinding
{
    private readonly HashSet<int> _codes;

    private HotkeyBinding(string serializedValue, string displayName, HotkeyDeviceKind deviceKind, IEnumerable<int> codes)
    {
        SerializedValue = serializedValue;
        DisplayName = displayName;
        DeviceKind = deviceKind;
        _codes = new HashSet<int>(codes);
    }

    public static HotkeyBinding Default => FromModifier("alt");

    public string SerializedValue { get; }

    public string DisplayName { get; }

    public HotkeyDeviceKind DeviceKind { get; }

    public IReadOnlyCollection<int> Codes => _codes;

    public bool MatchesKeyboard(int virtualKey)
    {
        return DeviceKind == HotkeyDeviceKind.Keyboard && _codes.Contains(virtualKey);
    }

    public bool MatchesMouse(int mouseCode)
    {
        return DeviceKind == HotkeyDeviceKind.Mouse && _codes.Contains(mouseCode);
    }

    public static HotkeyBinding Parse(string? serializedValue)
    {
        if (string.IsNullOrWhiteSpace(serializedValue))
        {
            return Default;
        }

        if (!serializedValue.Contains(':'))
        {
            return ParseLegacy(serializedValue) ?? Default;
        }

        string[] parts = serializedValue.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return Default;
        }

        if (string.Equals(parts[0], "keyboard", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length == 2)
            {
                return FromModifier(parts[1]);
            }

            if (parts.Length == 3 && string.Equals(parts[1], "vk", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[2], out int virtualKey))
            {
                return FromKeyboardVirtualKey(virtualKey);
            }
        }

        if (string.Equals(parts[0], "mouse", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
        {
            return parts[1].ToLowerInvariant() switch
            {
                "left" => FromMouseCode(MouseHotkeyCode.LeftButton),
                "right" => FromMouseCode(MouseHotkeyCode.RightButton),
                "middle" => FromMouseCode(MouseHotkeyCode.MiddleButton),
                "x1" => FromMouseCode(MouseHotkeyCode.XButton1),
                "x2" => FromMouseCode(MouseHotkeyCode.XButton2),
                _ => Default
            };
        }

        return Default;
    }

    public static HotkeyBinding FromKeyboardKey(Key key)
    {
        return key switch
        {
            Key.LeftAlt or Key.RightAlt => FromModifier("alt"),
            Key.LeftCtrl or Key.RightCtrl => FromModifier("ctrl"),
            Key.LeftShift or Key.RightShift => FromModifier("shift"),
            _ => FromKeyboardVirtualKey(KeyInterop.VirtualKeyFromKey(key))
        };
    }

    public static HotkeyBinding FromMouseButton(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => FromMouseCode(MouseHotkeyCode.LeftButton),
            MouseButton.Right => FromMouseCode(MouseHotkeyCode.RightButton),
            MouseButton.Middle => FromMouseCode(MouseHotkeyCode.MiddleButton),
            MouseButton.XButton1 => FromMouseCode(MouseHotkeyCode.XButton1),
            MouseButton.XButton2 => FromMouseCode(MouseHotkeyCode.XButton2),
            _ => Default
        };
    }

    private static HotkeyBinding FromKeyboardVirtualKey(int virtualKey)
    {
        return virtualKey switch
        {
            0xA4 or 0xA5 => FromModifier("alt"),
            0xA2 or 0xA3 => FromModifier("ctrl"),
            0xA0 or 0xA1 => FromModifier("shift"),
            _ => new HotkeyBinding($"keyboard:vk:{virtualKey}", GetKeyboardDisplayName(virtualKey), HotkeyDeviceKind.Keyboard, new[] { virtualKey })
        };
    }

    private static HotkeyBinding FromModifier(string modifier)
    {
        return modifier.ToLowerInvariant() switch
        {
            "alt" => new HotkeyBinding("keyboard:alt", "Alt", HotkeyDeviceKind.Keyboard, new[] { 0xA4, 0xA5 }),
            "ctrl" => new HotkeyBinding("keyboard:ctrl", "Ctrl", HotkeyDeviceKind.Keyboard, new[] { 0xA2, 0xA3 }),
            "shift" => new HotkeyBinding("keyboard:shift", "Shift", HotkeyDeviceKind.Keyboard, new[] { 0xA0, 0xA1 }),
            _ => Default
        };
    }

    private static HotkeyBinding FromMouseCode(MouseHotkeyCode mouseCode)
    {
        return mouseCode switch
        {
            MouseHotkeyCode.LeftButton => new HotkeyBinding("mouse:left", "Vänster musknapp", HotkeyDeviceKind.Mouse, new[] { (int)MouseHotkeyCode.LeftButton }),
            MouseHotkeyCode.RightButton => new HotkeyBinding("mouse:right", "Höger musknapp", HotkeyDeviceKind.Mouse, new[] { (int)MouseHotkeyCode.RightButton }),
            MouseHotkeyCode.MiddleButton => new HotkeyBinding("mouse:middle", "Mittenknapp", HotkeyDeviceKind.Mouse, new[] { (int)MouseHotkeyCode.MiddleButton }),
            MouseHotkeyCode.XButton1 => new HotkeyBinding("mouse:x1", "Musknapp X1", HotkeyDeviceKind.Mouse, new[] { (int)MouseHotkeyCode.XButton1 }),
            MouseHotkeyCode.XButton2 => new HotkeyBinding("mouse:x2", "Musknapp X2", HotkeyDeviceKind.Mouse, new[] { (int)MouseHotkeyCode.XButton2 }),
            _ => Default
        };
    }

    private static HotkeyBinding? ParseLegacy(string legacyValue)
    {
        string normalized = legacyValue.Trim();

        if (string.Equals(normalized, "Alt", StringComparison.OrdinalIgnoreCase))
        {
            return FromModifier("alt");
        }

        if (string.Equals(normalized, "Ctrl", StringComparison.OrdinalIgnoreCase))
        {
            return FromModifier("ctrl");
        }

        if (string.Equals(normalized, "Shift", StringComparison.OrdinalIgnoreCase))
        {
            return FromModifier("shift");
        }

        if (string.Equals(normalized, "Space", StringComparison.OrdinalIgnoreCase))
        {
            return FromKeyboardVirtualKey(0x20);
        }

        if (string.Equals(normalized, "Tab", StringComparison.OrdinalIgnoreCase))
        {
            return FromKeyboardVirtualKey(0x09);
        }

        if (string.Equals(normalized, "Escape", StringComparison.OrdinalIgnoreCase))
        {
            return FromKeyboardVirtualKey(0x1B);
        }

        if (normalized.Length == 1)
        {
            char character = char.ToUpperInvariant(normalized[0]);
            if (char.IsLetterOrDigit(character))
            {
                return FromKeyboardVirtualKey(character);
            }
        }

        if (normalized.Length == 2 && normalized[0] == 'D' && char.IsDigit(normalized[1]))
        {
            return FromKeyboardVirtualKey(normalized[1]);
        }

        if (normalized.StartsWith('F') && int.TryParse(normalized[1..], out int functionNumber) && functionNumber is >= 1 and <= 24)
        {
            return FromKeyboardVirtualKey(0x70 + functionNumber - 1);
        }

        return null;
    }

    private static string GetKeyboardDisplayName(int virtualKey)
    {
        if (virtualKey >= 'A' && virtualKey <= 'Z')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey >= '0' && virtualKey <= '9')
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x70 + 1}";
        }

        return virtualKey switch
        {
            0x20 => "Mellanslag",
            0x09 => "Tab",
            0x1B => "Escape",
            0x0D => "Enter",
            0x08 => "Backspace",
            0x25 => "Pil vänster",
            0x26 => "Pil upp",
            0x27 => "Pil höger",
            0x28 => "Pil ned",
            _ => KeyInterop.KeyFromVirtualKey(virtualKey) switch
            {
                Key.None => $"VK {virtualKey}",
                var key => key.ToString()
            }
        };
    }
}