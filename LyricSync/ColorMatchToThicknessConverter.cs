using Microsoft.UI.Xaml.Data;

namespace LyricSync;

/// <summary>
/// Selection ring for the color swatches: thick border when the bound color equals
/// the swatch's own color (passed as ConverterParameter), hairline otherwise.
/// </summary>
public sealed partial class ColorMatchToThicknessConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase)
            ? new Thickness(2.5)
            : new Thickness(1);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
