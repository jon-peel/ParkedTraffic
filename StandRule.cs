using System.Text.RegularExpressions;

namespace ParkedTraffic;

/// <summary>Any = any passenger traffic (domestic or international). Cargo = freighters only.</summary>
public enum Traffic { Any, Domestic, International, Cargo }

/// <summary>
/// Rules for one apron (e.g. "C") or one stand (e.g. "Gate C 5"). Stand rules override apron rules field by field;
/// null / empty means "inherit".
/// </summary>
public sealed class StandRule
{
    /// <summary>Any / Domestic / International. Null = inherit.</summary>
    public Traffic? Traffic { get; set; }

    /// <summary>If not empty, only these airlines (ICAO) may park here.</summary>
    public List<string> Airlines { get; set; } = [];

    /// <summary>Remembered airline-type options, e.g. "SAA-A320". When set, auto-fill picks from these.</summary>
    public List<string> Options { get; set; } = [];

    /// <summary>Move the aircraft forward (+) / back (-) along the stand heading, metres.</summary>
    public double? Nudge { get; set; }

    /// <summary>Never auto-fill this stand/apron.</summary>
    public bool? Disabled { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Traffic is null && Airlines.Count == 0 && Options.Count == 0 && Nudge is null && Disabled is null;

    /// <summary>Apron key for a stand: "Gate C 5" -> "C", "Gate 12" -> "Gate", "N Parking 3" -> "N Parking".</summary>
    public static string ApronOf(Stand s)
    {
        var m = Regex.Match(s.Name, @"^Gate ([A-Z]) ");
        if (m.Success) return m.Groups[1].Value;
        var baseName = Regex.Replace(s.Name, @"\s*\d+[A-Z]?$", "").Trim();
        return baseName; // "" for stands without a name prefix (shown blank)
    }

    /// <summary>Effective rule: apron rule with the stand rule layered on top.</summary>
    public static StandRule Effective(AirportConfig config, Stand s)
    {
        config.Aprons.TryGetValue(ApronOf(s), out var apron);
        config.Stands.TryGetValue(s.Name, out var stand);
        return new StandRule
        {
            // Stands the scenery marks as cargo default to Cargo unless a rule says otherwise.
            Traffic = stand?.Traffic ?? apron?.Traffic ?? (IsSceneryCargo(s) ? ParkedTraffic.Traffic.Cargo : null),
            Airlines = stand?.Airlines.Count > 0 ? stand.Airlines : apron?.Airlines ?? [],
            Options = stand?.Options.Count > 0 ? stand.Options : apron?.Options ?? [],
            Nudge = stand?.Nudge ?? apron?.Nudge,
            Disabled = stand?.Disabled ?? apron?.Disabled,
        };
    }

    /// <summary>Scenery parking type Cargo (5) or Mil cargo (6).</summary>
    public static bool IsSceneryCargo(Stand s) => s.TypeCode is 5 or 6;

    /// <summary>True when this rule wants freighters.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool WantsCargo => Traffic == ParkedTraffic.Traffic.Cargo;

    /// <summary>Short text for the stand table.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Disabled == true) parts.Add("OFF");
        if (Traffic is { } t && t != ParkedTraffic.Traffic.Any)
            parts.Add(t switch { ParkedTraffic.Traffic.Domestic => "dom", ParkedTraffic.Traffic.International => "intl", _ => "cargo" });
        if (Airlines.Count > 0) parts.Add(string.Join(",", Airlines));
        if (Options.Count > 0) parts.Add("[" + string.Join(",", Options) + "]");
        if (Nudge is { } n && n != 0) parts.Add($"{n:+0.#;-0.#}m");
        return string.Join(" ", parts);
    }

    /// <summary>Parses "SAA-A320" into airline + type.</summary>
    public static (string airline, string type)? ParseOption(string text)
    {
        var m = Regex.Match(text.Trim().ToUpperInvariant(), @"^([A-Z0-9]{3})[-\s/]+([A-Z0-9]{2,4})$");
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
    }
}
