// Ported from the Replay Analyser (EchoAnalyser.App/Converters.cs). Unchanged apart from the
// namespace; ShareToWidth is dropped, as the Spark views draw those bars with a ProgressBar.
#nullable enable

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Spark.ReplayAnalyser.Views;

public sealed class BoolToVisibility : IValueConverter
{
    /// <summary>Set to true to hide when the bound value is true.</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        bool b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is Visibility vis && vis == Visibility.Visible;
}

public sealed class NotEmptyToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        bool has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            System.Collections.ICollection col => col.Count > 0,
            _ => true,
        };
        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Flips a boolean, so a pair of radio buttons can share one backing property.</summary>
public sealed class InverseBool : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b ? !b : false;
}

/// <summary>Visible when a count is zero — for "nothing here" notes beside a list.</summary>
public sealed class ZeroToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}
