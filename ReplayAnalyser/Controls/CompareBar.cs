// Ported from the Replay Analyser (EchoAnalyser.App/Controls/CompareBar.cs). The split track is
// unchanged. It takes Spark's theme colours, and when there is no room for the label beside the
// track it moves the label above it instead of drawing nothing.
#nullable enable

using System;
using System.Windows;
using System.Windows.Media;

namespace Spark.ReplayAnalyser.Controls;

/// <summary>
/// One head-to-head stat. The label sits on the left where the eye starts, the two numbers
/// bracket a single split track, and the winning side is the one whose bar is longer — the
/// bars share one track rather than sitting in two separate gutters, so the comparison is
/// readable at a glance instead of needing to be measured.
/// </summary>
public sealed class CompareBar : ThemedElement
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(CompareBar),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BlueProperty = DependencyProperty.Register(
        nameof(Blue), typeof(double), typeof(CompareBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OrangeProperty = DependencyProperty.Register(
        nameof(Orange), typeof(double), typeof(CompareBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(string), typeof(CompareBar),
        new FrameworkPropertyMetadata("0.#", FrameworkPropertyMetadataOptions.AffectsRender));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Blue { get => (double)GetValue(BlueProperty); set => SetValue(BlueProperty, value); }
    public double Orange { get => (double)GetValue(OrangeProperty); set => SetValue(OrangeProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }

    private const double MaxLabelWidth = 190;
    private const double NumberWidth = 58;
    private const double TrackHeight = 10;
    private const double RowHeight = 30;
    /// <summary>Below this width the label goes above the track rather than beside it.</summary>
    private const double StackBelow = 400;

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 520 : available.Width;
        return new Size(w, w < StackBelow ? RowHeight + 16 : RowHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < NumberWidth * 2 + 40) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var pal = Palette;

        bool stacked = w < StackBelow;
        double labelWidth = stacked ? 0 : Math.Min(MaxLabelWidth, w * 0.3);
        // The bar row is the bottom RowHeight; a stacked label takes the space above it.
        double rowTop = h - RowHeight;

        bool blueWins = Blue > Orange;
        bool tied = Math.Abs(Blue - Orange) < 1e-9;

        var label = Draw.Text(Label, 12, pal.Dim, dpi);
        label.MaxTextWidth = Math.Max(10, stacked ? w : labelWidth - 8);
        label.MaxLineCount = 1;
        label.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(label, stacked
            ? new Point(0, 0)
            : new Point(0, rowTop + (RowHeight - label.Height) / 2));

        var blue = pal.Of(Tone.Blue);
        var orange = pal.Of(Tone.Orange);

        // Winning number is bright and bold; the other is dimmed, so the row can be scanned.
        var blueText = Draw.Text(Blue.ToString(Format), 13.5,
            tied || blueWins ? blue : Draw.Fade(blue, 0.62), dpi, Draw.Mono);
        var orangeText = Draw.Text(Orange.ToString(Format), 13.5,
            tied || !blueWins ? orange : Draw.Fade(orange, 0.62), dpi, Draw.Mono);

        double trackLeft = labelWidth + NumberWidth;
        double trackRight = w - NumberWidth;
        double trackWidth = Math.Max(20, trackRight - trackLeft);
        double centre = trackLeft + trackWidth / 2;
        double y = rowTop + (RowHeight - TrackHeight) / 2;

        dc.DrawText(blueText, new Point(trackLeft - 10 - blueText.Width, rowTop + (RowHeight - blueText.Height) / 2));
        dc.DrawText(orangeText, new Point(trackRight + 10, rowTop + (RowHeight - orangeText.Height) / 2));

        // Empty track first, then each side's share growing outward from the middle.
        dc.DrawRoundedRectangle(pal.Raised, null,
            new Rect(trackLeft, y, trackWidth, TrackHeight), TrackHeight / 2, TrackHeight / 2);

        double total = Math.Abs(Blue) + Math.Abs(Orange);
        if (total <= 0)
        {
            var none = Draw.Text("none", 10.5, pal.Faint, dpi);
            dc.DrawText(none, new Point(centre - none.Width / 2, rowTop + (RowHeight - none.Height) / 2));
            return;
        }

        double half = trackWidth / 2;
        double bw = half * (Math.Abs(Blue) / total);
        double ow = half * (Math.Abs(Orange) / total);

        if (bw > 1)
            dc.DrawRoundedRectangle(blue, null,
                new Rect(centre - bw, y, bw, TrackHeight), TrackHeight / 2, TrackHeight / 2);
        if (ow > 1)
            dc.DrawRoundedRectangle(orange, null,
                new Rect(centre, y, ow, TrackHeight), TrackHeight / 2, TrackHeight / 2);

        // Hairline at the midpoint so an even split is obvious.
        dc.DrawRectangle(pal.Card, null,
            new Rect(Math.Round(centre) - 0.5, y - 2, 1, TrackHeight + 4));
    }
}
