using System.Buffers.Binary;
using System.Text;

namespace Jellyfin.Plugin.TvStation.Scheduling;

/// <summary>Process- and platform-independent 64-bit FNV-1a hashing for seeds.</summary>
public static class StableHash
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Seed(Guid stationId, int revision, string scope, long periodIndex)
    {
        Span<byte> buf = stackalloc byte[16 + 4 + 8];
        stationId.TryWriteBytes(buf[..16]);
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(16, 4), revision);
        BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(20, 8), periodIndex);

        var h = Fnv1a(Offset, buf);
        h = Fnv1a(h, Encoding.UTF8.GetBytes(scope));
        return h;
    }

    public static ulong Fnv1a(ulong hash, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            hash ^= b;
            hash *= Prime;
        }

        return hash;
    }
}

/// <summary>SplitMix64 PRNG: tiny, fast, fully deterministic.</summary>
public sealed class SplitMix64
{
    private ulong _state;

    public SplitMix64(ulong seed)
    {
        _state = seed;
    }

    public ulong NextUInt64()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform integer in [0, maxExclusive).</summary>
    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 1)
        {
            return 0;
        }

        return (int)(NextUInt64() % (ulong)maxExclusive);
    }

    /// <summary>Returns a seeded Fisher-Yates permutation of indices 0..n-1.</summary>
    public static int[] Permutation(int n, ulong seed)
    {
        var perm = new int[n];
        for (var i = 0; i < n; i++)
        {
            perm[i] = i;
        }

        var rng = new SplitMix64(seed);
        for (var i = n - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }

        return perm;
    }
}
