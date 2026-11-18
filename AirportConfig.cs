using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParkedTraffic;

public sealed class AirlineWeight
{
    public string Airline { get; set; } = "";
    public double Weight { get; set; } = 1;
    public string? Note { get; set; }
}

/// <summary>Per-airport settings, stored as config/&lt;ICAO&gt;.json so they can be edited by hand (and later by the TUI).</summary>
public sealed class AirportConfig
{
    /// <summary>Share of free, eligible stands to fill (0-100). Used when <see cref="FillCount"/> is not set.</summary>
    public int FillPercent { get; set; } = 70;

    /// <summary>Fixed number of parked aircraft. When set, it is the quota instead of <see cref="FillPercent"/>.</summary>
    public int? FillCount { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string QuotaText => FillCount is { } n ? $"{n}" : $"{FillPercent}%";

    /// <summary>Stand types that may be filled. Names as shown by the "stands" command.</summary>
    public List<string> StandTypes { get; set; } = ["Gate S", "Gate M", "Gate H", "Gate XL", "Cargo", "Ramp GA M", "Ramp GA L"];

    /// <summary>A model must not be smaller than this fraction of the stand radius (keeps E-jets off A380 stands).</summary>
    public double MinSizeRatio { get; set; } = 0.5;

    /// <summary>How strongly aircraft that fill a stand well are preferred (0 = no preference, 12 = strong).</summary>
    public double SnugFitStrength { get; set; } = 12;

    /// <summary>An airline with at least this share of the airport's flight numbers may fill many stands.</summary>
    public double FrequentShare { get; set; } = 0.10;

    /// <summary>Max parked aircraft at once for every other airline.</summary>
    public int InfrequentCap { get; set; } = 1;

    /// <summary>Weight multiplier for airlines seen flying to/from the airport right now.</summary>
    public double LiveBoost { get; set; } = 2;

    /// <summary>Typical route length at/above which only widebodies are used (km).</summary>
    public double LongHaulKm { get; set; } = 4000;

    /// <summary>Typical route length at/below which only narrowbodies/regionals are used (km).</summary>
    public double ShortHaulKm { get; set; } = 2500;

    /// <summary>Radius for the live traffic lookup (nm).</summary>
    public int LiveRadiusNm { get; set; } = 120;

    /// <summary>Airlines never parked at this airport (stale routes, wrong callsigns, ...). ICAO codes.</summary>
    public List<string> ExcludeAirlines { get; set; } = [];

    /// <summary>Rules per apron, keyed by apron (e.g. "C" for Gate C stands).</summary>
    public Dictionary<string, StandRule> Aprons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rules per stand, keyed by stand name (e.g. "Gate C 5"). Override the apron rule.</summary>
    public Dictionary<string, StandRule> Stands { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Writes this config back to config/&lt;ICAO&gt;.json (empty rules are dropped).</summary>
    public void Save(string icao)
    {
        foreach (var k in Aprons.Where(kv => kv.Value.IsEmpty).Select(kv => kv.Key).ToList()) Aprons.Remove(k);
        foreach (var k in Stands.Where(kv => kv.Value.IsEmpty).Select(kv => kv.Key).ToList()) Stands.Remove(k);
        var path = PathFor(icao);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, ConfigJson.Default.AirportConfig));
    }

    /// <summary>Fallback weighted airline mix, used only when the route table knows fewer than 3 airlines here.</summary>
    public List<AirlineWeight> Mix { get; set; } = [];

    public static string PathFor(string icao) => Path.Combine(AppContext.BaseDirectory, "config", $"{icao.ToUpperInvariant()}.json");

    /// <summary>Loads config/&lt;ICAO&gt;.json; creates it from the built-in defaults on first use.</summary>
    public static AirportConfig LoadOrCreate(string icao, out string path)
    {
        path = PathFor(icao);
        if (File.Exists(path))
        {
            var loaded = JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJson.Default.AirportConfig) ?? new();
            // Keep lookups case-insensitive after deserialising.
            loaded.Aprons = new(loaded.Aprons, StringComparer.OrdinalIgnoreCase);
            loaded.Stands = new(loaded.Stands, StringComparer.OrdinalIgnoreCase);
            return loaded;
        }
        var cfg = Defaults(icao);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, ConfigJson.Default.AirportConfig));
        return cfg;
    }

    private static AirportConfig Defaults(string icao) => icao.ToUpperInvariant() switch
    {
        "FAOR" => new AirportConfig
        {
            ExcludeAirlines = ["CFG"], // Condor: old seasonal route, no longer flown
            Mix =
            [
                // Domestic / home carriers
                new() { Airline = "SAA", Weight = 16, Note = "South African Airways" },
                new() { Airline = "SFR", Weight = 18, Note = "FlySafair" },
                new() { Airline = "LNK", Weight = 12, Note = "Airlink" },
                new() { Airline = "GBB", Weight = 5, Note = "LIFT" },
                new() { Airline = "KEM", Weight = 3, Note = "CemAir" },
                // Regional Africa
                new() { Airline = "ETH", Weight = 4, Note = "Ethiopian" },
                new() { Airline = "KQA", Weight = 3, Note = "Kenya Airways" },
                new() { Airline = "RWD", Weight = 2, Note = "RwandAir" },
                new() { Airline = "DTA", Weight = 2, Note = "TAAG Angola" },
                new() { Airline = "BOT", Weight = 1, Note = "Air Botswana" },
                new() { Airline = "MAU", Weight = 2, Note = "Air Mauritius" },
                // Long haul
                new() { Airline = "BAW", Weight = 3, Note = "British Airways" },
                new() { Airline = "VIR", Weight = 2, Note = "Virgin Atlantic" },
                new() { Airline = "DLH", Weight = 2, Note = "Lufthansa" },
                new() { Airline = "AFR", Weight = 2, Note = "Air France" },
                new() { Airline = "KLM", Weight = 2, Note = "KLM" },
                new() { Airline = "SWR", Weight = 1, Note = "Swiss" },
                new() { Airline = "THY", Weight = 2, Note = "Turkish" },
                new() { Airline = "QTR", Weight = 4, Note = "Qatar" },
                new() { Airline = "UAE", Weight = 4, Note = "Emirates" },
                new() { Airline = "ETD", Weight = 2, Note = "Etihad" },
                new() { Airline = "SIA", Weight = 1, Note = "Singapore" },
                new() { Airline = "CPA", Weight = 1, Note = "Cathay Pacific" },
                new() { Airline = "QFA", Weight = 1, Note = "Qantas" },
                new() { Airline = "DAL", Weight = 1, Note = "Delta" },
                new() { Airline = "UAL", Weight = 1, Note = "United" },
            ],
        },
        _ => new AirportConfig(),
    };
}
