// Ported from the Replay Analyser (EchoAnalyser.App/Controls/ArenaMap.cs). The additive
// rasteriser is unchanged. Team colours now come from the Spark theme — brightened, since light
// adding up needs each hue at full strength — and the floor stays dark on every theme, because
// glowing routes only read against one.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Spark.ReplayAnalyser.Analysis;

namespace Spark.ReplayAnalyser.Controls;

public enum ArenaView
{
    /// <summary>Looking down the Y axis: arena length across the screen, width up the screen.</summary>
    TopDown,
    /// <summary>Looking from the side: arena length across the screen, height up the screen.</summary>
    Side,
}

/// <summary>One set of movement paths drawn in a single colour.</summary>
public sealed class TrackLayer
{
    public IReadOnlyList<IReadOnlyList<Vector3>> Paths { get; init; } = Array.Empty<IReadOnlyList<Vector3>>();
    /// <summary>The theme colour to draw in.</summary>
    public Tone Tone { get; init; } = Tone.Accent;
    /// <summary>A fixed colour instead of <see cref="Tone"/> — the disc is white whatever the theme.</summary>
    public Color? Color { get; init; }
    /// <summary>Relative brightness, for putting the disc over the players or vice versa.</summary>
    public double Weight { get; init; } = 1;
}

/// <summary>
/// The Echo Arena playing field with movement drawn over it.
///
/// Paths are rasterised additively rather than binned into a grid: every stroke deposits a
/// little light, so a lane skated a hundred times burns bright while a one-off run stays a
/// faint thread, and two teams crossing the same space blend towards white. That keeps the
/// individual routes legible, which a blurred density map destroys.
/// </summary>
public sealed class ArenaMap : ThemedElement
{
    public static readonly DependencyProperty TracksProperty = DependencyProperty.Register(
        nameof(Tracks), typeof(IReadOnlyList<TrackLayer>), typeof(ArenaMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnTracksChanged));

    public static readonly DependencyProperty ViewProperty = DependencyProperty.Register(
        nameof(View), typeof(ArenaView), typeof(ArenaMap),
        new FrameworkPropertyMetadata(ArenaView.TopDown,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure,
            OnTracksChanged));

    public static readonly DependencyProperty ShotsProperty = DependencyProperty.Register(
        nameof(Shots), typeof(IReadOnlyList<Vector3>), typeof(ArenaMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GoalsProperty = DependencyProperty.Register(
        nameof(Goals), typeof(IReadOnlyList<Vector3>), typeof(ArenaMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The side this map belongs to — highlights the goal they attack.</summary>
    public static readonly DependencyProperty AttackingProperty = DependencyProperty.Register(
        nameof(Attacking), typeof(TeamSide?), typeof(ArenaMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<TrackLayer>? Tracks
    {
        get => (IReadOnlyList<TrackLayer>?)GetValue(TracksProperty);
        set => SetValue(TracksProperty, value);
    }
    public ArenaView View { get => (ArenaView)GetValue(ViewProperty); set => SetValue(ViewProperty, value); }
    public IReadOnlyList<Vector3>? Shots
    {
        get => (IReadOnlyList<Vector3>?)GetValue(ShotsProperty);
        set => SetValue(ShotsProperty, value);
    }
    public IReadOnlyList<Vector3>? Goals
    {
        get => (IReadOnlyList<Vector3>?)GetValue(GoalsProperty);
        set => SetValue(GoalsProperty, value);
    }
    public TeamSide? Attacking { get => (TeamSide?)GetValue(AttackingProperty); set => SetValue(AttackingProperty, value); }

    private BitmapSource? _image;
    private Size _imageFor;
    private string _imageColours = "";

    /// <summary>
    /// A top-down render of the arena, drawn to the same 80 x 32 m bounds the analysis uses,
    /// so routes line up with the geometry players actually skate around.
    /// </summary>
    private static readonly BitmapSource? ArenaFloor = LoadFloor();

    private static BitmapSource? LoadFloor()
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri("pack://application:,,,/Spark;component/ReplayAnalyser/Resources/arena-top.png", UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            // Missing artwork should not take the map down; the plain floor still works.
            return null;
        }
    }

    public ArenaMap()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        ClipToBounds = true;
    }

    private static void OnTracksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ArenaMap)d)._image = null;

    // The routes are baked into the bitmap in the old theme's team colours.
    protected override void OnThemeChanged() => _image = null;

    private const double Pad = 6;
    private double ArenaWidthMetres => 2 * ArenaGeometry.HalfLength;
    private double ArenaHeightMetres => View == ArenaView.TopDown
        ? 2 * ArenaGeometry.HalfWidth
        : 2 * ArenaGeometry.HalfHeight;

    /// <summary>Take the width on offer and ask for exactly the height the arena needs.</summary>
    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) || available.Width <= 0 ? 640 : available.Width;
        double h = (w - Pad * 2) * (ArenaHeightMetres / ArenaWidthMetres) + Pad * 2;
        if (!double.IsInfinity(available.Height) && available.Height > 0 && h > available.Height)
        {
            h = available.Height;
            w = (h - Pad * 2) * (ArenaWidthMetres / ArenaHeightMetres) + Pad * 2;
        }
        return new Size(w, h);
    }

    /// <summary>The floor the routes glow against: the theme's own ground where that is dark enough.</summary>
    private static Color FloorColour(ChartPalette pal) => pal.IsDark
        ? Draw.Mix(pal.GroundColor, Colors.Black, 0.35)
        : Color.FromRgb(0x0B, 0x0E, 0x14);

    private static Color LayerColour(TrackLayer layer, ChartPalette pal) =>
        layer.Color ?? Draw.Glow(pal.ColorOf(layer.Tone));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 20) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var pal = Palette;

        double scale = Math.Min((w - Pad * 2) / ArenaWidthMetres, (h - Pad * 2) / ArenaHeightMetres);
        double pw = ArenaWidthMetres * scale, ph = ArenaHeightMetres * scale;
        var field = new Rect((w - pw) / 2, (h - ph) / 2, pw, ph);

        double radius = Math.Min(14, ph / 4);
        var clip = new RectangleGeometry(field, radius, radius);
        clip.Freeze();

        // Dark floor, so the additive lines have something to glow against.
        var floorColour = FloorColour(pal);
        var floorBrush = Draw.Solid(floorColour);
        dc.DrawRoundedRectangle(floorBrush, null, field, radius, radius);

        // The real arena floor underneath, held well back so the movement stays the subject.
        var floor = View == ArenaView.TopDown ? ArenaFloor : null;
        if (floor != null)
        {
            dc.PushClip(clip);
            dc.PushOpacity(0.42);
            // The render has orange on the left; this app puts blue there, so mirror it.
            dc.PushTransform(new ScaleTransform(-1, 1, field.Left + field.Width / 2, 0));
            dc.DrawImage(floor, field);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }

        double Zx(float z) => field.Left + (z + ArenaGeometry.HalfLength) / ArenaWidthMetres * field.Width;
        double Cy(float v) => View == ArenaView.TopDown
            ? field.Top + (v + ArenaGeometry.HalfWidth) / ArenaHeightMetres * field.Height
            : field.Bottom - (v + ArenaGeometry.HalfHeight) / ArenaHeightMetres * field.Height;

        dc.PushClip(clip);

        // Third lines sit under the movement. Over the arena render they only need to hint at
        // the zone boundaries, so they drop right back; on the plain floor they carry it alone.
        byte tone = floor != null ? (byte)0x44 : (byte)0x2C;
        var thirdPen = new Pen(Draw.Solid(tone, tone, tone, floor != null ? (byte)0x70 : (byte)0xFF), 1)
        {
            DashStyle = new DashStyle(new double[] { 5, 5 }, 0),
        };
        thirdPen.Freeze();
        foreach (float z in new[] { -ArenaGeometry.HalfLength / 3f, ArenaGeometry.HalfLength / 3f })
        {
            double x = Math.Round(Zx(z)) + 0.5;
            dc.DrawLine(thirdPen, new Point(x, field.Top), new Point(x, field.Bottom));
        }
        if (floor == null)
        {
            double mid = Math.Round(Zx(0)) + 0.5;
            dc.DrawLine(Draw.Pen(Draw.Solid(0x36, 0x36, 0x36), 1.2), new Point(mid, field.Top), new Point(mid, field.Bottom));
        }

        // The movement itself.
        var size = new Size(Math.Round(field.Width), Math.Round(field.Height));
        string colours = string.Join("|", (Tracks ?? Array.Empty<TrackLayer>()).Select(l => LayerColour(l, pal).ToString()));
        if (_image == null || _imageFor != size || _imageColours != colours)
        {
            _image = RenderTracks((int)size.Width, (int)size.Height, Zx, Cy, field, pal);
            _imageFor = size;
            _imageColours = colours;
        }
        if (_image != null) dc.DrawImage(_image, field);

        var blue = Draw.Solid(Draw.Glow(pal.ColorOf(Tone.Blue)));
        var orange = Draw.Solid(Draw.Glow(pal.ColorOf(Tone.Orange)));
        DrawGoal(dc, Zx(-ArenaGeometry.GoalZ), field, scale, blue, Attacking == TeamSide.Orange, dpi, "BLUE");
        DrawGoal(dc, Zx(ArenaGeometry.GoalZ), field, scale, orange, Attacking == TeamSide.Blue, dpi, "ORANGE");

        foreach (var s in Shots ?? Array.Empty<Vector3>())
        {
            var p = new Point(Zx(s.Z), Cy(View == ArenaView.TopDown ? s.X : s.Y));
            dc.DrawEllipse(null, Draw.Pen(Draw.Solid(0xD2, 0xD2, 0xD2, 0xC0), 1.5), p, 5, 5);
        }
        var good = Draw.Solid(Draw.Glow(pal.ColorOf(Tone.Good)));
        foreach (var g in Goals ?? Array.Empty<Vector3>())
        {
            var p = new Point(Zx(g.Z), Cy(View == ArenaView.TopDown ? g.X : g.Y));
            dc.DrawEllipse(good, Draw.Pen(floorBrush, 1.5), p, 6, 6);
        }
        dc.Pop();

        dc.DrawRoundedRectangle(null, Draw.Pen(pal.Border, 1.4), field, radius, radius);
    }

    private static void DrawGoal(DrawingContext dc, double x, Rect field, double scale,
                                 Brush colour, bool highlighted, double dpi, string label)
    {
        double cy = field.Top + field.Height / 2;
        double r = Math.Max(6, ArenaGeometry.GoalRadius * scale);

        dc.DrawLine(Draw.Pen(Draw.Fade(colour, highlighted ? 0.5 : 0.22), 1.2),
                    new Point(x, field.Top + 2), new Point(x, field.Bottom - 2));
        dc.DrawEllipse(null, Draw.Pen(Draw.Fade(colour, highlighted ? 1 : 0.5), highlighted ? 2.2 : 1.4),
                       new Point(x, cy), r, r);

        var t = Draw.Text(label, 9.5, Draw.Fade(colour, highlighted ? 1 : 0.55), dpi, Draw.UiBold);
        dc.DrawText(t, new Point(x - t.Width / 2, field.Bottom - t.Height - 5));
    }

    // ------------------------------------------------------------------ additive rasteriser

    /// <summary>
    /// Draw every path into a floating-point RGB buffer, one thin anti-aliased line at a
    /// time, adding light rather than replacing it. Overlap is what carries the information:
    /// a lane crossed once stays dim, a lane crossed constantly saturates to white.
    /// </summary>
    private BitmapSource? RenderTracks(int w, int h, Func<float, double> zx, Func<float, double> cy, Rect field,
                                       ChartPalette pal)
    {
        var layers = Tracks;
        if (layers == null || layers.Count == 0 || w < 8 || h < 8) return null;

        var acc = new float[w * h * 3];
        bool any = false;

        foreach (var layer in layers)
        {
            // Per-crossing deposit. Low, so it takes many passes over the same line to go
            // white — that is what makes the common routes stand out from the one-offs.
            var colour = LayerColour(layer, pal);
            float strength = (float)(0.16 * layer.Weight);
            float lr = colour.R / 255f * strength;
            float lg = colour.G / 255f * strength;
            float lb = colour.B / 255f * strength;

            foreach (var path in layer.Paths)
            {
                if (path.Count < 2) continue;
                any = true;

                double px = zx(path[0].Z) - field.Left;
                double py = cy(View == ArenaView.TopDown ? path[0].X : path[0].Y) - field.Top;
                for (int i = 1; i < path.Count; i++)
                {
                    double nx = zx(path[i].Z) - field.Left;
                    double ny = cy(View == ArenaView.TopDown ? path[i].X : path[i].Y) - field.Top;
                    AddLine(acc, w, h, px, py, nx, ny, lr, lg, lb);
                    px = nx; py = ny;
                }
            }
        }
        if (!any) return null;

        // A soft copy added back on top gives the halo around busy lanes without smearing the
        // lines themselves, which is the difference between a glow and a blur.
        AddBloom(acc, w, h, radius: 3, weight: 0.55f);

        // Scale so the busiest few percent clip to white and everything else keeps its range.
        float ceiling = Percentile(acc, w, h, 0.995f);
        if (ceiling <= 0) return null;

        var px8 = new byte[w * h * 4];
        for (int i = 0, o = 0; i < w * h; i++, o += 4)
        {
            float r = acc[i * 3] / ceiling;
            float g = acc[i * 3 + 1] / ceiling;
            float b = acc[i * 3 + 2] / ceiling;

            // Lift the faint end so single passes stay visible, then clamp the hot end.
            r = (float)Math.Pow(Math.Clamp(r, 0, 1), 0.62);
            g = (float)Math.Pow(Math.Clamp(g, 0, 1), 0.62);
            b = (float)Math.Pow(Math.Clamp(b, 0, 1), 0.62);

            float lum = Math.Max(r, Math.Max(g, b));
            byte a = (byte)(Math.Clamp(lum * 1.25f, 0, 1) * 255);
            if (a == 0) continue;

            px8[o + 0] = (byte)(Math.Clamp(b, 0, 1) * a);   // Pbgra32 is pre-multiplied
            px8[o + 1] = (byte)(Math.Clamp(g, 0, 1) * a);
            px8[o + 2] = (byte)(Math.Clamp(r, 0, 1) * a);
            px8[o + 3] = a;
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px8, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Anti-aliased line, accumulating colour into the buffer instead of overwriting.</summary>
    private static void AddLine(float[] acc, int w, int h,
                                double x0, double y0, double x1, double y1,
                                float r, float g, float b)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6)
        {
            AddPoint(acc, w, h, x0, y0, r, g, b);
            return;
        }
        // A long straight run between two samples is still one crossing, so step by pixel.
        int steps = (int)Math.Ceiling(len);
        if (steps > 4000) return;                 // guard against garbage coordinates

        double sx = dx / steps, sy = dy / steps;
        // Spread the same total light over however many steps this segment takes, so a fast
        // player crossing the arena does not deposit less than a slow one covering the same
        // ground.
        for (int i = 0; i <= steps; i++)
            AddPoint(acc, w, h, x0 + sx * i, y0 + sy * i, r, g, b);
    }

    /// <summary>Bilinear splat of one sample across the four pixels it straddles.</summary>
    private static void AddPoint(float[] acc, int w, int h, double x, double y,
                                 float r, float g, float b)
    {
        if (x < 0 || y < 0 || x >= w - 1 || y >= h - 1) return;
        int xi = (int)x, yi = (int)y;
        float fx = (float)(x - xi), fy = (float)(y - yi);

        Splat(acc, w, xi, yi, (1 - fx) * (1 - fy), r, g, b);
        Splat(acc, w, xi + 1, yi, fx * (1 - fy), r, g, b);
        Splat(acc, w, xi, yi + 1, (1 - fx) * fy, r, g, b);
        Splat(acc, w, xi + 1, yi + 1, fx * fy, r, g, b);
    }

    private static void Splat(float[] acc, int w, int x, int y, float k, float r, float g, float b)
    {
        int i = (y * w + x) * 3;
        acc[i] += r * k;
        acc[i + 1] += g * k;
        acc[i + 2] += b * k;
    }

    private static void AddBloom(float[] acc, int w, int h, int radius, float weight)
    {
        var blur = new float[acc.Length];
        Array.Copy(acc, blur, acc.Length);

        var tmp = new float[acc.Length];
        for (int c = 0; c < 3; c++)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0; int n = 0;
                    for (int d = -radius; d <= radius; d++)
                    {
                        int xx = x + d;
                        if (xx < 0 || xx >= w) continue;
                        sum += blur[(y * w + xx) * 3 + c]; n++;
                    }
                    tmp[(y * w + x) * 3 + c] = n == 0 ? 0 : sum / n;
                }

            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    float sum = 0; int n = 0;
                    for (int d = -radius; d <= radius; d++)
                    {
                        int yy = y + d;
                        if (yy < 0 || yy >= h) continue;
                        sum += tmp[(yy * w + x) * 3 + c]; n++;
                    }
                    blur[(y * w + x) * 3 + c] = n == 0 ? 0 : sum / n;
                }
        }

        for (int i = 0; i < acc.Length; i++) acc[i] += blur[i] * weight;
    }

    private static float Percentile(float[] acc, int w, int h, float fraction)
    {
        var values = new List<float>();
        for (int i = 0; i < w * h; i++)
        {
            float v = Math.Max(acc[i * 3], Math.Max(acc[i * 3 + 1], acc[i * 3 + 2]));
            if (v > 0) values.Add(v);
        }
        if (values.Count == 0) return 0;
        values.Sort();
        int idx = (int)Math.Clamp(fraction * (values.Count - 1), 0, values.Count - 1);
        return values[idx];
    }
}
