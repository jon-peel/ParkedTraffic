using System.Text.Json;

namespace ParkedTraffic;

public sealed class DefunctAirline
{
    public string Airline { get; set; } = "";
    public string? Note { get; set; }
}

/// <summary>Settings shared by all airports, stored as config/global.json.</summary>
public sealed class GlobalConfig
{
    /// <summary>Airlines that no longer fly. The route table has no dates, so their old flight numbers still appear.</summary>
    public List<DefunctAirline> DefunctAirlines { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public HashSet<string> DefunctCodes => DefunctAirlines.Select(d => d.Airline.ToUpperInvariant()).ToHashSet();

    public static string PathFor => Path.Combine(AppContext.BaseDirectory, "config", "global.json"); // next to the exe

    public static GlobalConfig LoadOrCreate()
    {
        var path = PathFor;
        if (File.Exists(path))
            return JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJson.Default.GlobalConfig) ?? new();
        var cfg = Defaults();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, ConfigJson.Default.GlobalConfig));
        return cfg;
    }

    private static GlobalConfig Defaults() => new()
    {
        DefunctAirlines =
        [
            new() { Airline = "CAW", Note = "Comair (South Africa), ceased 2022" },
            new() { Airline = "MNO", Note = "Mango (South Africa), ceased 2021" },
            new() { Airline = "NMB", Note = "Air Namibia, ceased 2021" },
            new() { Airline = "ABB", Note = "Air Belgium, passenger flights ended 2023" },
            new() { Airline = "AZA", Note = "Alitalia, ceased 2021" },
            new() { Airline = "GWI", Note = "Germanwings, ceased 2020" },
            new() { Airline = "BEE", Note = "Flybe, ceased 2023" },
            new() { Airline = "JAI", Note = "Jet Airways, ceased 2019" },
            new() { Airline = "TCX", Note = "Thomas Cook Airlines, ceased 2019" },
            new() { Airline = "BER", Note = "Air Berlin, ceased 2017" },
            new() { Airline = "MON", Note = "Monarch, ceased 2017" },
            new() { Airline = "WOW", Note = "WOW air, ceased 2019" },
            new() { Airline = "VRD", Note = "Virgin America, merged into Alaska 2018" },
        ],
    };
}
