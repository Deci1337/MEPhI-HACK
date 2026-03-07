using System.Globalization;
using Microsoft.Maui.Controls;

namespace MassangerMaximka.Converters;

public class PeerStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        if (s.StartsWith("#CH "))
        {
            var parts = s.Split('|');
            return parts.Length > 1 ? parts[1].Trim() : "Channel";
        }

        string state = "";
        if (s.StartsWith("["))
        {
            int idx = s.IndexOf(']');
            if (idx >= 0) state = s.Substring(1, idx - 1);
        }

        bool isNew = s.Contains("[NEW]");

        string ep = "";
        if (s.EndsWith("]"))
        {
            int idx = s.LastIndexOf('[');
            if (idx >= 0) ep = s.Substring(idx + 1, s.Length - idx - 2);
        }

        var res = state;
        if (isNew) res += "  NEW";
        if (!string.IsNullOrEmpty(ep)) res += $"  {ep}";
        return res.Trim();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
