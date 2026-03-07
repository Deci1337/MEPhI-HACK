using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Maui.Controls;

namespace MassangerMaximka.Converters;

public class PeerNameConverter : IValueConverter
{
    // Input format: "[state][NEW]? DisplayName (nodeId) [ep]"
    private static readonly Regex LeadingBadges = new(@"^\[[^\]]*\](\s*\[NEW\])?\s*", RegexOptions.Compiled);
    private static readonly Regex TrailingInfo = new(@"\s*(\([^)]*\))?\s*\[[^\]]*\].*$", RegexOptions.Compiled);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        var noState = LeadingBadges.Replace(s, "");
        var name = TrailingInfo.Replace(noState, "").Trim();
        return string.IsNullOrEmpty(name) ? s : name;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
