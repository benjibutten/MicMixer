using System.Windows;
using MicMixer.Input;

namespace MicMixer.UI;

/// <summary>One hotkey in a list with Change and Remove buttons, as the settings window and the setup guide show it.</summary>
internal sealed record HotkeyRow(int Index, string Name, string ChangeText, Visibility RemoveVisibility)
{
    public const string CapturingText = "Press now…";

    /// <summary>
    /// The rows for <paramref name="hotkeys"/>. The row at <paramref name="capturingIndex"/>
    /// waits for a key, and Remove shows only while more than one hotkey is left.
    /// </summary>
    public static List<HotkeyRow> For(IReadOnlyList<HotkeyBinding> hotkeys, int capturingIndex) =>
        [.. hotkeys.Select((binding, index) => new HotkeyRow(
            index,
            binding.DisplayName,
            index == capturingIndex ? CapturingText : "Change",
            hotkeys.Count > 1 ? Visibility.Visible : Visibility.Collapsed))];

    /// <summary>All hotkeys by name, for text that tells the user what to hold.</summary>
    public static string Names(IEnumerable<HotkeyBinding> hotkeys) =>
        string.Join(" or ", hotkeys.Select(binding => binding.DisplayName));
}
