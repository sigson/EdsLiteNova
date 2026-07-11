using System.Globalization;

namespace Eds.Maui.Converters;

/// <summary>
/// Local replacements for the CommunityToolkit.Maui converters used in XAML, so the
/// UI has no hard dependency on that package. This matters for the Avalonia (net11.0)
/// head, where CommunityToolkit.Maui isn't referenced — the toolkit's XAML namespace
/// wouldn't resolve there and would fail App.xaml parsing.
/// </summary>
public sealed class InvertedBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value is null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;
}

/// <summary>True when the bound string is non-null and non-empty.</summary>
public sealed class IsStringNotNullOrEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrEmpty(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
