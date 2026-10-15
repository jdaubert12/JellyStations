using System.Text.RegularExpressions;
using Jellyfin.Plugin.TvStation.Models;

namespace Jellyfin.Plugin.TvStation.Library;

/// <summary>What a source's display name says about the item it was picked from.</summary>
/// <param name="Name">Item title (movie, series, collection, playlist, library, season or episode name).</param>
/// <param name="Year">Production year when the name carried a "(2001)" suffix.</param>
/// <param name="SeriesName">For seasons and episodes: the series title.</param>
/// <param name="Season">Season number when it could be read from the name.</param>
/// <param name="Episode">Episode number when it could be read from the name.</param>
public sealed record ParsedSourceName(string Name, int? Year, string? SeriesName, int? Season, int? Episode);

/// <summary>A library item that may be what a display name refers to.</summary>
public sealed record NameCandidate(Guid Id, string Name, int? Year = null, string? SeriesName = null, int? Season = null, int? Episode = null);

/// <summary>
/// Pure matching of the display names the editor stores beside item ids ("Title (2001)", "Series S01E02 – Episode",
/// "Series – Season 2") against library candidates, so an exported station can be imported on another server.
/// </summary>
public static partial class SourceNameMatcher
{
    [GeneratedRegex(@"^(?<name>.*?)\s*\((?<year>(19|20)\d{2})\)\s*$")]
    private static partial Regex YearSuffix();

    [GeneratedRegex(@"^(?<series>.+?)\s+S(?<season>\d{1,3})E(?<episode>\d{1,4})\s*[–\-]\s*(?<name>.*)$")]
    private static partial Regex EpisodeName();

    [GeneratedRegex(@"^(?<series>.+?)\s+[–\-]\s+(?<name>.+)$")]
    private static partial Regex SeriesDashName();

    [GeneratedRegex(@"^(Season|Series|Staffel|Saison|Temporada)\s+(?<n>\d{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonNumber();

    /// <summary>True when the display name looks like an episode ("Series S01E02 – Title").</summary>
    public static bool LooksLikeEpisode(string? displayName) => !string.IsNullOrWhiteSpace(displayName) && EpisodeName().IsMatch(displayName.Trim());

    public static ParsedSourceName Parse(SourceKind kind, string displayName)
    {
        var text = (displayName ?? string.Empty).Trim();
        switch (kind)
        {
            case SourceKind.Episode:
                {
                    var m = EpisodeName().Match(text);
                    if (m.Success)
                    {
                        return new ParsedSourceName(m.Groups["name"].Value.Trim(), null, m.Groups["series"].Value.Trim(), int.Parse(m.Groups["season"].Value), int.Parse(m.Groups["episode"].Value));
                    }

                    var d = SeriesDashName().Match(text);
                    return d.Success
                        ? new ParsedSourceName(d.Groups["name"].Value.Trim(), null, d.Groups["series"].Value.Trim(), null, null)
                        : new ParsedSourceName(text, null, null, null, null);
                }

            case SourceKind.Season:
                {
                    var d = SeriesDashName().Match(text);
                    var series = d.Success ? d.Groups["series"].Value.Trim() : null;
                    var name = d.Success ? d.Groups["name"].Value.Trim() : text;
                    var n = SeasonNumber().Match(name);
                    return new ParsedSourceName(name, null, series, n.Success ? int.Parse(n.Groups["n"].Value) : null, null);
                }

            default:
                {
                    var y = YearSuffix().Match(text);
                    return y.Success
                        ? new ParsedSourceName(y.Groups["name"].Value.Trim(), int.Parse(y.Groups["year"].Value), null, null, null)
                        : new ParsedSourceName(text, null, null, null, null);
                }
        }
    }

    /// <summary>
    /// The one candidate the parsed name refers to, or null when none or several match. Titles compare
    /// case-insensitively; a year, series name, season or episode number in the parsed name must agree when the
    /// candidate carries one. A year only breaks ties: a lone title match is accepted even if its year differs.
    /// </summary>
    public static Guid? Pick(IEnumerable<NameCandidate> candidates, ParsedSourceName parsed)
    {
        // A library whose titles still carry "(2020)" (no metadata provider) compares on the bare title too.
        candidates = candidates.Select(c =>
        {
            var y = YearSuffix().Match(c.Name ?? string.Empty);
            return y.Success ? c with { Name = y.Groups["name"].Value.Trim(), Year = c.Year ?? int.Parse(y.Groups["year"].Value) } : c;
        }).ToList();

        var byName = candidates.Where(c => Same(c.Name, parsed.Name) && SeriesAgrees(c, parsed) && NumbersAgree(c, parsed)).ToList();
        if (byName.Count == 0 && parsed.Season.HasValue && parsed.Episode.HasValue)
        {
            // Episode renamed: fall back to series + numbers.
            byName = candidates.Where(c => SeriesAgrees(c, parsed) && c.Season == parsed.Season && c.Episode == parsed.Episode).ToList();
        }

        if (byName.Count == 1)
        {
            return byName[0].Id;
        }

        if (byName.Count > 1 && parsed.Year.HasValue)
        {
            var byYear = byName.Where(c => c.Year == parsed.Year).ToList();
            if (byYear.Count == 1)
            {
                return byYear[0].Id;
            }
        }

        return null;
    }

    private static bool SeriesAgrees(NameCandidate c, ParsedSourceName p) => p.SeriesName is null || c.SeriesName is null || Same(c.SeriesName, p.SeriesName);

    private static bool NumbersAgree(NameCandidate c, ParsedSourceName p)
        => (!p.Season.HasValue || !c.Season.HasValue || c.Season == p.Season) && (!p.Episode.HasValue || !c.Episode.HasValue || c.Episode == p.Episode);

    private static bool Same(string? a, string? b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        return s.Trim().Replace('’', '\'').Replace('–', '-').Replace('—', '-');
    }
}
