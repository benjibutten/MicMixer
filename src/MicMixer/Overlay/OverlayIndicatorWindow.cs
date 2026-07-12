using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using MicMixer.UI;

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
/// While music is actually routed into the mic channel, expanding "sound wave"
/// rings pulse around the dot. An optional level meter to the left of the dot
/// shows the complete outgoing mix level (mic + music, post push-to-talk gate)
/// and whether it is good (green), too low or very hot (amber), or near
/// clipping (red). The ring animation clocks are fully torn down while no music
/// plays and are capped at a low frame rate, because every animation frame of a
/// layered window re-uploads its bitmap to the compositor.
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
    private const double DotZoneSize = 58;   // dot plus room for the ripple rings around it
    private const double MeterWidth = 8;
    private const double MeterHeight = 34;
    private const double MeterGap = 5;
    private const double MeterInnerHeight = MeterHeight - 4; // shell border + padding
    private const double EdgeMargin = 10;
    private const string StreamDeckyProcessName = "StreamDecky";
    private static readonly TimeSpan TopmostInterval = TimeSpan.FromSeconds(3);

    private const double RippleMaxScale = 1.6;
    private const double RippleStartOpacity = 0.85;
    private const double RippleStrokeThickness = 2.75;
    private const int RippleFrameRate = 24;
    private static readonly TimeSpan RipplePeriod = TimeSpan.FromSeconds(1.9);

    // The meter judges perceived loudness (block RMS on a dB scale), not sample
    // peaks: limited music runs its peaks near full scale at any volume, so a
    // peak meter would just mirror the volume slider while the music sounds far
    // louder than speech at the same peak. Sample peak is only used for the
    // clip-red flash. The dB window maps -42 dBFS (bottom) to -6 dBFS (top).
    private const float MeterFloorDb = -42f;
    private const float MeterCeilingDb = -6f;
    private const float ClipThreshold = 0.97f;
    private static readonly TimeSpan ClipHold = TimeSpan.FromSeconds(1.2);

    // Color ramp positions on that normalized loudness scale: amber (too low)
    // fades into green (good), then amber again (hot) and red (way too loud).
    // The bands overlap so the color glides through the boundaries instead of
    // snapping at a threshold. In dBFS: -36 / -30 / -18 / -13 / -9.
    private const float LowBandEnd = 0.17f;
    private const float GoodBandStart = 0.33f;
    private const float GoodBandEnd = 0.67f;
    private const float HotBandStart = 0.81f;
    private const float RedBandStart = 0.92f;

    private static readonly System.Windows.Media.Color MeterGoodColor = System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E);
    private static readonly System.Windows.Media.Color MeterWarnColor = System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B);
    private static readonly System.Windows.Media.Color MeterClipColor = System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44);

    private readonly Ellipse _dot;
    private readonly System.Windows.Shapes.Path _glyph;
    private readonly Ellipse[] _ripples;
    private readonly ScaleTransform[] _rippleScales;
    private readonly Border _meterShell;
    private readonly System.Windows.Shapes.Rectangle _meterFill;
    private readonly DispatcherTimer _topmostTimer;
    private OverlayIndicatorState _state = OverlayIndicatorState.Hidden;
    private bool _musicActive;
    private bool _meterEnabled = true;
    private float _meterSensitivityDb;
    private bool _ripplesRunning;
    private float _fillPeak;   // fast decay: drives the bar height
    private float _zonePeak;   // slow decay: drives the bar color, so it doesn't flip between beats
    private DateTime _clipHoldUntil = DateTime.MinValue;
    private readonly GradientStop _meterFillTop;
    private readonly GradientStop _meterFillBottom;
    private System.Windows.Media.Color _meterColor = MeterGoodColor;   // displayed color, eased toward the ramp target

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

        // Fixed size with the meter column always reserved: the dot keeps its
        // screen position whether or not the meter is currently shown.
        Width = MeterWidth + MeterGap + DotZoneSize;
        Height = DotZoneSize;
        Opacity = 0.72;

        _dot = new Ellipse
        {
            Width = DotDiameter,
            Height = DotDiameter,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = StatusTheme.LiveBrush,
            Effect = new DropShadowEffect { BlurRadius = 7, ShadowDepth = 1, Opacity = 0.35 }
        };

        _glyph = new System.Windows.Shapes.Path
        {
            Data = StatusTheme.MicGlyph,
            Fill = System.Windows.Media.Brushes.White,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _rippleScales = new ScaleTransform[2];
        _ripples = new Ellipse[2];
        for (int i = 0; i < _ripples.Length; i++)
        {
            _rippleScales[i] = new ScaleTransform(1, 1);
            _ripples[i] = new Ellipse
            {
                Width = DotDiameter,
                Height = DotDiameter,
                Stroke = StatusTheme.LiveBrush,
                StrokeThickness = RippleStrokeThickness,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = _rippleScales[i],
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                Opacity = 0,
                Visibility = Visibility.Collapsed
            };
        }

        // Subtle vertical sheen (lighter top) so the fill doesn't read as a flat block.
        _meterFillTop = new GradientStop(Lighten(MeterGoodColor, 0.35f), 0);
        _meterFillBottom = new GradientStop(MeterGoodColor, 1);
        _meterFill = new System.Windows.Shapes.Rectangle
        {
            RadiusX = 2.5,
            RadiusY = 2.5,
            Height = 0,
            VerticalAlignment = VerticalAlignment.Bottom,
            Fill = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0),
                EndPoint = new System.Windows.Point(0, 1),
                GradientStops = { _meterFillTop, _meterFillBottom }
            }
        };

        _meterShell = new Border
        {
            Width = MeterWidth,
            Height = MeterHeight,
            CornerRadius = new CornerRadius(MeterWidth / 2),
            Background = CreateFrozenBrush(0x00, 0x00, 0x00, 0x59),
            BorderBrush = CreateFrozenBrush(0xFF, 0xFF, 0xFF, 0x66),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(1),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = _meterFill
        };

        var dotZone = new Grid();
        foreach (var ripple in _ripples)
        {
            dotZone.Children.Add(ripple);
        }
        dotZone.Children.Add(_dot);
        dotZone.Children.Add(_glyph);

        var root = new Grid { IsHitTestVisible = false };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MeterWidth + MeterGap) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DotZoneSize) });
        Grid.SetColumn(_meterShell, 0);
        Grid.SetColumn(dotZone, 1);
        root.Children.Add(_meterShell);
        root.Children.Add(dotZone);
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

    /// <summary>Whether the outgoing-level meter may be shown (user setting).</summary>
    public bool MeterEnabled
    {
        get => _meterEnabled;
        set
        {
            if (_meterEnabled == value)
            {
                return;
            }

            _meterEnabled = value;
            UpdateMusicVisuals();
        }
    }

    /// <summary>
    /// Calibration offset in dB added to the measured loudness before it is mapped
    /// onto the meter's dB window. Positive values make the meter read hotter, so
    /// the bar and every color band react earlier. The sample-peak clip flash is
    /// deliberately unaffected: digital clipping does not depend on perception.
    /// </summary>
    public float MeterSensitivityDb
    {
        get => _meterSensitivityDb;
        set => _meterSensitivityDb = Math.Clamp(value, -12f, 12f);
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
            UpdateMusicVisuals();
            Hide();
            return;
        }

        (_dot.Fill, _glyph.Data) = state switch
        {
            OverlayIndicatorState.Modded => (StatusTheme.ModdedBrush, StatusTheme.ModdedMicGlyph),
            OverlayIndicatorState.Muted => (StatusTheme.MutedBrush, StatusTheme.MicOffGlyph),
            _ => (StatusTheme.LiveBrush, StatusTheme.MicGlyph)
        };

        var rippleBrush = state == OverlayIndicatorState.Modded ? StatusTheme.ModdedBrush : StatusTheme.LiveBrush;
        foreach (var ripple in _ripples)
        {
            ripple.Stroke = rippleBrush;
        }

        if (!IsVisible)
        {
            PositionTopRight();
            Show();
        }

        UpdateMusicVisuals();
        EnsureTopmost();
        _topmostTimer.Start();
    }

    /// <summary>
    /// Signals whether music is currently being routed into the mic channel.
    /// Starts/stops the wave rings. No-op when unchanged.
    /// </summary>
    public void SetMusicActive(bool active)
    {
        if (_musicActive == active)
        {
            return;
        }

        _musicActive = active;
        UpdateMusicVisuals();
    }

    /// <summary>
    /// Feeds the meter with the latest outgoing mix levels: sample peak (0..1,
    /// clipping detection) and block RMS (perceived loudness, drives the bar and
    /// color). Cheap no-op while the meter is hidden. Peak-hold decay happens
    /// here, so call this on a steady tick (~50 ms) while the overlay exists.
    /// </summary>
    public void SetOutputLevel(float peak, float rms)
    {
        if (_meterShell.Visibility != Visibility.Visible)
        {
            return;
        }

        if (peak >= ClipThreshold)
        {
            _clipHoldUntil = DateTime.UtcNow + ClipHold;
        }

        float loudness = NormalizeLoudness(rms);
        _fillPeak = Math.Max(loudness, _fillPeak * 0.78f);
        _zonePeak = Math.Max(loudness, _zonePeak * 0.96f);

        double height = _fillPeak * MeterInnerHeight;
        if (Math.Abs(height - _meterFill.Height) >= 0.5)
        {
            _meterFill.Height = height;
        }

        // Clipping snaps to red instantly (that signal should be crisp); everything
        // else eases toward the ramp color so zone changes glide instead of snapping.
        var target = DateTime.UtcNow < _clipHoldUntil ? MeterClipColor : ColorForLevel(_zonePeak);
        _meterColor = target == MeterClipColor ? target : LerpColor(_meterColor, target, 0.22f);

        if (_meterFillBottom.Color != _meterColor)
        {
            _meterFillBottom.Color = _meterColor;
            _meterFillTop.Color = Lighten(_meterColor, 0.35f);
        }
    }

    /// <summary>Maps a linear RMS value onto the meter's dB window as 0..1, after the sensitivity offset.</summary>
    private float NormalizeLoudness(float rms)
    {
        if (rms <= 0f)
        {
            return 0f;
        }

        float db = 20f * (float)Math.Log10(rms) + _meterSensitivityDb;
        return Math.Clamp((db - MeterFloorDb) / (MeterCeilingDb - MeterFloorDb), 0f, 1f);
    }

    /// <summary>Color ramp over the normalized loudness: amber → green → amber → red with soft crossfades.</summary>
    private static System.Windows.Media.Color ColorForLevel(float level)
    {
        return level < LowBandEnd ? MeterWarnColor
            : level < GoodBandStart ? LerpColor(MeterWarnColor, MeterGoodColor, (level - LowBandEnd) / (GoodBandStart - LowBandEnd))
            : level < GoodBandEnd ? MeterGoodColor
            : level < HotBandStart ? LerpColor(MeterGoodColor, MeterWarnColor, (level - GoodBandEnd) / (HotBandStart - GoodBandEnd))
            : LerpColor(MeterWarnColor, MeterClipColor, Math.Min(1f, (level - HotBandStart) / (RedBandStart - HotBandStart)));
    }

    private static System.Windows.Media.Color LerpColor(System.Windows.Media.Color from, System.Windows.Media.Color to, float t)
    {
        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    private static System.Windows.Media.Color Lighten(System.Windows.Media.Color color, float amount)
    {
        return LerpColor(color, System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF), amount);
    }

    private void UpdateMusicVisuals()
    {
        bool shown = _state != OverlayIndicatorState.Hidden;

        if (_musicActive && shown)
        {
            StartRipples();
        }
        else
        {
            StopRipples();
        }

        // The meter shows the complete outgoing mix, so it follows the dot's
        // visibility rather than the music state.
        bool meterVisible = shown && _meterEnabled;
        if (meterVisible && _meterShell.Visibility != Visibility.Visible)
        {
            _fillPeak = 0f;
            _zonePeak = 0f;
            _clipHoldUntil = DateTime.MinValue;
            _meterFill.Height = 0;
            _meterColor = MeterGoodColor;
            _meterFillBottom.Color = _meterColor;
            _meterFillTop.Color = Lighten(_meterColor, 0.35f);
            _meterShell.Visibility = Visibility.Visible;
        }
        else if (!meterVisible && _meterShell.Visibility == Visibility.Visible)
        {
            _meterShell.Visibility = Visibility.Collapsed;
        }
    }

    private void StartRipples()
    {
        if (_ripplesRunning)
        {
            return;
        }

        _ripplesRunning = true;

        for (int i = 0; i < _ripples.Length; i++)
        {
            // Stagger the rings half a period apart for a continuous pulse.
            var beginTime = TimeSpan.FromMilliseconds(RipplePeriod.TotalMilliseconds * i / _ripples.Length);

            var scaleAnimation = new DoubleAnimation(1.0, RippleMaxScale, RipplePeriod)
            {
                BeginTime = beginTime,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Timeline.SetDesiredFrameRate(scaleAnimation, RippleFrameRate);

            var fadeAnimation = new DoubleAnimation(RippleStartOpacity, 0.0, RipplePeriod)
            {
                BeginTime = beginTime,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Timeline.SetDesiredFrameRate(fadeAnimation, RippleFrameRate);

            _ripples[i].Visibility = Visibility.Visible;
            _rippleScales[i].BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
            _rippleScales[i].BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
            _ripples[i].BeginAnimation(OpacityProperty, fadeAnimation);
        }
    }

    private void StopRipples()
    {
        if (!_ripplesRunning)
        {
            return;
        }

        _ripplesRunning = false;

        for (int i = 0; i < _ripples.Length; i++)
        {
            // Detach the animation clocks entirely so an idle overlay renders nothing.
            _rippleScales[i].BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _rippleScales[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _ripples[i].BeginAnimation(OpacityProperty, null);
            _rippleScales[i].ScaleX = 1;
            _rippleScales[i].ScaleY = 1;
            _ripples[i].Opacity = 0;
            _ripples[i].Visibility = Visibility.Collapsed;
        }
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
        StopRipples();
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

    private static System.Windows.Media.Brush CreateFrozenBrush(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(a, r, g, b));
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
