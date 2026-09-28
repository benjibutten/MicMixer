namespace MicMixer.Input;

/// <summary>One of the function keys F13–F24.</summary>
internal sealed record FunctionKey(string Name, ushort VirtualKey)
{
    // They type no text and few programs react to them, so another app can bind one
    // without clashing with anything.
    private const ushort VkF13 = 0x7C;

    public static IReadOnlyList<FunctionKey> All { get; } =
        Enumerable.Range(0, 12).Select(i => new FunctionKey($"F{13 + i}", (ushort)(VkF13 + i))).ToArray();

    public static FunctionKey Default => All[^1];

    /// <summary>The key named <paramref name="name"/>, or <see cref="Default"/> when there is none.</summary>
    public static FunctionKey Parse(string? name) =>
        All.FirstOrDefault(key => string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Default;

    /// <summary>Injects a key-down or key-up for this key.</summary>
    public void Send(bool down)
    {
        // Programs that read raw keyboard input identify keys by scan code; without
        // one the event reaches them as key 0.
        KeyInjector.Send(new KeyEvent(VirtualKey, KeyInjector.ScanCodeOf(VirtualKey), down ? 0 : KeyInjector.KeyUp));
    }

    /// <summary>True while Windows has this key down, whether pressed or injected.</summary>
    public bool IsDown => KeyInjector.IsKeyDown(VirtualKey);
}
