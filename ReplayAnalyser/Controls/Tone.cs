// Part of the Replay Analyser port. The original app painted with fixed brushes handed out by its
// view models; Spark re-themes at runtime, so colours here are roles that resolve against the
// active theme instead.
#nullable enable

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Spark.ReplayAnalyser.Controls;

/// <summary>
/// A colour role rather than a colour. View models say what something means — blue team, a
/// strength, a weakness — and the view looks up what that looks like in the current Spark theme,
/// so a theme change repaints every page instead of leaving the colours it was built with.
/// </summary>
public enum Tone
{
    Primary,
    Dim,
    Faint,
    Accent,
    Blue,
    Orange,
    Good,
    Warn,
    Bad,
}

/// <summary>The theme resource keys behind each <see cref="Tone"/> (see ThemesController).</summary>
public static class ToneKeys
{
    /// <summary>The tone at full strength: lines, text, markers.</summary>
    public static string Solid(Tone tone) => tone switch
    {
        Tone.Primary => "TextPrimary",
        Tone.Dim => "TextDim",
        Tone.Faint => "TextFaint",
        Tone.Accent => "ControlAccent",
        Tone.Blue => "TeamBlue",
        Tone.Orange => "TeamOrange",
        Tone.Good => "StatusGood",
        Tone.Warn => "StatusWarn",
        Tone.Bad => "StatusBad",
        _ => "TextDim",
    };

    /// <summary>A wash of the tone that text can sit on. Neutral tones fall back to a raised surface.</summary>
    public static string Tint(Tone tone) => tone switch
    {
        Tone.Blue => "TeamBlueTint",
        Tone.Orange => "TeamOrangeTint",
        Tone.Good => "StatusGoodTint",
        Tone.Warn => "StatusWarnTint",
        Tone.Bad => "StatusBadTint",
        _ => "SurfaceRaised",
    };

    /// <summary>The border that goes with <see cref="Tint"/>.</summary>
    public static string Edge(Tone tone) => tone switch
    {
        Tone.Blue => "TeamBlueEdge",
        Tone.Orange => "TeamOrangeEdge",
        Tone.Good => "StatusGoodEdge",
        Tone.Warn => "StatusWarnEdge",
        Tone.Bad => "StatusBadEdge",
        _ => "SurfaceBorder",
    };
}

/// <summary>
/// Attached properties that paint an element in a <see cref="Tone"/>, as a live reference to the
/// theme brush — the XAML equivalent of <c>{DynamicResource TeamBlue}</c>, but chosen by a binding.
/// </summary>
public static class ToneBrush
{
    public static readonly DependencyProperty BackgroundProperty = Register("Background", Paint.Background);
    public static readonly DependencyProperty ForegroundProperty = Register("Foreground", Paint.Foreground);
    public static readonly DependencyProperty FillProperty = Register("Fill", Paint.Fill);
    public static readonly DependencyProperty StrokeProperty = Register("Stroke", Paint.Stroke);
    public static readonly DependencyProperty TintProperty = Register("Tint", Paint.Tint);
    public static readonly DependencyProperty EdgeProperty = Register("Edge", Paint.Edge);

    public static Tone? GetBackground(DependencyObject d) => (Tone?)d.GetValue(BackgroundProperty);
    public static void SetBackground(DependencyObject d, Tone? v) => d.SetValue(BackgroundProperty, v);
    public static Tone? GetForeground(DependencyObject d) => (Tone?)d.GetValue(ForegroundProperty);
    public static void SetForeground(DependencyObject d, Tone? v) => d.SetValue(ForegroundProperty, v);
    public static Tone? GetFill(DependencyObject d) => (Tone?)d.GetValue(FillProperty);
    public static void SetFill(DependencyObject d, Tone? v) => d.SetValue(FillProperty, v);
    public static Tone? GetStroke(DependencyObject d) => (Tone?)d.GetValue(StrokeProperty);
    public static void SetStroke(DependencyObject d, Tone? v) => d.SetValue(StrokeProperty, v);
    /// <summary>Background in the tone's tint.</summary>
    public static Tone? GetTint(DependencyObject d) => (Tone?)d.GetValue(TintProperty);
    public static void SetTint(DependencyObject d, Tone? v) => d.SetValue(TintProperty, v);
    /// <summary>Border brush in the tone's edge colour.</summary>
    public static Tone? GetEdge(DependencyObject d) => (Tone?)d.GetValue(EdgeProperty);
    public static void SetEdge(DependencyObject d, Tone? v) => d.SetValue(EdgeProperty, v);

    private enum Paint { Background, Foreground, Fill, Stroke, Tint, Edge }

    // Nullable with a null default, so the first value always registers as a change — with a
    // plain enum default, binding the default tone would never apply its brush.
    private static DependencyProperty Register(string name, Paint paint) =>
        DependencyProperty.RegisterAttached(name, typeof(Tone?), typeof(ToneBrush),
            new PropertyMetadata(null, (d, e) => { if (e.NewValue is Tone t) Apply(d, paint, t); }));

    private static void Apply(DependencyObject d, Paint paint, Tone tone)
    {
        string key = paint switch
        {
            Paint.Tint => ToneKeys.Tint(tone),
            Paint.Edge => ToneKeys.Edge(tone),
            _ => ToneKeys.Solid(tone),
        };

        DependencyProperty? target = (paint, d) switch
        {
            (Paint.Background or Paint.Tint, Border) => Border.BackgroundProperty,
            (Paint.Background or Paint.Tint, Panel) => Panel.BackgroundProperty,
            (Paint.Background or Paint.Tint, Control) => Control.BackgroundProperty,
            (Paint.Background or Paint.Tint, TextBlock) => TextBlock.BackgroundProperty,
            (Paint.Foreground, TextBlock) => TextBlock.ForegroundProperty,
            (Paint.Foreground, Control) => Control.ForegroundProperty,
            (Paint.Foreground, TextElement) => TextElement.ForegroundProperty,
            (Paint.Fill, Shape) => Shape.FillProperty,
            (Paint.Stroke, Shape) => Shape.StrokeProperty,
            (Paint.Edge, Border) => Border.BorderBrushProperty,
            (Paint.Edge, Control) => Control.BorderBrushProperty,
            _ => null,
        };
        if (target == null) return;

        switch (d)
        {
            case FrameworkElement fe: fe.SetResourceReference(target, key); break;
            case FrameworkContentElement fce: fce.SetResourceReference(target, key); break;
        }
    }
}
