using System.Globalization;

namespace Jellyfin.Plugin.JellyStations.Library;

/// <summary>
/// Language codes as they appear on media streams: ISO 639-2 three-letter codes, in either the bibliographic
/// ("ger", "fre", "chi") or terminology ("deu", "fra", "zho") spelling, or two-letter ISO 639-1 codes. All compare
/// equal when they name the same language, and each has an English name for the editor.
/// </summary>
public static class LanguageNames
{
    private static readonly Dictionary<string, string> Bibliographic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["chi"] = "zho", ["cze"] = "ces", ["dut"] = "nld",
        ["fre"] = "fra", ["geo"] = "kat", ["ger"] = "deu", ["gre"] = "ell", ["ice"] = "isl", ["mac"] = "mkd", ["mao"] = "mri",
        ["may"] = "msa", ["per"] = "fas", ["rum"] = "ron", ["slo"] = "slk", ["tib"] = "bod", ["wel"] = "cym"
    };

    private static readonly Lazy<Dictionary<string, string>> EnglishNames = new(() =>
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            var code = culture.ThreeLetterISOLanguageName;
            if (code.Length == 3 && !names.ContainsKey(code))
            {
                names[code] = culture.EnglishName;
            }
        }

        return names;
    });

    /// <summary>The terminology three-letter code, lower case ("ja" and "jpn" both give "jpn", "ger" gives "deu"); empty for no code.</summary>
    public static string Canonical(string? code)
    {
        var c = (code ?? string.Empty).Trim().ToLowerInvariant();
        if (c.Length == 2)
        {
            try
            {
                c = CultureInfo.GetCultureInfo(c).ThreeLetterISOLanguageName.ToLowerInvariant();
            }
            catch (CultureNotFoundException)
            {
            }
        }

        return Bibliographic.TryGetValue(c, out var terminology) ? terminology : c;
    }

    /// <summary>Whether two codes name the same language; false when either is missing.</summary>
    public static bool Same(string? a, string? b)
    {
        var ca = Canonical(a);
        return ca.Length > 0 && ca == Canonical(b);
    }

    /// <summary>"Japanese" for "jpn"; the code itself, upper case, when .NET has no name for it.</summary>
    public static string Describe(string? code)
    {
        var c = Canonical(code);
        if (c.Length == 0 || c == "und")
        {
            return "Unknown";
        }

        return EnglishNames.Value.TryGetValue(c, out var name) ? name : c.ToUpperInvariant();
    }
}
