// Ported from the Replay Analyser (EchoAnalyser.App/Controls/RadarChart.cs). Geometry and labels
// are unchanged; the two shapes take theme tones rather than fixed brushes.
#nullable enable

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Spark.ReplayAnalyser.Controls;

public sealed record RadarAxis(string Label, double Value);

/// <summary>
/// Radar plot of a player's rated areas. Two shapes can be overlaid so a player can be
/// compared against an opponent or against the lobby average.
/// </summary>
public sealed class RadarChart : ThemedElement
{
    public static readonly DependencyProperty AxesProperty = DependencyProperty.Register(
        nameof(Axes), typeof(IReadOnlyList<RadarAxis>), typeof(RadarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Optional second shape, drawn behind the first — the comparison.</summary>
    public static readonly DependencyProperty CompareProperty = DependencyProperty.Register(
        nameof(Compare), typeof(IReadOnlyList<RadarAxis>), typeof(RadarChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(Tone), typeof(RadarChart),
        new FrameworkPropertyMetadata(Tone.Accent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CompareToneProperty = DependencyProperty.Register(
        nameof(CompareTone), typeof(Tone), typeof(RadarChart),
        new FrameworkPropertyMetadata(Tone.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<RadarAxis>? Axes
    {
        get => (IReadOnlyList<RadarAxis>?)GetValue(AxesProperty);
        set => SetValue(AxesProperty, value);
    }
    public IReadOnlyList<RadarAxis>? Compare
    {
        get => (IReadOnlyList<RadarAxis>?)GetValue(CompareProperty);
        set => SetValue(CompareProperty, value);
    }
    public Tone Tone { get => (Tone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public Tone CompareTone { get => (Tone)GetValue(CompareToneProperty); set => SetValue(CompareToneProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var axes = Axes;
        if (axes == null || axes.Count < 3) return;

        double w = ActualWidth, h = ActualHeight;
        if (w < 80 || h < 80) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var pal = Palette;

        var centre = new Point(w / 2, h / 2 + 4);
        // Leave room for the labels that sit outside the outermost ring.
        double radius = Math.Min(w, h) / 2 - 34;
        if (radius < 20) return;

        int n = axes.Count;
        Point At(int i, double frac)
        {
            double a = -Math.PI / 2 + 2 * Math.PI * i / n;
            return new Point(centre.X + Math.Cos(a) * radius * frac,
                             centre.Y + Math.Sin(a) * radius * frac);
        }

        // ---- rings and spokes ----
        var ringPen = Draw.Pen(pal.Border, 1);
        for (int r = 1; r <= 4; r++)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(At(0, r / 4.0), false, true);
                for (int i = 1; i < n; i++) c.LineTo(At(i, r / 4.0), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, ringPen, g);
        }
        for (int i = 0; i < n; i++)
            dc.DrawLine(ringPen, centre, At(i, 1));

        // ---- shapes ----
        var accent = pal.Of(Tone);
        if (Compare is { Count: > 2 })
            DrawShape(dc, Compare, At, pal.Of(CompareTone), 0.10, 1.5, dashed: true);
        DrawShape(dc, axes, At, accent, 0.22, 2, dashed: false);

        // ---- vertices and labels ----
        for (int i = 0; i < n; i++)
        {
            var p = At(i, Math.Clamp(axes[i].Value / 100.0, 0, 1));
            dc.DrawEllipse(accent, Draw.Pen(pal.Card, 1.5), p, 3.5, 3.5);

            var label = Draw.Text(axes[i].Label, 10.5, pal.Dim, dpi, Draw.UiBold);
            var value = Draw.Text($"{axes[i].Value:F0}", 10.5, pal.Text, dpi, Draw.Mono);
            var anchor = At(i, 1.16);

            double lx = anchor.X - label.Width / 2;
            double ly = anchor.Y - label.Height / 2 - 5;
            // Nudge the side labels outward so they clear the outer ring.
            double cos = Math.Cos(-Math.PI / 2 + 2 * Math.PI * i / n);
            if (cos > 0.5) lx = anchor.X - 2;
            else if (cos < -0.5) lx = anchor.X - label.Width + 2;

            dc.DrawText(label, new Point(lx, ly));
            dc.DrawText(value, new Point(anchor.X - value.Width / 2 + (cos > 0.5 ? label.Width / 2 : cos < -0.5 ? -label.Width / 2 : 0),
                                         ly + label.Height - 1));
        }
    }

    private static void DrawShape(DrawingContext dc, IReadOnlyList<RadarAxis> axes,
                                  Func<int, double, Point> at, Brush colour,
                                  double fillOpacity, double thickness, bool dashed)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(at(0, Math.Clamp(axes[0].Value / 100.0, 0, 1)), true, true);
            for (int i = 1; i < axes.Count; i++)
                c.LineTo(at(i, Math.Clamp(axes[i].Value / 100.0, 0, 1)), true, false);
        }
        g.Freeze();

        var pen = new Pen(colour, thickness);
        if (dashed) pen.DashStyle = new DashStyle(new double[] { 3, 3 }, 0);
        pen.Freeze();
        dc.DrawGeometry(Draw.Fade(colour, fillOpacity), pen, g);
    }
}
