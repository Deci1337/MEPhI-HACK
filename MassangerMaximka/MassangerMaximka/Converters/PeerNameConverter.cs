using System.Globalization;
using Microsoft.Maui.Controls;

namespace MassangerMaximka.Converters;

public class PeerNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        if (s.StartsWith("#CH "))
        {
            var parts = s.Split('|');
            return parts[0].Replace("#CH", "").Trim();
        }

        var name = s;
        if (name.StartsWith("["))
        {
            int idx = name.IndexOf(']');
            if (idx >= 0) name = name.Substring(idx + 1).TrimStart();
        }
        if (name.StartsWith("[NEW]"))
        {
            int idx = name.IndexOf(']');
            if (idx >= 0) name = name.Substring(idx + 1).TrimStart();
        }
        if (name.EndsWith("]"))
        {
            int idx = name.LastIndexOf('[');
            if (idx >= 0) name = name.Substring(0, idx).TrimEnd();
        }
        if (name.EndsWith(")"))
        {
            int idx = name.LastIndexOf('(');
            if (idx >= 0) name = name.Substring(0, idx).TrimEnd();
        }

        return string.IsNullOrWhiteSpace(name) ? s : name;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
