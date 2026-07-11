using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MicMixer.Overlay;

public enum OverlayIndicatorState
{
    Hidden,
    Normal,
    Modded,
    Muted
}

/// <summary>
/// Small always-on-top status dot in the top-right corner of the primary screen,
/// mirroring the tray icon state (normal / modded / push-to-talk muted) so the
/// state is visible on top of games without alt-tabbing.
///
/// Deliberately inert: click-through (WS_EX_TRANSPARENT), never focusable or
/// activatable (WS_EX_NOACTIVATE + ShowActivated=false), hidden from Alt-Tab
/// (WS_EX_TOOLWINDOW). A low-frequency timer re-asserts topmost because some
/// games and launchers push themselves over the topmost band; the re-assert
/// slots this window *below* any visible StreamDecky topmost window so that
/// overlay stays on top when both run at the same time.
/// </summary>
public sealed class OverlayIndicatorWindow : Window
{
    private const double DotDiameter = 34;
    private const double EdgeMargin = 10;
    private const string StreamDeckyProcessName = "StreamDecky";
    private static readonly TimeSpan TopmostInterval = TimeSpan.FromSeconds(3);

    // Same glyphs as the MicIcon / ModdedMicIcon / MicOffIcon window resources.
    private static readonly Geometry MicGlyph = CreateGlyph(
        "M12,2A3,3 0 0,1 15,5V11A3,3 0 0,1 12,14A3,3 0 0,1 9,11V5A3,3 0 0,1 12,2M19,11C19,14.53 16.39,17.44 13,17.93V21H11V17.93C7.61,17.44 5,14.53 5,11H7A5,5 0 0,0 12,16A5,5 0 0,0 17,11H19Z");
    private static readonly Geometry ModdedMicGlyph = CreateGlyph(
        "M12,2A3,3 0 0,1 15,5V11A3,3 0 0,1 12,14A3,3 0 0,1 9,11V5A3,3 0 0,1 12,2M19,11C19,14.53 16.39,17.44 13,17.93V21H11V17.93C7.61,17.44 5,14.53 5,11H7A5,5 0 0,0 12,16A5,5 0 0,0 17,11H19M3,9H1V15H3V9M21,9H23V15H21V9");
    private static readonly Geometry MicOffGlyph = CreateGlyph(
        "M19,11C19,12.19 18.66,13.3 18.1,14.28L16.87,13.05C17.14,12.43 17.3,11.74 17.3,11H19M15,11.16L9,5.18V5A3,3 0 0,1 12,2A3,3 0 0,1 15,5V11L15,11.16M4.27,3L21,19.73L19.73,21L15.54,16.81C14.77,17.27 13.91,17.58 13,17.72V21H11V17.72C7.72,17.23 5,14.41 5,11H6.7C6.7,14 9.24,16.1 12,16.1C12.81,16.1 13.6,15.91 14.31,15.58L12.65,13.92L12,14A3,3 0 0,1 9,11V10.28L3,4.27L4.27,3Z");

    // Same palette as the tray icon states.
    private static readonly System.Windows.Media.Brush NormalBrush = CreateFrozenBrush(0x0F, 0x76, 0x6E);   // teal
    private static readonly System.Windows.Media.Brush ModdedBrush = CreateFrozenBrush(0xC2, 0x41, 0x0C);   // orange
    private static readonly System.Windows.Media.Brush MutedBrush = CreateFrozenBrush(0xB9, 0x1C, 0x1C);    // red

    private readonly Ellipse _dot;
    private readonly System.Windows.Shapes.Path _glyph;
    private readonly DispatcherTimer _topmostTimer;
    private OverlayIndicatorState _state = OverlayIndicatorState.Hidden;

    public OverlayIndicatorWindow()
    {
        Title = "MicMixer-indikator";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;

        // Room for the drop shadow around the dot.
        Width = DotDiameter + 10;
        Height = DotDiameter + 10;
        Opacity = 0.72;

        _dot = new Ellipse
        {
            Width = DotDiameter,
            Height = DotDiameter,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = NormalBrush,
            Effect = new DropShadowEffect { BlurRadius = 7, ShadowDepth = 1, Opacity = 0.35 }
        };

        _glyph = new System.Windows.Shapes.Path
        {
            Data = MicGlyph,
            Fill = System.Windows.Media.Brushes.White,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var root = new Grid { IsHitTestVisible = false };
        root.Children.Add(_dot);
        root.Children.Add(_glyph);
        Content = root;

        _topmostTimer = new DispatcherTimer { Interval = TopmostInterval };
        _topmostTimer.Tick += (_, _) =>
        {
            // Reposition too: resolution or work-area changes would otherwise
            // strand the dot mid-screen or off-screen.
            PositionTopRight();
            EnsureTopmost();
        };
    }

    /// <summary>Shows, hides, and recolors the dot. No-op when the state is unchanged.</summary>
    public void SetState(OverlayIndicatorState state)
    {
        if (state == _state)
        {
            return;
        }

        _state = state;

        if (state == OverlayIndicatorState.Hidden)
        {
            _topmostTimer.Stop();
            Hide();
            return;
        }

        (_dot.Fill, _glyph.Data) = state switch
        {
            OverlayIndicatorState.Modded => (ModdedBrush, ModdedMicGlyph),
            OverlayIndicatorState.Muted => (MutedBrush, MicOffGlyph),
            _ => (NormalBrush, MicGlyph)
        };

        if (!IsVisible)
        {
            PositionTopRight();
            Show();
        }

        EnsureTopmost();
        _topmostTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        base.OnClosed(e);
    }

    private void PositionTopRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - EdgeMargin;
        Top = workArea.Top + EdgeMargin;
    }

    /// <summary>
    /// Re-asserts the topmost position. Slots below a visible StreamDecky
    /// topmost window when one exists (its overlay must stay clickable above
    /// this dot); otherwise goes to the top of the topmost band.
    /// </summary>
    private void EnsureTopmost()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !IsVisible)
        {
            return;
        }

        IntPtr streamDecky = FindStreamDeckyTopmostWindow(hwnd);
        SetWindowPos(
            hwnd,
            streamDecky != IntPtr.Zero ? streamDecky : HWND_TOPMOST,
            0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Highest visible topmost window owned by a StreamDecky process, or zero.</summary>
    private static IntPtr FindStreamDeckyTopmostWindow(IntPtr self)
    {
        var streamDeckyPids = new HashSet<uint>();

        try
        {
            foreach (var process in Process.GetProcessesByName(StreamDeckyProcessName))
            {
                streamDeckyPids.Add((uint)process.Id);
                process.Dispose();
            }
        }
        catch
        {
            // Process enumeration can fail under restricted accounts; the dot
            // then simply claims plain topmost.
        }

        if (streamDeckyPids.Count == 0)
        {
            return IntPtr.Zero;
        }

        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == self || !IsWindowVisible(hwnd))
            {
                return true;
            }

            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) == 0)
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!streamDeckyPids.Contains(pid))
            {
                return true;
            }

            // EnumWindows walks top-down, so the first hit is the highest one.
            found = hwnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private static Geometry CreateGlyph(string pathData)
    {
        var geometry = Geometry.Parse(pathData);
        geometry.Freeze();
        return geometry;
    }

    private static System.Windows.Media.Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // --- Win32 interop ---

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }
}
