using System.Globalization;
using System.IO.Compression;

namespace ParkedTraffic;

/// <summary>
/// Callsign -> route table from Virtual Radar Server "standing data" (CC0, github.com/vradarserver/standing-data).
/// Downloaded once, cached locally, refreshed weekly. Only the routes and airports folders are kept.
/// </summary>
public sealed class RouteDb
{
    private const string ZipUrl = "https://codeload.github.com/vradarserver/standing-data/zip/refs/heads/main";
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParkedTraffic", "standing-data");

    public sealed record Route(string Callsign, string Airline, string[] Airports);
    public sealed record AirportInfo(string Icao, double Lat, double Lon, string Country);

    /// <summary>One airline's presence at an airport, from the route table.</summary>
    public sealed record AirlineAtAirport(string Airline, int FlightNumbers, double MedianRouteKm);

    private readonly Dictionary<string, Route> _byCallsign = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AirportInfo> _airports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Route>> _byAirport = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Routes that touch this airport (indexed at load, so no full-table scans).</summary>
    private IEnumerable<Route> RoutesAt(string icao) => _byAirport.TryGetValue(icao, out var l) ? l : [];

    public int RouteCount => _byCallsign.Count;

    public static async Task<RouteDb> LoadAsync(HttpClient http)
    {
        await EnsureDownloadedAsync(http);
        var db = new RouteDb();
        db.LoadAirports(Path.Combine(CacheDir, "airports"));
        db.LoadRoutes(Path.Combine(CacheDir, "routes"));
        db.LoadAirlines(Path.Combine(CacheDir, "airlines"));
        db.ComputeHomeCountries();
        return db;
    }

    private readonly Dictionary<string, string> _airlineNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _homeCountry = new(StringComparer.OrdinalIgnoreCase);

    public string AirlineName(string icao) => _airlineNames.TryGetValue(icao, out var n) ? n : icao;

    /// <summary>Country (ISO2) most of this airline's route endpoints are in; "" if unknown.</summary>
    public string HomeCountry(string icao) => _homeCountry.TryGetValue(icao, out var c) ? c : "";

    private void LoadAirlines(string dir)
    {
        if (!Directory.Exists(dir)) return;
        // Code,Name,ICAO,IATA,PositioningFlightPattern,CharterFlightPattern
        foreach (var file in Directory.EnumerateFiles(dir, "*.csv"))
            foreach (var line in File.ReadLines(file).Skip(1))
            {
                var c = SplitCsv(line);
                if (c.Count >= 3 && c[2].Length == 3) _airlineNames.TryAdd(c[2], c[1]);
            }
    }

    private void ComputeHomeCountries()
    {
        foreach (var g in _byCallsign.Values.GroupBy(r => r.Airline, StringComparer.OrdinalIgnoreCase))
        {
            var top = g.SelectMany(r => r.Airports).Select(a => Airport(a)?.Country).Where(c => !string.IsNullOrEmpty(c))
                       .GroupBy(c => c).OrderByDescending(x => x.Count()).FirstOrDefault();
            if (top is not null) _homeCountry[g.Key] = top.Key!;
        }
    }

    private static async Task EnsureDownloadedAsync(HttpClient http)
    {
        var stamp = Path.Combine(CacheDir, "downloaded.txt");
        bool hasAirlines = Directory.Exists(Path.Combine(CacheDir, "airlines"));
        if (hasAirlines && File.Exists(stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < MaxAge) return;

        Log.Write("Downloading route database (VRS standing data, CC0)...");
        var tmp = Path.Combine(Path.GetTempPath(), $"standing-data-{Guid.NewGuid():N}.zip");
        try
        {
            using (var resp = await http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs);
            }

            var staging = CacheDir + ".new";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            using (var zip = ZipFile.OpenRead(tmp))
            {
                foreach (var e in zip.Entries)
                {
                    // Entries look like "standing-data-main/routes/schema-01/S/SAA-all.csv"
                    var parts = e.FullName.Split('/');
                    if (parts.Length < 4 || e.Name.Length == 0 || !e.Name.EndsWith(".csv")) continue;
                    if (parts[1] is not ("routes" or "airports" or "airlines") || parts[2] != "schema-01") continue;
                    var dest = Path.Combine(staging, parts[1], e.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, overwrite: true);
                }
            }
            if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, true);
            Directory.Move(staging, CacheDir);
            File.WriteAllText(stamp, DateTime.UtcNow.ToString("O"));
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private void LoadAirports(string dir)
    {
        // Code,Name,ICAO,IATA,Location,CountryISO2,Latitude,Longitude,AltitudeFeet
        foreach (var file in Directory.EnumerateFiles(dir, "*.csv"))
            foreach (var line in File.ReadLines(file).Skip(1))
            {
                var c = SplitCsv(line);
                if (c.Count < 8 || c[2].Length != 4) continue;
                if (!double.TryParse(c[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) continue;
                if (!double.TryParse(c[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
                _airports.TryAdd(c[2], new AirportInfo(c[2], lat, lon, c[5]));
            }
    }

    private void LoadRoutes(string dir)
    {
        // Callsign,Code,Number,AirlineCode,AirportCodes
        foreach (var file in Directory.EnumerateFiles(dir, "*.csv"))
            foreach (var line in File.ReadLines(file).Skip(1))
            {
                var c = line.Split(',');
                if (c.Length < 5 || c[4].Length < 9) continue;
                var route = new Route(c[0], c[3], c[4].Trim().Split('-'));
                if (!_byCallsign.TryAdd(c[0], route)) continue;
                foreach (var a in route.Airports.Distinct())
                {
                    if (!_byAirport.TryGetValue(a, out var list)) _byAirport[a] = list = new();
                    list.Add(route);
                }
            }
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char ch in line)
        {
            if (ch == '"') quoted = !quoted;
            else if (ch == ',' && !quoted) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }

    public Route? Lookup(string callsign) => _byCallsign.TryGetValue(callsign.Trim(), out var r) ? r : null;

    public AirportInfo? Airport(string icao) => _airports.TryGetValue(icao, out var a) ? a : null;

    /// <summary>
    /// Airports near a position, closest first (within maxKm). Airports with airline routes come before those without,
    /// so you get the airport you're parked at, not a nearby heliport or strip.
    /// </summary>
    public List<(AirportInfo airport, double km)> Nearest(double lat, double lon, double maxKm = 15)
    {
        return _airports.Values
            .Select(a => (airport: a, km: Sim.DistanceMeters(lat, lon, a.Lat, a.Lon) / 1000))
            .Where(x => x.km <= maxKm)
            .OrderByDescending(x => _byAirport.ContainsKey(x.airport.Icao))
            .ThenBy(x => x.km)
            .ToList();
    }

    /// <summary>Airlines with routes touching this airport, with the number of distinct flight numbers and a typical route length.</summary>
    public List<AirlineAtAirport> AirlinesAt(string icao)
    {
        var here = Airport(icao);
        return RoutesAt(icao)
            .GroupBy(r => r.Airline, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var km = g.SelectMany(r => r.Airports)
                          .Where(a => !a.Equals(icao, StringComparison.OrdinalIgnoreCase))
                          .Select(Airport).Where(a => a is not null && here is not null)
                          .Select(a => Sim.DistanceMeters(here!.Lat, here.Lon, a!.Lat, a.Lon) / 1000)
                          .OrderBy(d => d).ToList();
                return new AirlineAtAirport(g.Key.ToUpperInvariant(), g.Count(), km.Count > 0 ? km[km.Count / 2] : 0);
            })
            .OrderByDescending(a => a.FlightNumbers)
            .ToList();
    }

    /// <summary>This airline's flight numbers touching this airport, as "CALLSIGN ROUTE" (e.g. "BAW55 EGLL-FAOR").</summary>
    public List<string> FlightsAt(string icao, string airline) =>
        RoutesAt(icao)
            .Where(r => r.Airline.Equals(airline, StringComparison.OrdinalIgnoreCase))
            .Select(r => $"{r.Callsign} {string.Join("-", r.Airports)}")
            .ToList();

    /// <summary>Longest leg (km) of a specific route that touches this airport.</summary>
    public double LegKm(Route r, string icao)
    {
        var here = Airport(icao);
        if (here is null) return 0;
        return r.Airports.Where(a => !a.Equals(icao, StringComparison.OrdinalIgnoreCase))
                         .Select(Airport).Where(a => a is not null)
                         .Select(a => Sim.DistanceMeters(here.Lat, here.Lon, a!.Lat, a.Lon) / 1000)
                         .DefaultIfEmpty(0).Max();
    }
}
