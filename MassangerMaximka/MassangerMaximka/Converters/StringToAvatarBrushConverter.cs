using System.Globalization;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace MassangerMaximka.Converters;

public class StringToAvatarBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var str = value as string ?? "";
        var hash = str.GetHashCode();
        var rnd = new Random(hash);

        // Generate base grayscale/muted colors for the B&W theme
        var c1 = Color.FromRgb(rnd.Next(20, 100), rnd.Next(20, 100), rnd.Next(20, 100));
        var c2 = Color.FromRgb(rnd.Next(100, 200), rnd.Next(100, 200), rnd.Next(100, 200));
        var c3 = Color.FromRgb(rnd.Next(50, 150), rnd.Next(50, 150), rnd.Next(50, 150));

        var stops = new GradientStopCollection();
        
        // Create sharp stripes
        int numStripes = rnd.Next(3, 6);
        float step = 1.0f / numStripes;
        
        for (int i = 0; i < numStripes; i++)
        {
            var color = i % 3 == 0 ? c1 : (i % 3 == 1 ? c2 : c3);
            stops.Add(new GradientStop(color, i * step));
            stops.Add(new GradientStop(color, (i + 1) * step));
        }

        return new LinearGradientBrush(stops, new Point(0, 0), new Point(rnd.NextDouble(), rnd.NextDouble()));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
