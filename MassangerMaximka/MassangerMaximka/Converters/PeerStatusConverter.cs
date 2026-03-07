using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Maui.Controls;

namespace MassangerMaximka.Converters;

public class PeerStatusConverter : IValueConverter
{
    private static readonly Regex StateTag = new(@"^\[([^\]]*)\]", RegexOptions.Compiled);
    private static readonly Regex NewBadge = new(@"\[NEW\]", RegexOptions.Compiled);
    private static readonly Regex Endpoint = new(@"\[(\d[\d.:]+:\d+)\]", RegexOptions.Compiled);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        var state = StateTag.Match(s) is { Success: true } m ? m.Groups[1].Value : "";
        var hasNew = NewBadge.IsMatch(s);
        var ep = Endpoint.Match(s) is { Success: true } em ? em.Groups[1].Value : "";

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(state)) parts.Add(state);
        if (hasNew) parts.Add("NEW");
        if (!string.IsNullOrEmpty(ep)) parts.Add(ep);
        return string.Join("  ", parts);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
