// Part of the Replay Analyser port. The standalone app had a 1560px window and laid its pages
// out in fixed side-by-side grids; a Spark tab can be anywhere from about 440px wide upwards, so
// the pages reflow with these instead.
#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Spark.ReplayAnalyser.Controls;

/// <summary>
/// Lays its children out in as many equal columns as fit, wrapping to new rows as the page gets
/// narrower — side by side on a wide page, stacked on a narrow one. Cells in a row share the
/// row's height, so cards placed next to each other line up.
/// </summary>
public sealed class Columns : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(Columns),
        new FrameworkPropertyMetadata(320d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(Columns),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(Columns),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>
    /// Relative column widths such as "3,2", used when the row holds exactly that many columns.
    /// Any other column count falls back to equal widths.
    /// </summary>
    public static readonly DependencyProperty WeightsProperty = DependencyProperty.Register(
        nameof(Weights), typeof(string), typeof(Columns),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public string Weights { get => (string)GetValue(WeightsProperty); set => SetValue(WeightsProperty, value); }

    private double[] _widths = Array.Empty<double>();
    private readonly List<double> _rowHeights = new List<double>();

    private List<UIElement> Visible() =>
        InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    private double[] ColumnWidths(double width, int count)
    {
        if (count == 0) return Array.Empty<double>();
        double gap = Gap;
        int cols = (int)Math.Floor((width + gap) / (MinColumnWidth + gap));
        cols = Math.Clamp(cols, 1, Math.Max(1, Math.Min(MaxColumns, count)));

        double[] weights = (Weights ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : 1)
            .ToArray();
        if (weights.Length != cols) weights = Enumerable.Repeat(1.0, cols).ToArray();

        double usable = Math.Max(0, width - gap * (cols - 1));
        double total = weights.Sum();
        return weights.Select(k => usable * k / total).ToArray();
    }

    protected override Size MeasureOverride(Size available)
    {
        var children = Visible();
        double width = double.IsInfinity(available.Width)
            ? children.Count * MinColumnWidth + Math.Max(0, children.Count - 1) * Gap
            : available.Width;

        _widths = ColumnWidths(width, children.Count);
        _rowHeights.Clear();

        int cols = Math.Max(1, _widths.Length);
        for (int i = 0; i < children.Count; i++)
        {
            int col = i % cols;
            children[i].Measure(new Size(_widths.Length == 0 ? width : _widths[col], double.PositiveInfinity));
            if (col == 0) _rowHeights.Add(0);
            _rowHeights[^1] = Math.Max(_rowHeights[^1], children[i].DesiredSize.Height);
        }

        double height = _rowHeights.Sum() + Math.Max(0, _rowHeights.Count - 1) * Gap;
        return new Size(double.IsInfinity(available.Width) ? width : available.Width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = Visible();
        if (_widths.Length == 0 || Math.Abs(_widths.Sum() + Gap * (_widths.Length - 1) - final.Width) > 0.5)
        {
            // Arranged at a different width than measured: redo the rows for the real width.
            MeasureOverride(new Size(final.Width, double.PositiveInfinity));
        }

        int cols = Math.Max(1, _widths.Length);
        double y = 0;
        for (int i = 0; i < children.Count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            if (col == 0 && row > 0) y += _rowHeights[row - 1] + Gap;

            double x = 0;
            for (int c = 0; c < col; c++) x += _widths[c] + Gap;
            children[i].Arrange(new Rect(x, y, _widths[col], row < _rowHeights.Count ? _rowHeights[row] : 0));
        }
        return final;
    }
}

/// <summary>
/// Hands mouse-wheel scrolling on to the page. A table that scrolls sideways sits inside a page
/// that scrolls down, and without this the inner scroller swallows the wheel and the page stops
/// moving whenever the pointer is over the table.
/// </summary>
public static class WheelBubble
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(WheelBubble), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((bool)e.NewValue) element.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject d) return;
        if (VisualTreeHelper.GetParent(d) is not UIElement parent) return;

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        });
    }
}
