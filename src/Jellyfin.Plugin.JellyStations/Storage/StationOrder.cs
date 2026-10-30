using System.Globalization;
using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Storage;

/// <summary>The one order stations are listed in everywhere.</summary>
public static class StationOrder
{
    /// <summary>
    /// Channel number as a number ("2" before "10", "2.1" between "2" and "3"; non-numeric last), then the raw
    /// channel string, then the name (both case-insensitive).
    /// </summary>
    public static IComparer<Station> ByChannel { get; } = Comparer<Station>.Create(Compare);

    private static int Compare(Station? a, Station? b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }

        if (a is null)
        {
            return -1;
        }

        if (b is null)
        {
            return 1;
        }

        var byNumber = NumberOf(a.ChannelNumber).CompareTo(NumberOf(b.ChannelNumber));
        if (byNumber != 0)
        {
            return byNumber;
        }

        var byChannel = StringComparer.OrdinalIgnoreCase.Compare(a.ChannelNumber, b.ChannelNumber);
        return byChannel != 0 ? byChannel : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
    }

    private static double NumberOf(string? channel)
        => double.TryParse(channel, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.MaxValue;
}
