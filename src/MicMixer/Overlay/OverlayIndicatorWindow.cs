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

/// <summary>Where the music currently goes, shown by the overlay's music circle.</summary>
public enum OverlayMusicState
{
    /// <summary>No active music — the music circle is hidden.</summary>
    Hidden,

    /// <summary>Music is being sent into the mic channel (others hear it).</summary>
    Sending,

    /// <summary>Monitor-only preview: heard locally (and on the secondary output), never on the cable.</summary>
    MonitorOnly,

    /// <summary>Music plays but push-to-talk currently blocks it from the cable.</summary>
    Blocked
}

/// <summary>
/// Small always-on-top status cluster in the top-right corner of the primary
/// screen. The right circle mirrors the tray icon state (normal / modded /
/// push-to-talk muted); the left circle appears while music is active and tells
/// where the music goes: purple with a note = sent into the mic channel, amber
/// with headphones = monitor-only preview (only you and the secondary output
/// hear it), gray with a crossed-out note = playing but blocked by push-to-talk.
///
/// While music is audible somewhere, three tiny equalizer bars dance below the
/// music circle — the universal "now playing" sign; in the blocked state they
/// freeze at minimum height (paused look) without any animation clocks. Each
/// circle carries an optional level ring — a 270° arc gauge with its opening at
/// the bottom: the mic ring shows the complete outgoing mix (post gates, exactly
/// what the cable receives) and the music ring shows the music branch alone
/// (after the music volume, before its cable gate — so the level can be set
/// right during a monitor-only preview). The bar animation clocks are fully torn
/// down while no music plays and are capped at a low frame rate, because every
/// animation frame of a layered window re-uploads its bitmap to the compositor.
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
    private const double DotZoneSize = 58;   // dot + level ring + the equalizer bars in its opening
    private const double EdgeMargin = 10;

    // Level ring: an arc gauge wrapped around the dot with its opening at the
    // bottom. All gauge angles are degrees clockwise from 12 o'clock.
    private const double RingThickness = 3.5;
    private const double RingBackingExtra = 2;  // dark backing arc extends this much past the fill
    private const double RingGap = 3;           // between the dot edge and the ring
    private const double RingRadius = DotDiameter / 2 + RingGap + RingThickness / 2;
    private const double GaugeGapDegrees = 90;
    private const double GaugeStartAngle = 180 + GaugeGapDegrees / 2;
    private const double GaugeSweepDegrees = 360 - GaugeGapDegrees;
    private const double NotchWidth = 2.2;
    private const double NotchLength = RingThickness + 2.5;
    private const float NotchDecayPerTick = 0.015f; // peak notch falls full scale in ~3 s at the 50 ms feed tick
    private const float NotchVisibleFloor = 0.03f;
    private const string StreamDeckyProcessName = "StreamDecky";
    private static readonly TimeSpan TopmostInterval = TimeSpan.FromSeconds(3);

    // Music indicator: three tiny equalizer bars dancing in the music ring's
    // bottom opening (the universal "now playing" sign). Neutral white — color
    // is reserved for status and level. Each bar bounces between the min and
    // max height on its own period so the trio looks organic, not mechanical.
    private const double MusicBarWidth = 3;
    private const double MusicBarSpacing = 2;
    private const double MusicBarMinHeight = 2.5;
    private const double MusicBarMaxHeight = 8;
    private const double MusicBarBottomMargin = 2;   // shifted low enough that a full-height bar keeps a ~2 px gap to the dot
    private const int MusicBarFrameRate = 24;
    private static readonly TimeSpan[] MusicBarPeriods =
    {
        TimeSpan.FromMilliseconds(360),
        TimeSpan.FromMilliseconds(280),
        TimeSpan.FromMilliseconds(440)
    };

    // The meters judge perceived loudness (block RMS on a dB scale), not sample
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

    // Music circle state language. Purple is the app's music accent; amber for
    // monitor-only signals "careful — the game does not hear this" without
    // reading as an error; blocked reuses the neutral stopped gray. Every state
    // also has its own glyph so they stay distinguishable without color.
    private static readonly System.Windows.Media.Brush MusicSendingBrush = CreateFrozenBrush(0x7C, 0x3A, 0xED);
    private static readonly System.Windows.Media.Brush MusicMonitorOnlyBrush = CreateFrozenBrush(0xD9, 0x77, 0x06);

    private static readonly Geometry MusicNoteGlyph = CreateFrozenGeometry(
        "M12,3V13.55C11.41,13.21 10.73,13 10,13A4,4 0 0,0 6,17A4,4 0 0,0 10,21A4,4 0 0,0 14,17V7H18V3H12Z");
    private static readonly Geometry MusicNoteOffGlyph = CreateFrozenGeometry(
        "M4.27,3L3,4.27L12,13.27V13.55C11.41,13.21 10.73,13 10,13A4,4 0 0,0 6,17A4,4 0 0,0 10,21A4,4 0 0,0 14,17V15.27L19.73,21L21,19.73L4.27,3M14,7H18V3H12V8.18L14,10.18V7Z");
    private static readonly Geometry HeadphonesGlyph = CreateFrozenGeometry(
        "M12,1C7,1 3,5 3,10V17A3,3 0 0,0 6,20H9V12H5V10A7,7 0 0,1 12,3A7,7 0 0,1 19,10V12H15V20H18A3,3 0 0,0 21,17V10C21,5 17,1 12,1Z");

    private readonly Ellipse _dot;
    private readonly System.Windows.Shapes.Path _glyph;
    private readonly LevelRingGauge _micGauge;
    private readonly Grid _musicZone;
    private readonly Ellipse _musicDot;
    private readonly System.Windows.Shapes.Path _musicGlyph;
    private readonly LevelRingGauge _musicGauge;
    private readonly System.Windows.Shapes.Rectangle[] _musicBars;
    private readonly ScaleTransform[] _musicBarScales;
    private readonly StackPanel _musicBarsPanel;
    private readonly DispatcherTimer _topmostTimer;
    private OverlayIndicatorState _state = OverlayIndicatorState.Hidden;
    private OverlayMusicState _musicState = OverlayMusicState.Hidden;
    private bool _meterEnabled = true;
    private float _meterSensitivityDb;
    private bool _musicBarsRunning;

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

        // Two square zones side by side: the music circle on the left, the mic
        // circle on the right. The window is right-anchored, so the mic circle
        // keeps its screen position whether or not music is shown, and each
        // level ring wraps its own dot without shifting anything.
        Width = DotZoneSize * 2;
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

        _musicDot = new Ellipse
        {
            Width = DotDiameter,
            Height = DotDiameter,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = MusicSendingBrush,
            Effect = new DropShadowEffect { BlurRadius = 7, ShadowDepth = 1, Opacity = 0.35 }
        };

        _musicGlyph = new System.Windows.Shapes.Path
        {
            Data = MusicNoteGlyph,
            Fill = System.Windows.Media.Brushes.White,
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _musicBars = new System.Windows.Shapes.Rectangle[MusicBarPeriods.Length];
        _musicBarScales = new ScaleTransform[MusicBarPeriods.Length];
        for (int i = 0; i < _musicBars.Length; i++)
        {
            _musicBarScales[i] = new ScaleTransform(1, MusicBarMinHeight / MusicBarMaxHeight);
            _musicBars[i] = new System.Windows.Shapes.Rectangle
            {
                Width = MusicBarWidth,
                Height = MusicBarMaxHeight,
                RadiusX = MusicBarWidth / 2,
                RadiusY = MusicBarWidth / 2,
                Fill = CreateFrozenBrush(0xFF, 0xFF, 0xFF, 0xE6),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(MusicBarSpacing / 2, 0, MusicBarSpacing / 2, 0),
                RenderTransform = _musicBarScales[i],
                RenderTransformOrigin = new System.Windows.Point(0.5, 1)
            };
        }

        _musicBarsPanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Height = MusicBarMaxHeight,
            Margin = new Thickness(0, 0, 0, MusicBarBottomMargin),
            Visibility = Visibility.Collapsed
        };
        foreach (var bar in _musicBars)
        {
            _musicBarsPanel.Children.Add(bar);
        }

        _micGauge = new LevelRingGauge();
        _musicGauge = new LevelRingGauge();

        _musicZone = new Grid { Visibility = Visibility.Collapsed };
        _musicZone.Children.Add(_musicGauge.Visual);
        _musicZone.Children.Add(_musicBarsPanel);
        _musicZone.Children.Add(_musicDot);
        _musicZone.Children.Add(_musicGlyph);

        var micZone = new Grid();
        micZone.Children.Add(_micGauge.Visual);
        micZone.Children.Add(_dot);
        micZone.Children.Add(_glyph);

        var root = new Grid { IsHitTestVisible = false };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DotZoneSize) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DotZoneSize) });
        Grid.SetColumn(_musicZone, 0);
        Grid.SetColumn(micZone, 1);
        root.Children.Add(_musicZone);
        root.Children.Add(micZone);
        Content = root;

        _topmostTimer = new DispatcherTimer { Interval = TopmostInterval };
        _topmostTimer.Tick += (_, _) =>
        {
            // Reposition too: resolution or work-area changes would otherwise
            // strand the dots mid-screen or off-screen.
            PositionTopRight();
            EnsureTopmost();
        };
    }

    /// <summary>Whether the level rings may be shown (user setting). Applies to both circles.</summary>
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
            UpdateVisuals();
        }
    }

    /// <summary>
    /// Calibration offset in dB added to the measured loudness before it is mapped
    /// onto the meters' dB window. Positive values make the meters read hotter, so
    /// the bar and every color band react earlier. The sample-peak clip flash is
    /// deliberately unaffected: digital clipping does not depend on perception.
    /// </summary>
    public float MeterSensitivityDb
    {
        get => _meterSensitivityDb;
        set => _meterSensitivityDb = Math.Clamp(value, -12f, 12f);
    }

    /// <summary>Shows, hides, and recolors the mic dot. No-op when the state is unchanged.</summary>
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
            UpdateVisuals();
            Hide();
            return;
        }

        (_dot.Fill, _glyph.Data) = state switch
        {
            OverlayIndicatorState.Modded => (StatusTheme.ModdedBrush, StatusTheme.ModdedMicGlyph),
            OverlayIndicatorState.Muted => (StatusTheme.MutedBrush, StatusTheme.MicOffGlyph),
            _ => (StatusTheme.LiveBrush, StatusTheme.MicGlyph)
        };

        if (!IsVisible)
        {
            PositionTopRight();
            Show();
        }

        UpdateVisuals();
        EnsureTopmost();
        _topmostTimer.Start();
    }

    /// <summary>
    /// Sets the music circle: hidden while no music is active, otherwise where
    /// the music currently goes. No-op when unchanged.
    /// </summary>
    public void SetMusicState(OverlayMusicState state)
    {
        if (_musicState == state)
        {
            return;
        }

        _musicState = state;
        UpdateVisuals();
    }

    /// <summary>
    /// Feeds the mic ring with the latest outgoing mix levels: sample peak (0..1,
    /// clipping detection) and block RMS (perceived loudness, drives the arc and
    /// color). Cheap no-op while the ring is hidden. Peak-hold decay happens
    /// here, so call this on a steady tick (~50 ms) while the overlay exists.
    /// </summary>
    public void SetOutputLevel(float peak, float rms)
    {
        _micGauge.Feed(peak, rms, _meterSensitivityDb);
    }

    /// <summary>
    /// Feeds the music ring with the latest music-branch levels (after the music
    /// volume, before its cable gate). Same contract as <see cref="SetOutputLevel"/>.
    /// </summary>
    public void SetMusicLevel(float peak, float rms)
    {
        _musicGauge.Feed(peak, rms, _meterSensitivityDb);
    }

    private void UpdateVisuals()
    {
        bool shown = _state != OverlayIndicatorState.Hidden;
        bool musicShown = shown && _musicState != OverlayMusicState.Hidden;

        // The mic ring shows the complete outgoing mix, so it follows the mic
        // dot's visibility rather than the music state.
        _micGauge.SetVisible(shown && _meterEnabled);

        if (musicShown)
        {
            (_musicDot.Fill, _musicGlyph.Data) = _musicState switch
            {
                OverlayMusicState.MonitorOnly => (MusicMonitorOnlyBrush, HeadphonesGlyph),
                OverlayMusicState.Blocked => (StatusTheme.StoppedBrush, MusicNoteOffGlyph),
                _ => (MusicSendingBrush, MusicNoteGlyph)
            };
            _musicZone.Visibility = Visibility.Visible;
        }
        else
        {
            _musicZone.Visibility = Visibility.Collapsed;
        }

        _musicGauge.SetVisible(musicShown && _meterEnabled);

        // Bars dance while the music is audible somewhere; in the blocked state
        // they stay visible frozen at minimum height (a paused look) so the
        // difference between "plays but blocked" and "sends" is unmistakable.
        if (musicShown && _musicState != OverlayMusicState.Blocked)
        {
            StartMusicBars();
        }
        else
        {
            StopMusicBars(keepVisible: musicShown);
        }
    }

    private void StartMusicBars()
    {
        if (_musicBarsRunning)
        {
            _musicBarsPanel.Visibility = Visibility.Visible;
            return;
        }

        _musicBarsRunning = true;

        for (int i = 0; i < _musicBars.Length; i++)
        {
            // Each bar bounces on its own period, phase-shifted, so the trio
            // dances instead of pumping in lockstep.
            var bounce = new DoubleAnimation(MusicBarMinHeight / MusicBarMaxHeight, 1, MusicBarPeriods[i])
            {
                BeginTime = TimeSpan.FromMilliseconds(90 * i),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(bounce, MusicBarFrameRate);
            _musicBarScales[i].BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
        }

        _musicBarsPanel.Visibility = Visibility.Visible;
    }

    private void StopMusicBars(bool keepVisible = false)
    {
        _musicBarsPanel.Visibility = keepVisible ? Visibility.Visible : Visibility.Collapsed;

        if (!_musicBarsRunning)
        {
            return;
        }

        _musicBarsRunning = false;

        for (int i = 0; i < _musicBars.Length; i++)
        {
            // Detach the animation clocks entirely so an idle overlay renders nothing.
            _musicBarScales[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _musicBarScales[i].ScaleY = MusicBarMinHeight / MusicBarMaxHeight;
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
        StopMusicBars();
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
    /// One 270° level-ring gauge (backing, hairline track, colored fill arc and a
    /// peak-hold notch) centered in a dot zone, together with all its decay and
    /// color-easing state. Both overlay circles own one instance each.
    /// </summary>
    private sealed class LevelRingGauge
    {
        private readonly System.Windows.Shapes.Path _fill;
        private readonly ArcSegment _fillArc;
        private readonly System.Windows.Shapes.Rectangle _notch;
        private readonly RotateTransform _notchRotate;
        private readonly SolidColorBrush _brush;
        private float _fillPeak;   // fast decay: drives the arc sweep
        private float _zonePeak;   // slow decay: drives the arc color, so it doesn't flip between beats
        private float _notchPeak;  // slowest decay: drives the peak-hold notch
        private DateTime _clipHoldUntil = DateTime.MinValue;
        private double _renderedSweep = -1; // last sweep pushed to the arc, to skip sub-degree redraws
        private System.Windows.Media.Color _color = MeterGoodColor;   // displayed color, eased toward the ramp target

        public LevelRingGauge()
        {
            _brush = new SolidColorBrush(MeterGoodColor);

            // A dark backing arc keeps the gauge readable on bright scenes, a
            // hairline track shows the scale while unfilled, the colored fill
            // arc is the meter itself and a white notch holds the recent peak.
            var backing = CreateGaugeArc(GaugeSweepDegrees, CreateFrozenBrush(0x00, 0x00, 0x00, 0x59), RingThickness + RingBackingExtra);
            var track = CreateGaugeArc(GaugeSweepDegrees, CreateFrozenBrush(0xFF, 0xFF, 0xFF, 0x4D), 1.2);

            _fillArc = new ArcSegment
            {
                Size = new System.Windows.Size(RingRadius, RingRadius),
                SweepDirection = SweepDirection.Clockwise
            };
            var fillFigure = new PathFigure
            {
                StartPoint = GaugePoint(GaugeStartAngle),
                IsClosed = false,
                IsFilled = false,
                Segments = { _fillArc }
            };
            _fill = new System.Windows.Shapes.Path
            {
                Data = new PathGeometry { Figures = { fillFigure } },
                Stroke = _brush,
                StrokeThickness = RingThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Visibility = Visibility.Collapsed
            };

            // The notch stays a centered element; the transform chain lifts it up
            // onto the ring and then rotates it about its original centered origin,
            // so the rotation orbits it around the dot while keeping it radial.
            _notchRotate = new RotateTransform(GaugeStartAngle);
            _notch = new System.Windows.Shapes.Rectangle
            {
                Width = NotchWidth,
                Height = NotchLength,
                RadiusX = NotchWidth / 2,
                RadiusY = NotchWidth / 2,
                Fill = CreateFrozenBrush(0xFF, 0xFF, 0xFF, 0xE6),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                RenderTransform = new TransformGroup
                {
                    Children = { new TranslateTransform(0, -RingRadius), _notchRotate }
                },
                Visibility = Visibility.Collapsed
            };

            Visual = new Grid { Visibility = Visibility.Collapsed };
            Visual.Children.Add(backing);
            Visual.Children.Add(track);
            Visual.Children.Add(_fill);
            Visual.Children.Add(_notch);
        }

        public Grid Visual { get; }

        public void SetVisible(bool visible)
        {
            if (visible && Visual.Visibility != Visibility.Visible)
            {
                Reset();
                Visual.Visibility = Visibility.Visible;
            }
            else if (!visible && Visual.Visibility == Visibility.Visible)
            {
                Visual.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>See <see cref="OverlayIndicatorWindow.SetOutputLevel"/>. No-op while hidden.</summary>
        public void Feed(float peak, float rms, float sensitivityDb)
        {
            if (Visual.Visibility != Visibility.Visible)
            {
                return;
            }

            if (peak >= ClipThreshold)
            {
                _clipHoldUntil = DateTime.UtcNow + ClipHold;
            }

            float loudness = NormalizeLoudness(rms, sensitivityDb);
            _fillPeak = Math.Max(loudness, _fillPeak * 0.78f);
            _zonePeak = Math.Max(loudness, _zonePeak * 0.96f);
            _notchPeak = Math.Max(loudness, _notchPeak - NotchDecayPerTick);

            double sweep = _fillPeak * GaugeSweepDegrees;
            if (Math.Abs(sweep - _renderedSweep) >= 1.0)
            {
                _renderedSweep = sweep;
                if (sweep < 1.0)
                {
                    _fill.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _fillArc.Point = GaugePoint(GaugeStartAngle + sweep);
                    _fillArc.IsLargeArc = sweep > 180.0;
                    _fill.Visibility = Visibility.Visible;
                }
            }

            if (_notchPeak <= NotchVisibleFloor)
            {
                _notch.Visibility = Visibility.Collapsed;
            }
            else
            {
                double notchAngle = GaugeStartAngle + _notchPeak * GaugeSweepDegrees;
                if (Math.Abs(notchAngle - _notchRotate.Angle) >= 1.0)
                {
                    _notchRotate.Angle = notchAngle;
                }
                _notch.Visibility = Visibility.Visible;
            }

            // Clipping snaps to red instantly (that signal should be crisp); everything
            // else eases toward the ramp color so zone changes glide instead of snapping.
            var target = DateTime.UtcNow < _clipHoldUntil ? MeterClipColor : ColorForLevel(_zonePeak);
            _color = target == MeterClipColor ? target : LerpColor(_color, target, 0.22f);

            if (_brush.Color != _color)
            {
                _brush.Color = _color;
            }
        }

        private void Reset()
        {
            _fillPeak = 0f;
            _zonePeak = 0f;
            _notchPeak = 0f;
            _clipHoldUntil = DateTime.MinValue;
            _renderedSweep = -1;
            _fill.Visibility = Visibility.Collapsed;
            _notch.Visibility = Visibility.Collapsed;
            _color = MeterGoodColor;
            _brush.Color = _color;
        }
    }

    /// <summary>Point on the ring centerline for an angle in degrees clockwise from 12 o'clock.</summary>
    private static System.Windows.Point GaugePoint(double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180.0;
        return new System.Windows.Point(
            DotZoneSize / 2 + RingRadius * Math.Sin(radians),
            DotZoneSize / 2 - RingRadius * Math.Cos(radians));
    }

    /// <summary>Static gauge arc from the gauge start over the given sweep, with round caps.</summary>
    private static System.Windows.Shapes.Path CreateGaugeArc(double sweepDegrees, System.Windows.Media.Brush stroke, double thickness)
    {
        var figure = new PathFigure
        {
            StartPoint = GaugePoint(GaugeStartAngle),
            IsClosed = false,
            IsFilled = false,
            Segments =
            {
                new ArcSegment(
                    GaugePoint(GaugeStartAngle + sweepDegrees),
                    new System.Windows.Size(RingRadius, RingRadius),
                    0,
                    isLargeArc: sweepDegrees > 180.0,
                    SweepDirection.Clockwise,
                    isStroked: true)
            }
        };
        var geometry = new PathGeometry { Figures = { figure } };
        geometry.Freeze();
        return new System.Windows.Shapes.Path
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
    }

    /// <summary>Maps a linear RMS value onto the meter's dB window as 0..1, after the sensitivity offset.</summary>
    private static float NormalizeLoudness(float rms, float sensitivityDb)
    {
        if (rms <= 0f)
        {
            return 0f;
        }

        float db = 20f * (float)Math.Log10(rms) + sensitivityDb;
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

    private static Geometry CreateFrozenGeometry(string pathData)
    {
        var geometry = Geometry.Parse(pathData);
        geometry.Freeze();
        return geometry;
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
