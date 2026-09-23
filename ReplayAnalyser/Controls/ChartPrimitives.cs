// Ported from the Replay Analyser (EchoAnalyser.App/Controls/ChartPrimitives.cs). The drawing
// helpers are unchanged; the fixed Palette is replaced by ChartPalette, which reads the active
// Spark theme, and series/markers carry a Tone instead of a brush.
#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Spark.ReplayAnalyser.Controls;

/// <summary>Shared drawing helpers for the hand-rendered charts.</summary>
internal static class Draw
{
    private static readonly FontFamily UiFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

    public static readonly Typeface Ui =
        new Typeface(UiFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public static readonly Typeface UiBold =
        new Typeface(UiFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    public static readonly Typeface Mono =
        new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static FormattedText Text(string s, double size, Brush brush, double pixelsPerDip,
                                     Typeface? face = null) =>
        new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            face ?? Ui, size, brush, pixelsPerDip);

    /// <summary>Pen that will not blur: snapped to device pixels.</summary>
    public static Pen Pen(Brush b, double thickness)
    {
        var p = new Pen(b, thickness);
        p.Freeze();
        return p;
    }

    public static Brush Solid(byte r, byte g, byte b, byte a = 255) => Solid(Color.FromArgb(a, r, g, b));

    public static Brush Solid(Color c)
    {
        var br = new SolidColorBrush(c);
        br.Freeze();
        return br;
    }

    public static Brush Fade(Brush source, double opacity)
    {
        if (source is SolidColorBrush s)
        {
            var c = s.Color;
            var br = new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), c.R, c.G, c.B));
            br.Freeze();
            return br;
        }
        return source;
    }

    /// <summary>
    /// Choose an axis step that lands on 1/2/5 x 10^n, so labels read as round numbers
    /// rather than arbitrary fractions of the data range.
    /// </summary>
    public static double NiceStep(double range, int targetTicks)
    {
        if (range <= 0 || targetTicks <= 0) return 1;
        double raw = range / targetTicks;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double step = norm switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10,
        };
        return step * mag;
    }

    public static string Clock(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return "-";
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(a.A,
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>
    /// The same hue at full brightness. Theme colours are shaded to read as text on the window —
    /// deep on a bright theme — but light added up on the arena floor needs its brightest form,
    /// or the busy lanes never reach white.
    /// </summary>
    public static Color Glow(Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max <= 0) return Colors.White;
        double k = 255.0 / max;
        return Color.FromRgb((byte)Math.Min(255, c.R * k), (byte)Math.Min(255, c.G * k), (byte)Math.Min(255, c.B * k));
    }

    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}

/// <summary>
/// The active Spark theme's colours, resolved once per theme for the charts to draw with. Frozen
/// copies rather than the theme's own brushes, which are live objects and slow to render with.
/// </summary>
internal sealed class ChartPalette
{
    public Brush Ground = null!, Card = null!, Raised = null!, BorderSoft = null!, Border = null!, Track = null!;
    public Brush Text = null!, Dim = null!, Faint = null!;
    public Color GroundColor, CardColor;
    /// <summary>True when the window is dark; bright themes shade everything the other way.</summary>
    public bool IsDark;

    private readonly Dictionary<Tone, (Color color, Brush brush)> _tones = new Dictionary<Tone, (Color, Brush)>();

    public Brush Of(Tone tone) => _tones[tone].brush;
    public Color ColorOf(Tone tone) => _tones[tone].color;

    public static ChartPalette From(FrameworkElement fe)
    {
        Color Get(string key, Color fallback) => fe.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

        var p = new ChartPalette
        {
            GroundColor = Get("SurfaceGround", Color.FromRgb(0x15, 0x15, 0x15)),
            CardColor = Get("SurfaceCard", Color.FromRgb(0x26, 0x26, 0x26)),
        };
        p.Ground = Draw.Solid(p.GroundColor);
        p.Card = Draw.Solid(p.CardColor);
        p.Raised = Draw.Solid(Get("SurfaceRaised", Color.FromRgb(0x30, 0x30, 0x30)));
        p.BorderSoft = Draw.Solid(Get("SurfaceBorderSoft", Color.FromRgb(0x2C, 0x2C, 0x2C)));
        p.Border = Draw.Solid(Get("SurfaceBorder", Color.FromRgb(0x3B, 0x3B, 0x3B)));
        p.Track = Draw.Solid(Get("SurfaceTrack", Color.FromRgb(0x43, 0x43, 0x43)));

        var text = Get("TextPrimary", Color.FromRgb(0xEC, 0xEC, 0xEC));
        p.Text = Draw.Solid(text);
        p.Dim = Draw.Solid(Get("TextDim", Color.FromRgb(0xA5, 0xA5, 0xA5)));
        p.Faint = Draw.Solid(Get("TextFaint", Color.FromRgb(0x78, 0x78, 0x78)));
        p.IsDark = Draw.Luminance(text) > 0.5;

        foreach (Tone tone in Enum.GetValues(typeof(Tone)))
        {
            var c = Get(ToneKeys.Solid(tone), Colors.Gray);
            p._tones[tone] = (c, Draw.Solid(c));
        }
        return p;
    }
}

/// <summary>
/// Base for the hand-drawn charts. They paint in OnRender rather than with bound brushes, so
/// they need telling when Spark changes theme: each probe below holds a live reference to a
/// theme brush, and ThemesController swaps every brush on a change, which lands here.
/// </summary>
public abstract class ThemedElement : FrameworkElement
{
    private static DependencyProperty Probe(string name) => DependencyProperty.Register(
        name, typeof(object), typeof(ThemedElement),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((ThemedElement)d).ThemeChanged()));

    private static readonly DependencyProperty SurfaceProbeProperty = Probe("SurfaceProbe");
    private static readonly DependencyProperty TextProbeProperty = Probe("TextProbe");
    private static readonly DependencyProperty AccentProbeProperty = Probe("AccentProbe");
    private static readonly DependencyProperty TeamProbeProperty = Probe("TeamProbe");

    private ChartPalette? _palette;

    protected ThemedElement()
    {
        SetResourceReference(SurfaceProbeProperty, "SurfaceCard");
        SetResourceReference(TextProbeProperty, "TextPrimary");
        SetResourceReference(AccentProbeProperty, "ControlAccent");
        SetResourceReference(TeamProbeProperty, "TeamBlue");
    }

    internal ChartPalette Palette => _palette ??= ChartPalette.From(this);

    private void ThemeChanged()
    {
        _palette = null;
        OnThemeChanged();
    }

    /// <summary>For anything cached from the old theme's colours.</summary>
    protected virtual void OnThemeChanged() { }
}

/// <summary>One line on a <see cref="LineChart"/>.</summary>
public sealed class ChartSeries
{
    public string Name { get; init; } = "";
    public Tone Tone { get; init; } = Tone.Accent;
    /// <summary>When above zero, the area under the line is filled with the tone at this opacity.</summary>
    public double FillOpacity { get; init; }
    public double Thickness { get; init; } = 2;
    public IReadOnlyList<(double X, double Y)> Points { get; init; } = Array.Empty<(double, double)>();
    /// <summary>Draw as a step line — right for scores, which change instantaneously.</summary>
    public bool Stepped { get; init; }
}

/// <summary>A vertical event marker drawn across a <see cref="LineChart"/>.</summary>
public sealed class ChartMarker
{
    public double X { get; init; }
    public Tone Tone { get; init; } = Tone.Accent;
    public string Label { get; init; } = "";
    public string Tooltip { get; init; } = "";
}
