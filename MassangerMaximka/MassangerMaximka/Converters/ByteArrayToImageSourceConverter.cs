using System.Globalization;

namespace MassangerMaximka.Converters;

public sealed class ByteArrayToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is byte[] bytes && bytes.Length > 0
            ? ImageSource.FromStream(() => new MemoryStream(bytes))
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
