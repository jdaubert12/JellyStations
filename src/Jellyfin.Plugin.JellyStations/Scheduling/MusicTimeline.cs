using Jellyfin.Plugin.JellyStations.Models;

namespace Jellyfin.Plugin.JellyStations.Scheduling;

/// <summary>One stretch of one track: where in the file it starts and how long it plays.</summary>
public sealed record MusicPiece(MusicTrack Track, TimeSpan InPoint, TimeSpan Duration, DateTime StartUtc)
{
    public DateTime EndUtc => StartUtc + Duration;
}

/// <summary>
/// The music under a weather channel as a deterministic timeline: rounds of a seeded shuffle play back to back
/// from the anchor, so any instant maps to one track at one offset and every ffmpeg segment can seek into the
/// right place. The same seed scheme as the program schedulers (station, revision, round), so a saved station
/// plays the same music at the same time until it is edited.
/// </summary>
public sealed class MusicTimeline
{
    private const string Scope = "music";

    private readonly IReadOnlyList<MusicTrack> _tracks;
    private readonly long[] _ticks;
    private readonly long _roundTicks;
    private readonly DateTime _anchorUtc;
    private readonly Guid _stationId;
    private readonly int _revision;
    private readonly Dictionary<long, int[]> _rounds = new();

    public MusicTimeline(IReadOnlyList<MusicTrack> tracks, DateTime anchorUtc, Guid stationId, int revision)
    {
        _tracks = tracks.Where(t => t.RuntimeTicks > 0 && !string.IsNullOrEmpty(t.Path)).ToList();
        _ticks = _tracks.Select(t => t.RuntimeTicks).ToArray();
        _roundTicks = _ticks.Sum();
        _anchorUtc = DateTime.SpecifyKind(anchorUtc, DateTimeKind.Utc);
        _stationId = stationId;
        _revision = revision;
    }

    public bool IsEmpty => _tracks.Count == 0 || _roundTicks <= 0;

    public int Count => _tracks.Count;

    public TimeSpan RoundLength => TimeSpan.FromTicks(_roundTicks);

    /// <summary>The track playing at an instant and how far into it we are; null when there is no music.</summary>
    public MusicPiece? At(DateTime utc)
    {
        if (IsEmpty)
        {
            return null;
        }

        var elapsed = utc.Ticks - _anchorUtc.Ticks;
        var round = Padding.FloorDiv(elapsed, _roundTicks);
        var inRound = elapsed - (round * _roundTicks);
        var order = Order(round);
        long pos = 0;
        foreach (var idx in order)
        {
            var len = _ticks[idx];
            if (inRound < pos + len)
            {
                var start = new DateTime(_anchorUtc.Ticks + (round * _roundTicks) + pos, DateTimeKind.Utc);
                return new MusicPiece(_tracks[idx], TimeSpan.FromTicks(inRound - pos), TimeSpan.FromTicks(len - (inRound - pos)), start + TimeSpan.FromTicks(inRound - pos));
            }

            pos += len;
        }

        throw new InvalidOperationException("unreachable");
    }

    /// <summary>The pieces that play from <paramref name="fromUtc"/> to <paramref name="toUtc"/>, in order, each cut to the window.</summary>
    public IReadOnlyList<MusicPiece> Between(DateTime fromUtc, DateTime toUtc)
    {
        var pieces = new List<MusicPiece>();
        if (IsEmpty || toUtc <= fromUtc)
        {
            return pieces;
        }

        var cursor = fromUtc;
        while (cursor < toUtc)
        {
            var piece = At(cursor)!;
            var end = piece.EndUtc < toUtc ? piece.EndUtc : toUtc;
            pieces.Add(piece with { Duration = end - cursor, StartUtc = cursor });
            cursor = end;
        }

        return pieces;
    }

    /// <summary>The play order of one round: a seeded shuffle that never starts with the track the previous round ended on.</summary>
    public int[] Order(long round)
    {
        lock (_rounds)
        {
            if (_rounds.TryGetValue(round, out var cached))
            {
                return cached;
            }

            var perm = SplitMix64.Permutation(_tracks.Count, StableHash.Seed(_stationId, _revision, Scope, round));
            if (_tracks.Count > 1)
            {
                var previousLast = SplitMix64.Permutation(_tracks.Count, StableHash.Seed(_stationId, _revision, Scope, round - 1));
                if (perm[0] == previousLast[^1])
                {
                    (perm[0], perm[1]) = (perm[1], perm[0]);
                }
            }

            if (_rounds.Count > 64)
            {
                _rounds.Clear();
            }

            _rounds[round] = perm;
            return perm;
        }
    }
}
