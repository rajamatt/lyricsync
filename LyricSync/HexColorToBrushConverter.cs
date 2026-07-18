using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace LyricSync;

/// <summary>"#RRGGBB" / "#AARRGGBB" string → SolidColorBrush, for classic {Binding}.</summary>
public sealed partial class HexColorToBrushConverter : IValueConverter
{
    private static readonly Windows.UI.Color Fallback = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xEB, 0x3B);

    public object Convert(object value, Type targetType, object parameter, string language) =>
        new SolidColorBrush(Parse(value as string));

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static Windows.UI.Color Parse(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#' || (hex.Length != 7 && hex.Length != 9))
        {
            return Fallback;
        }

        try
        {
            var digits = System.Globalization.NumberStyles.HexNumber;
            var culture = System.Globalization.CultureInfo.InvariantCulture;

            var offset = hex.Length == 9 ? 3 : 1;
            var a = hex.Length == 9 ? byte.Parse(hex.AsSpan(1, 2), digits, culture) : (byte)0xFF;
            var r = byte.Parse(hex.AsSpan(offset, 2), digits, culture);
            var g = byte.Parse(hex.AsSpan(offset + 2, 2), digits, culture);
            var b = byte.Parse(hex.AsSpan(offset + 4, 2), digits, culture);
            return Windows.UI.Color.FromArgb(a, r, g, b);
        }
        catch
        {
            return Fallback;
        }
    }
}
