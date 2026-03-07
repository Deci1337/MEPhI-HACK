using System.Globalization;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace MassangerMaximka.Converters;

public class StringToAvatarBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var str = value as string ?? "";
        var rnd = new Random(str.GetHashCode());

        var bg = Color.FromRgb(10, 10, 10);
        int numLines = rnd.Next(5, 10);
        float lineWidth = 0.014f;

        var positions = new List<float>();
        for (int i = 0; i < numLines; i++)
            positions.Add((float)(rnd.NextDouble() * 0.88 + 0.06));
        positions.Sort();

        var stops = new GradientStopCollection { new GradientStop(bg, 0f) };
        float prev = 0f;

        foreach (var pos in positions)
        {
            int brightness = rnd.Next(90, 255);
            var line = Color.FromRgb(brightness, brightness, brightness);
            float lo = Math.Max(prev, pos - lineWidth);
            float hi = Math.Min(1f, pos + lineWidth);

            if (lo > prev + 0.001f) stops.Add(new GradientStop(bg, lo));
            stops.Add(new GradientStop(line, pos));
            stops.Add(new GradientStop(bg, hi));
            prev = hi;
        }

        if (prev < 1f) stops.Add(new GradientStop(bg, 1f));

        // Sort stops by offset (required)
        var sorted = stops.OrderBy(s => s.Offset).ToList();
        var finalStops = new GradientStopCollection();
        foreach (var s in sorted) finalStops.Add(s);

        double angle = rnd.NextDouble() * Math.PI;
        return new LinearGradientBrush(finalStops,
            new Point(0, 0),
            new Point(Math.Abs(Math.Cos(angle)), Math.Abs(Math.Sin(angle))));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
