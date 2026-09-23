// Ported from the Replay Analyser (EchoAnalyser.App/Controls/LineChart.cs). Scales, geometry and
// the crosshair are unchanged. It now draws in the Spark theme's colours, and the legend wraps
// onto extra rows instead of running off a narrow chart.
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Spark.ReplayAnalyser.Controls;

/// <summary>
/// A time-series chart drawn by hand: axes with round-numbered ticks, one or more lines,
/// optional event markers, and a crosshair that reads out the value under the pointer.
/// </summary>
public sealed class LineChart : ThemedElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<ChartSeries>), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(
        nameof(Markers), typeof(IReadOnlyList<ChartMarker>), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty YLabelProperty = DependencyProperty.Register(
        nameof(YLabel), typeof(string), typeof(LineChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Format applied to Y tick labels and the crosshair readout.</summary>
    public static readonly DependencyProperty YFormatProperty = DependencyProperty.Register(
        nameof(YFormat), typeof(string), typeof(LineChart),
        new FrameworkPropertyMetadata("0.#", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Pin the Y axis to this range instead of fitting the data.</summary>
    public static readonly DependencyProperty YMinProperty = DependencyProperty.Register(
        nameof(YMin), typeof(double?), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty YMaxProperty = DependencyProperty.Register(
        nameof(YMax), typeof(double?), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Format for X tick labels. Time series want the default clock; a chart counting matches
    /// wants plain numbers, and labelling match 3 as "0:03" is simply wrong.
    /// </summary>
    public static readonly DependencyProperty XIsTimeProperty = DependencyProperty.Register(
        nameof(XIsTime), typeof(bool), typeof(LineChart),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty XLabelProperty = DependencyProperty.Register(
        nameof(XLabel), typeof(string), typeof(LineChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Draw a heavier line at y = 0, for charts that swing either side of it.</summary>
    public static readonly DependencyProperty ShowZeroLineProperty = DependencyProperty.Register(
        nameof(ShowZeroLine), typeof(bool), typeof(LineChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartSeries>? Series
    {
        get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }
    public IReadOnlyList<ChartMarker>? Markers
    {
        get => (IReadOnlyList<ChartMarker>?)GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }
    public string YLabel { get => (string)GetValue(YLabelProperty); set => SetValue(YLabelProperty, value); }
    public string YFormat { get => (string)GetValue(YFormatProperty); set => SetValue(YFormatProperty, value); }
    public double? YMin { get => (double?)GetValue(YMinProperty); set => SetValue(YMinProperty, value); }
    public double? YMax { get => (double?)GetValue(YMaxProperty); set => SetValue(YMaxProperty, value); }
    public bool ShowZeroLine { get => (bool)GetValue(ShowZeroLineProperty); set => SetValue(ShowZeroLineProperty, value); }
    public bool XIsTime { get => (bool)GetValue(XIsTimeProperty); set => SetValue(XIsTimeProperty, value); }
    public string XLabel { get => (string)GetValue(XLabelProperty); set => SetValue(XLabelProperty, value); }

    private Point? _cursor;

    public LineChart()
    {
        ClipToBounds = true;
        MouseMove += (_, e) => { _cursor = e.GetPosition(this); InvalidateVisual(); };
        MouseLeave += (_, _) => { _cursor = null; InvalidateVisual(); };
    }

    private const double PadLeft = 46, PadRight = 14, PadBottom = 26;
    private const double LegendTop = 7, LegendRow = 17;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 50) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var pal = Palette;

        // The chart sits in a well cut into the card, like Spark's other inset panels.
        dc.DrawRoundedRectangle(pal.Ground, null, new Rect(0, 0, w, h), 6, 6);

        var series = (Series ?? Array.Empty<ChartSeries>()).Where(s => s.Points.Count > 0).ToList();
        if (series.Count == 0)
        {
            var none = Draw.Text("no data", 12, pal.Dim, dpi);
            dc.DrawText(none, new Point((w - none.Width) / 2, (h - none.Height) / 2));
            return;
        }

        // ---- legend layout first: it decides where the plot can start ----
        var legend = new List<(ChartSeries s, FormattedText t, Point at)>();
        {
            double lx = PadLeft + 2, row = 0;
            foreach (var s in series.Where(s => !string.IsNullOrEmpty(s.Name)))
            {
                var t = Draw.Text(s.Name, 10.5, pal.Dim, dpi);
                double itemWidth = 13 + t.Width;
                if (lx > PadLeft + 2 && lx + itemWidth > w - PadRight) { lx = PadLeft + 2; row++; }
                legend.Add((s, t, new Point(lx, LegendTop + row * LegendRow)));
                lx += itemWidth + 14;
            }
        }
        double padTop = legend.Count == 0 ? 14 : legend.Max(l => l.at.Y) + LegendRow + 4;

        var plot = new Rect(PadLeft, padTop, Math.Max(1, w - PadLeft - PadRight), Math.Max(1, h - padTop - PadBottom));

        double xMin = series.Min(s => s.Points.Min(p => p.X));
        double xMax = series.Max(s => s.Points.Max(p => p.X));
        double yMin = YMin ?? series.Min(s => s.Points.Min(p => p.Y));
        double yMax = YMax ?? series.Max(s => s.Points.Max(p => p.Y));
        if (Math.Abs(xMax - xMin) < 1e-9) xMax = xMin + 1;
        if (Math.Abs(yMax - yMin) < 1e-9) { yMin -= 0.5; yMax += 0.5; }
        if (YMin == null && YMax == null)
        {
            double pad = (yMax - yMin) * 0.12;
            yMin -= pad; yMax += pad;
        }

        double X(double v) => plot.Left + (v - xMin) / (xMax - xMin) * plot.Width;
        double Y(double v) => plot.Bottom - (v - yMin) / (yMax - yMin) * plot.Height;

        // ---- grid + axis labels ----
        var gridPen = Draw.Pen(pal.BorderSoft, 1);
        double yStep = Draw.NiceStep(yMax - yMin, plot.Height < 120 ? 3 : 4);
        for (double v = Math.Ceiling(yMin / yStep) * yStep; v <= yMax + 1e-9; v += yStep)
        {
            double y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var t = Draw.Text(v.ToString(YFormat), 10.5, pal.Faint, dpi);
            dc.DrawText(t, new Point(plot.Left - t.Width - 7, y - t.Height / 2));
        }

        // Fewer ticks when the chart is narrow, so the clock labels never collide.
        double xStep = Draw.NiceStep(xMax - xMin, Math.Clamp((int)(plot.Width / 70), 2, 6));
        if (!XIsTime) xStep = Math.Max(1, Math.Round(xStep));
        for (double v = Math.Ceiling(xMin / xStep) * xStep; v <= xMax + 1e-9; v += xStep)
        {
            double x = Math.Round(X(v)) + 0.5;
            dc.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var t = Draw.Text(XIsTime ? Draw.Clock(v) : v.ToString("0.#"), 10.5, pal.Faint, dpi);
            dc.DrawText(t, new Point(x - t.Width / 2, plot.Bottom + 6));
        }

        if (ShowZeroLine && yMin < 0 && yMax > 0)
        {
            double y = Math.Round(Y(0)) + 0.5;
            dc.DrawLine(Draw.Pen(pal.Border, 1.5), new Point(plot.Left, y), new Point(plot.Right, y));
        }

        // ---- event markers behind the lines ----
        foreach (var mk in Markers ?? Array.Empty<ChartMarker>())
        {
            if (mk.X < xMin || mk.X > xMax) continue;
            double x = Math.Round(X(mk.X)) + 0.5;
            var colour = pal.Of(mk.Tone);
            dc.DrawLine(Draw.Pen(Draw.Fade(colour, 0.35), 1),
                        new Point(x, plot.Top), new Point(x, plot.Bottom));
            dc.DrawEllipse(colour, null, new Point(x, plot.Top + 4), 3, 3);
        }

        // ---- the series ----
        foreach (var s in series)
        {
            var colour = pal.Of(s.Tone);
            var geo = BuildGeometry(s, X, Y, plot, out var fillGeo);
            if (s.FillOpacity > 0 && fillGeo != null)
                dc.DrawGeometry(Draw.Fade(colour, s.FillOpacity), null, fillGeo);
            dc.DrawGeometry(null, Draw.Pen(colour, s.Thickness), geo);
        }

        // ---- crosshair readout ----
        if (_cursor is { } c && c.X >= plot.Left && c.X <= plot.Right && c.Y >= plot.Top && c.Y <= plot.Bottom)
        {
            double xv = xMin + (c.X - plot.Left) / plot.Width * (xMax - xMin);
            double cx = Math.Round(c.X) + 0.5;
            dc.DrawLine(Draw.Pen(Draw.Fade(pal.Dim, 0.6), 1),
                        new Point(cx, plot.Top), new Point(cx, plot.Bottom));

            var lines = new List<(string text, Brush brush)>
            {
                (XIsTime ? Draw.Clock(xv) : $"{XLabel} {xv:0.#}".Trim(), pal.Text),
            };
            foreach (var s in series)
            {
                var pt = Nearest(s.Points, xv);
                if (pt == null) continue;
                var colour = pal.Of(s.Tone);
                dc.DrawEllipse(colour, Draw.Pen(pal.Ground, 1.5), new Point(X(pt.Value.X), Y(pt.Value.Y)), 4, 4);
                lines.Add(($"{s.Name}  {pt.Value.Y.ToString(YFormat)}", colour));
            }
            DrawTooltip(dc, lines, c, plot, dpi, pal);
        }

        // ---- legend ----
        foreach (var (s, t, at) in legend)
        {
            dc.DrawRoundedRectangle(pal.Of(s.Tone), null, new Rect(at.X, at.Y + 6, 9, 3), 1.5, 1.5);
            dc.DrawText(t, new Point(at.X + 13, at.Y));
        }

        if (!string.IsNullOrEmpty(YLabel))
        {
            var t = Draw.Text(YLabel, 10.5, pal.Faint, dpi);
            dc.PushTransform(new RotateTransform(-90, 12, plot.Top + plot.Height / 2));
            dc.DrawText(t, new Point(12 - t.Width / 2, plot.Top + plot.Height / 2 - t.Height / 2));
            dc.Pop();
        }
    }

    private static (double X, double Y)? Nearest(IReadOnlyList<(double X, double Y)> pts, double x)
    {
        if (pts.Count == 0) return null;
        int lo = 0, hi = pts.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (pts[mid].X < x) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && Math.Abs(pts[lo - 1].X - x) < Math.Abs(pts[lo].X - x)) lo--;
        return pts[lo];
    }

    private static Geometry BuildGeometry(ChartSeries s, Func<double, double> X, Func<double, double> Y,
                                          Rect plot, out Geometry? fill)
    {
        var stroke = new StreamGeometry();
        StreamGeometry? area = s.FillOpacity > 0 ? new StreamGeometry() : null;

        using (var sc = stroke.Open())
        {
            var first = new Point(X(s.Points[0].X), Y(s.Points[0].Y));
            sc.BeginFigure(first, false, false);
            var prev = first;
            for (int i = 1; i < s.Points.Count; i++)
            {
                var p = new Point(X(s.Points[i].X), Y(s.Points[i].Y));
                if (s.Stepped) sc.LineTo(new Point(p.X, prev.Y), true, false);
                sc.LineTo(p, true, false);
                prev = p;
            }
        }
        stroke.Freeze();

        if (area != null)
        {
            using (var ac = area.Open())
            {
                var first = new Point(X(s.Points[0].X), Y(s.Points[0].Y));
                ac.BeginFigure(new Point(first.X, plot.Bottom), true, true);
                ac.LineTo(first, true, false);
                var prev = first;
                for (int i = 1; i < s.Points.Count; i++)
                {
                    var p = new Point(X(s.Points[i].X), Y(s.Points[i].Y));
                    if (s.Stepped) ac.LineTo(new Point(p.X, prev.Y), true, false);
                    ac.LineTo(p, true, false);
                    prev = p;
                }
                ac.LineTo(new Point(prev.X, plot.Bottom), true, false);
            }
            area.Freeze();
        }

        fill = area;
        return stroke;
    }

    private static void DrawTooltip(DrawingContext dc, List<(string text, Brush brush)> lines,
                                    Point at, Rect plot, double dpi, ChartPalette pal)
    {
        var texts = lines.Select(l => Draw.Text(l.text, 11, l.brush, dpi)).ToList();
        double tw = texts.Max(t => t.Width) + 18;
        double th = texts.Sum(t => t.Height) + 12;
        double x = at.X + 14, y = at.Y - th - 8;
        if (x + tw > plot.Right) x = at.X - tw - 14;
        if (x < 0) x = 2;
        if (y < plot.Top) y = plot.Top + 4;

        var box = new Rect(x, y, tw, th);
        dc.DrawRoundedRectangle(Draw.Fade(pal.Raised, 0.97), Draw.Pen(pal.Border, 1), box, 5, 5);
        double ty = y + 6;
        foreach (var t in texts) { dc.DrawText(t, new Point(x + 9, ty)); ty += t.Height; }
    }
}
