// Ported from the Replay Analyser (EchoAnalyser.App/Controls/MetricBar.cs). The centred bar is
// unchanged. It takes Spark's theme colours, and on a narrow page the label and figures move
// above the track instead of the whole row disappearing.
#nullable enable

using System;
using System.Windows;
using System.Windows.Media;

namespace Spark.ReplayAnalyser.Controls;

/// <summary>
/// One habit measured against the players in the same lobbies, drawn as a bar growing out
/// from a centre line: right and green when the player is ahead of the room, left and red
/// when behind. The centre is "same as everyone else", which is the only comparison that
/// means anything across mixed skill levels.
/// </summary>
public sealed class MetricBar : ThemedElement
{
    public static readonly DependencyProperty EdgeProperty = DependencyProperty.Register(
        nameof(Edge), typeof(double), typeof(MetricBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(MetricBar),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
        nameof(ValueText), typeof(string), typeof(MetricBar),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeerTextProperty = DependencyProperty.Register(
        nameof(PeerText), typeof(string), typeof(MetricBar),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public double Edge { get => (double)GetValue(EdgeProperty); set => SetValue(EdgeProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public string PeerText { get => (string)GetValue(PeerTextProperty); set => SetValue(PeerTextProperty, value); }

    private const double LabelWidth = 168;
    private const double NumberWidth = 116;
    private const double TrackHeight = 9;
    private const double RowHeight = 28;
    /// <summary>Below this width the label and figures sit above the track.</summary>
    private const double StackBelow = 420;
    /// <summary>Edge magnitude that fills half the track. Beyond this the bar simply clips.</summary>
    private const double FullScale = 1.0;

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 460 : available.Width;
        return new Size(w, w < StackBelow ? RowHeight + 16 : RowHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 120) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var pal = Palette;

        bool stacked = w < StackBelow;
        double rowTop = h - RowHeight;

        var numbers = Draw.Text($"{ValueText}  vs {PeerText}", 11.5, pal.Dim, dpi, Draw.Mono);
        var label = Draw.Text(Label, 12.5, pal.Text, dpi);
        label.MaxLineCount = 1;
        label.Trimming = TextTrimming.CharacterEllipsis;

        double trackLeft;
        if (stacked)
        {
            // Label left and figures right on the top line; the track gets the full width below.
            label.MaxTextWidth = Math.Max(10, w - numbers.Width - 12);
            dc.DrawText(label, new Point(0, 0));
            dc.DrawText(numbers, new Point(w - numbers.Width, (label.Height - numbers.Height) / 2));
            trackLeft = 0;
        }
        else
        {
            label.MaxTextWidth = LabelWidth - 8;
            dc.DrawText(label, new Point(0, rowTop + (RowHeight - label.Height) / 2));
            dc.DrawText(numbers, new Point(LabelWidth, rowTop + (RowHeight - numbers.Height) / 2));
            // Long figures push the track along rather than running underneath it.
            trackLeft = LabelWidth + Math.Max(NumberWidth, numbers.Width + 14);
        }

        double trackWidth = Math.Max(30, w - trackLeft);
        double centre = trackLeft + trackWidth / 2;
        double y = rowTop + (RowHeight - TrackHeight) / 2;

        dc.DrawRoundedRectangle(pal.Raised, null,
            new Rect(trackLeft, y, trackWidth, TrackHeight), TrackHeight / 2, TrackHeight / 2);

        double frac = Math.Clamp(Edge / FullScale, -1, 1);
        double len = Math.Abs(frac) * (trackWidth / 2);
        if (len > 1)
        {
            var brush = pal.Of(Edge >= 0 ? Tone.Good : Tone.Bad);
            var rect = Edge >= 0
                ? new Rect(centre, y, len, TrackHeight)
                : new Rect(centre - len, y, len, TrackHeight);
            dc.DrawRoundedRectangle(brush, null, rect, TrackHeight / 2, TrackHeight / 2);
        }

        // Centre line: level with the room.
        dc.DrawRectangle(pal.Faint, null,
            new Rect(Math.Round(centre) - 0.5, y - 3, 1, TrackHeight + 6));
    }
}
