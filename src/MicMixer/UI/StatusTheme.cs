using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace MicMixer.UI;

/// <summary>Routing status shown by the tray icon, the overlay dot and the status texts.</summary>
public enum MicStatus
{
    /// <summary>Routing is stopped; nothing reaches the virtual cable.</summary>
    Stopped,

    /// <summary>Routing runs with the normal microphone — others hear you.</summary>
    Live,

    /// <summary>Routing runs with the modded microphone — others hear the modded voice.</summary>
    Modded,

    /// <summary>Routing runs but the push-to-talk gate keeps the outgoing mix silent.</summary>
    Muted
}

/// <summary>
/// Single source of truth for the status color language, the mic glyphs and the
/// round badge rendering, so the tray icon, the overlay dot, the window icon and
/// the in-window status elements always agree.
///
/// The color semantics follow voice communication apps rather than recording
/// apps: green = you are heard, blue = the modded voice is heard, red with a
/// crossed-out mic = muted, gray = stopped. Every state also has its own glyph,
/// so states stay distinguishable without color (16 px tray, color blindness).
/// Teal is reserved for the app brand (exe/taskbar/window icon) and never used
/// as a state color.
/// </summary>
public static class StatusTheme
{
    // Indicator colors: saturated fills for badges (tray, overlay dot, level meters).
    public static readonly Color StoppedColor = Color.FromRgb(0x6B, 0x72, 0x80);
    public static readonly Color LiveColor = Color.FromRgb(0x16, 0xA3, 0x4A);
    public static readonly Color ModdedColor = Color.FromRgb(0x25, 0x63, 0xEB);
    public static readonly Color MutedColor = Color.FromRgb(0xDC, 0x26, 0x26);

    public static readonly Brush StoppedBrush = CreateFrozenBrush(StoppedColor);
    public static readonly Brush LiveBrush = CreateFrozenBrush(LiveColor);
    public static readonly Brush ModdedBrush = CreateFrozenBrush(ModdedColor);
    public static readonly Brush MutedBrush = CreateFrozenBrush(MutedColor);

    // Ink colors: darker steps of the same hues for text on the light window background.
    public static readonly Brush StoppedInkBrush = CreateFrozenBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    public static readonly Brush LiveInkBrush = CreateFrozenBrush(Color.FromRgb(0x15, 0x80, 0x3D));
    public static readonly Brush ModdedInkBrush = CreateFrozenBrush(Color.FromRgb(0x1D, 0x4E, 0xD8));
    public static readonly Brush MutedInkBrush = CreateFrozenBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));

    // Same MDI glyphs as the MicIcon / ModdedMicIcon / MicOffIcon window resources.
    public static readonly Geometry MicGlyph = CreateGlyph(
        "M12,2A3,3 0 0,1 15,5V11A3,3 0 0,1 12,14A3,3 0 0,1 9,11V5A3,3 0 0,1 12,2M19,11C19,14.53 16.39,17.44 13,17.93V21H11V17.93C7.61,17.44 5,14.53 5,11H7A5,5 0 0,0 12,16A5,5 0 0,0 17,11H19Z");
    public static readonly Geometry ModdedMicGlyph = CreateGlyph(
        "M12,2A3,3 0 0,1 15,5V11A3,3 0 0,1 12,14A3,3 0 0,1 9,11V5A3,3 0 0,1 12,2M19,11C19,14.53 16.39,17.44 13,17.93V21H11V17.93C7.61,17.44 5,14.53 5,11H7A5,5 0 0,0 12,16A5,5 0 0,0 17,11H19M3,9H1V15H3V9M21,9H23V15H21V9");
    public static readonly Geometry MicOffGlyph = CreateGlyph(
        "M19,11C19,12.19 18.66,13.3 18.1,14.28L16.87,13.05C17.14,12.43 17.3,11.74 17.3,11H19M15,11.16L9,5.18V5A3,3 0 0,1 12,2A3,3 0 0,1 15,5V11L15,11.16M4.27,3L21,19.73L19.73,21L15.54,16.81C14.77,17.27 13.91,17.58 13,17.72V21H11V17.72C7.72,17.23 5,14.41 5,11H6.7C6.7,14 9.24,16.1 12,16.1C12.81,16.1 13.6,15.91 14.31,15.58L12.65,13.92L12,14A3,3 0 0,1 9,11V10.28L3,4.27L4.27,3Z");

    // Brand fill for the exe/window icon: a teal gradient, deliberately outside the
    // state palette so the brand never reads as a status.
    private static readonly Brush BrandFill = CreateBrandFill();

    /// <summary>Fraction of the badge diameter that the glyph occupies.</summary>
    private const double GlyphFraction = 0.56;

    public static Brush BrushFor(MicStatus status) => status switch
    {
        MicStatus.Live => LiveBrush,
        MicStatus.Modded => ModdedBrush,
        MicStatus.Muted => MutedBrush,
        _ => StoppedBrush
    };

    public static Brush InkFor(MicStatus status) => status switch
    {
        MicStatus.Live => LiveInkBrush,
        MicStatus.Modded => ModdedInkBrush,
        MicStatus.Muted => MutedInkBrush,
        _ => StoppedInkBrush
    };

    public static Geometry GlyphFor(MicStatus status) => status switch
    {
        MicStatus.Modded => ModdedMicGlyph,
        MicStatus.Muted => MicOffGlyph,
        _ => MicGlyph
    };

    /// <summary>Round status badge (colored circle + white state glyph) as a bitmap.</summary>
    public static BitmapSource RenderStatusBadge(MicStatus status, int pixelSize)
    {
        return RenderBadge(BrushFor(status), GlyphFor(status), pixelSize);
    }

    /// <summary>The brand badge (teal gradient circle + white mic) used for the window/exe icon.</summary>
    public static BitmapSource RenderBrandBadge(int pixelSize)
    {
        return RenderBadge(BrandFill, MicGlyph, pixelSize);
    }

    /// <summary>Status badge as a GDI icon for the WinForms tray. Caller owns the icon.</summary>
    public static System.Drawing.Icon RenderStatusIcon(MicStatus status, int pixelSize)
    {
        BitmapSource source = RenderStatusBadge(status, pixelSize);
        using var bitmap = ToGdiBitmap(source);

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using var temporaryIcon = System.Drawing.Icon.FromHandle(hIcon);
            return (System.Drawing.Icon)temporaryIcon.Clone();
        }
        finally
        {
            _ = DestroyIcon(hIcon);
        }
    }

    private static BitmapSource RenderBadge(Brush fill, Geometry glyph, int pixelSize)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            double center = pixelSize / 2.0;
            double radius = center - Math.Max(1.0, pixelSize / 32.0);
            dc.DrawEllipse(fill, null, new Point(center, center), radius, radius);

            // Uniformly fit the glyph into a centered box, like Stretch.Uniform.
            Rect bounds = glyph.Bounds;
            double box = pixelSize * GlyphFraction;
            double scale = Math.Min(box / bounds.Width, box / bounds.Height);
            dc.PushTransform(new TranslateTransform(
                center - (bounds.X + bounds.Width / 2) * scale,
                center - (bounds.Y + bounds.Height / 2) * scale));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawGeometry(Brushes.White, null, glyph);
            dc.Pop();
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static System.Drawing.Bitmap ToGdiBitmap(BitmapSource source)
    {
        // Pbgra32 is premultiplied, so the GDI bitmap must be PArgb as well.
        var bitmap = new System.Drawing.Bitmap(source.PixelWidth, source.PixelHeight,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            source.CopyPixels(Int32Rect.Empty, data.Scan0, data.Stride * data.Height, data.Stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static Brush CreateBrandFill()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.15, 0),
            EndPoint = new Point(0.85, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x14, 0xB8, 0xA6), 0),
                new GradientStop(Color.FromRgb(0x0F, 0x76, 0x6E), 1)
            }
        };
        brush.Freeze();
        return brush;
    }

    private static Geometry CreateGlyph(string pathData)
    {
        var geometry = Geometry.Parse(pathData);
        geometry.Freeze();
        return geometry;
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
