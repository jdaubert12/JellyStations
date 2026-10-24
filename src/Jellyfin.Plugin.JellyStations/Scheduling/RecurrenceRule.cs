using System.Globalization;
using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Scheduling;

/// <summary>
/// Decides whether a block occurs on a given local date. Weeks run Monday to Sunday. For "every N weeks" the
/// weeks are counted from the Monday of the start date's week, in both directions, so the pattern is the same
/// however far the guide looks. For "weeks of the month" a week belongs to the month of its Monday and is the
/// Nth week when its Monday is the Nth Monday of that month; 5 means the week of the month's last Monday. That
/// keeps a Saturday-Sunday weekend in one week even when the month changes between them.
/// </summary>
internal static class RecurrenceRule
{
    public const string DateFormat = "yyyy-MM-dd";

    public static bool Occurs(SlotBlockInput block, DateTime localDate)
    {
        var date = localDate.Date;
        if (block.Recurrence == BlockRecurrence.Dates)
        {
            return block.Dates.Contains(date);
        }

        if (!block.Days.Contains(date.DayOfWeek))
        {
            return false;
        }

        switch (block.Recurrence)
        {
            case BlockRecurrence.EveryNWeeks:
                var n = Math.Max(1, block.IntervalWeeks);
                var weeks = (long)(MondayOf(date) - MondayOf(block.RecurrenceStart)).TotalDays / 7;
                return Padding.FloorMod(weeks, n) == 0;
            case BlockRecurrence.WeeksOfMonth:
                var monday = MondayOf(date);
                var week = WeekOfMonth(monday);
                return block.WeeksOfMonth.Contains(week) || (block.WeeksOfMonth.Contains(5) && IsLastWeekOfMonth(monday));
            default:
                return true;
        }
    }

    /// <summary>The Monday on or before a date.</summary>
    public static DateTime MondayOf(DateTime date)
    {
        var d = date.Date;
        return d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
    }

    /// <summary>1-5: which Monday of its month a week's Monday is.</summary>
    public static int WeekOfMonth(DateTime monday) => ((monday.Day - 1) / 7) + 1;

    /// <summary>True when no later Monday falls in the same month.</summary>
    public static bool IsLastWeekOfMonth(DateTime monday) => monday.Day + 7 > DateTime.DaysInMonth(monday.Year, monday.Month);

    public static DateTime? ParseDate(string? text)
        => DateTime.TryParseExact(text?.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : null;

    public static string Format(DateTime date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);
}
