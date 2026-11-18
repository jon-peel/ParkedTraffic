namespace ParkedTraffic;

/// <summary>
/// Decides which airline + type goes on a stand.
///  * Airlines come from the route table (who flies to/from this airport) and from live flights to/from it right now.
///  * Frequent airlines (large share of the airport's flight numbers) may fill many stands; everyone else gets at most
///    <see cref="AirportConfig.InfrequentCap"/> aircraft on the apron at once.
///  * Types: what that airline is flying here right now if seen live; otherwise catalog types that fit the
///    airline's typical route length (long haul -> widebodies, short haul -> narrowbodies/regionals).
///  * Stand/apron rules: remembered options, allowed airlines, domestic/international.
/// </summary>
public sealed class MixPlanner
{
    public sealed record Candidate(string Airline, string Name, string HomeCountry, double Weight, bool Frequent, bool SeenLive,
                                   double MedianRouteKm, HashSet<string> LiveTypes);

    private readonly List<Candidate> _candidates;
    private readonly List<Model> _catalog;
    private readonly AirportConfig _config;
    private readonly string _airportCountry;
    private readonly Random _rng;

    // For showing a flight number next to each parked aircraft.
    private string _icao = "";
    private RouteDb? _routes;
    private Dictionary<string, List<(string callsign, string type, string route)>> _liveFlights = new();

    public IReadOnlyList<Candidate> Candidates => _candidates;
    public string Source { get; }

    private MixPlanner(List<Candidate> candidates, List<Model> catalog, AirportConfig config, string airportCountry, Random rng, string source)
    {
        _candidates = candidates; _catalog = catalog; _config = config; _airportCountry = airportCountry; _rng = rng; Source = source;
    }

    /// <summary>A live flight to/from this airport, as seen on ADS-B.</summary>
    public sealed record LiveFlight(string Airline, string Callsign, string Type, string Route);

    /// <summary>All live flights to/from this airport from the last lookup.</summary>
    public List<LiveFlight> LiveFlights =>
        _liveFlights.SelectMany(kv => kv.Value.Select(f => new LiveFlight(kv.Key, f.callsign, f.type, f.route))).ToList();

    /// <summary>
    /// A model for this live flight on this stand (its airline and actual type), or null when it can't go there:
    /// stand rule, cap, size, jet bridge or cargo/passenger mismatch.
    /// </summary>
    public Model? PickLive(LiveFlight f, Stand stand, StandRule rule, IReadOnlyDictionary<string, int> parkedPerAirline)
    {
        if (rule.Options.Count > 0) return null; // stands with fixed options keep their options
        bool cargo = rule.WantsCargo;
        bool frequent = _candidates.FirstOrDefault(c => c.Airline == f.Airline)?.Frequent ?? false;
        parkedPerAirline.TryGetValue(CapKey(f.Airline, cargo), out var parked);
        if (!((frequent && !cargo) || parked < _config.InfrequentCap) || !Allowed(f.Airline, rule)) return null;

        var models = _catalog.Where(m => m.Airline == f.Airline && m.Type == f.Type && m.IsCargo == cargo && Fits(m, stand) &&
                                         !(stand.HasJetway && m.IsProp)).ToList();
        return models.Count == 0 ? null : models[_rng.Next(models.Count)];
    }

    /// <summary>
    /// A real flight for this airline: a live one flying this type if seen, otherwise one of its flight numbers at this
    /// airport from the route table. Display only.
    /// </summary>
    public string FlightFor(Model m, IReadOnlySet<string> usedCallsigns)
    {
        if (_liveFlights.TryGetValue(m.Airline, out var live))
        {
            var same = live.Where(f => f.type == m.Type && !usedCallsigns.Contains(f.callsign)).ToList();
            if (same.Count > 0) { var f = same[_rng.Next(same.Count)]; return $"{f.callsign} {f.route} (live)"; }
        }
        // Route-table flights are "CALLSIGN ROUTE"; skip callsigns already on the apron.
        var flights = (_routes?.FlightsAt(_icao, m.Airline) ?? []).Where(f => !usedCallsigns.Contains(f.Split(' ')[0])).ToList();
        return flights.Count > 0 ? flights[_rng.Next(flights.Count)] : "";
    }

    public static MixPlanner Build(string icao, RouteDb? routes, List<LiveTraffic.Contact> live, List<Model> catalog, AirportConfig config,
                                   GlobalConfig global, Random rng)
    {
        // Excluded airlines (defunct everywhere, or excluded at this airport) simply have no models as far as the mix is concerned.
        var excluded = global.DefunctCodes;
        excluded.UnionWith(config.ExcludeAirlines.Select(a => a.ToUpperInvariant()));
        catalog = catalog.Where(m => !excluded.Contains(m.Airline)).ToList();
        var catalogAirlines = catalog.Select(m => m.Airline).ToHashSet();
        string airportCountry = routes?.Airport(icao)?.Country ?? "";

        // Live flights whose route touches this airport -> airline + type actually flown.
        var liveTypes = new Dictionary<string, HashSet<string>>();
        var liveFlights = new Dictionary<string, List<(string, string, string)>>();
        if (routes is not null)
            foreach (var c in live)
            {
                var r = routes.Lookup(c.Callsign);
                if (r is null || !r.Airports.Contains(icao, StringComparer.OrdinalIgnoreCase)) continue;
                var airline = r.Airline.ToUpperInvariant();
                if (!liveTypes.TryGetValue(airline, out var set)) liveTypes[airline] = set = new();
                set.Add(c.Type);
                if (!liveFlights.TryGetValue(airline, out var fl)) liveFlights[airline] = fl = new();
                fl.Add((c.Callsign, c.Type, string.Join("-", r.Airports)));
            }

        var atAirport = routes?.AirlinesAt(icao).Where(a => catalogAirlines.Contains(a.Airline)).ToList() ?? [];
        if (atAirport.Count >= 3)
        {
            double total = atAirport.Sum(a => a.FlightNumbers);
            var candidates = atAirport.Select(a =>
            {
                bool frequent = a.FlightNumbers / total >= config.FrequentShare;
                bool seen = liveTypes.TryGetValue(a.Airline, out var types);
                double weight = a.FlightNumbers * (seen ? config.LiveBoost : 1);
                return new Candidate(a.Airline, routes!.AirlineName(a.Airline), routes.HomeCountry(a.Airline), weight, frequent, seen,
                                     a.MedianRouteKm, types ?? []);
            }).ToList();
            return new MixPlanner(candidates, catalog, config, airportCountry, rng, "route table" + (liveTypes.Count > 0 ? " + live flights" : ""))
                { _icao = icao, _routes = routes, _liveFlights = liveFlights };
        }

        // Fallback: hand-written mix from config/<ICAO>.json.
        var manual = config.Mix.Where(w => w.Weight > 0 && catalogAirlines.Contains(w.Airline.ToUpperInvariant()))
            .Select(w => new Candidate(w.Airline.ToUpperInvariant(), w.Note ?? w.Airline, routes?.HomeCountry(w.Airline) ?? "", w.Weight,
                                       w.Weight >= 10, false, 0, [])).ToList();
        return new MixPlanner(manual, catalog, config, airportCountry, rng, "config mix (no route data for this airport)")
            { _icao = icao, _routes = routes, _liveFlights = liveFlights };
    }

    /// <summary>Key for the per-airline cap; freighters are capped separately from passenger aircraft.</summary>
    public static string CapKey(string airline, bool cargo) => cargo ? airline + "/F" : airline;

    public bool Fits(Model m, Stand stand, bool strictSize = true) =>
        m.HalfSpan <= stand.Radius + 0.5 && (!strictSize || m.HalfSpan >= stand.Radius * _config.MinSizeRatio);

    /// <summary>Does an airline satisfy this stand's rule (allowed list, domestic/international)?</summary>
    public bool Allowed(string airline, StandRule rule)
    {
        if (rule.Airlines.Count > 0 && !rule.Airlines.Contains(airline, StringComparer.OrdinalIgnoreCase)) return false;
        if (rule.Traffic is null or Traffic.Any or Traffic.Cargo || _airportCountry.Length == 0) return true;
        // Candidates first; airlines outside the candidate list (stand options, manual picks) via the route table.
        var home = _candidates.FirstOrDefault(c => c.Airline == airline)?.HomeCountry;
        if (string.IsNullOrEmpty(home)) home = _routes?.HomeCountry(airline) ?? "";
        if (home.Length == 0) return true; // unknown home country: don't block it
        bool domestic = home == _airportCountry;
        return rule.Traffic == Traffic.Domestic ? domestic : !domestic;
    }

    /// <summary>Picks a model for this stand, respecting its rule and the per-airline caps given what is already parked.</summary>
    public Model? Pick(Stand stand, StandRule rule, IReadOnlyDictionary<string, int> parkedPerAirline)
    {
        bool cargo = rule.WantsCargo;

        bool CapOk(string airline, bool frequent)
        {
            parkedPerAirline.TryGetValue(CapKey(airline, cargo), out var parked);
            return (frequent && !cargo) || parked < _config.InfrequentCap;
        }

        // 1) Remembered options for this stand/apron win.
        if (rule.Options.Count > 0)
        {
            var opts = rule.Options.Select(StandRule.ParseOption).Where(o => o is not null).Select(o => o!.Value)
                .Where(o => CapOk(o.airline, _candidates.FirstOrDefault(c => c.Airline == o.airline)?.Frequent ?? false))
                .Select(o => PreferKind(_catalog.Where(m => m.Airline == o.airline && m.Type == o.type && Fits(m, stand, strictSize: false)).ToList(), cargo))
                .Where(l => l.Count > 0).ToList();
            if (opts.Count > 0) { var l = opts[_rng.Next(opts.Count)]; return l[_rng.Next(l.Count)]; }
        }

        // 2) Weighted pick among allowed airlines.
        var options = new List<(Candidate c, List<Model> models)>();
        foreach (var c in _candidates)
        {
            if (!CapOk(c.Airline, c.Frequent) || !Allowed(c.Airline, rule)) continue;

            // Cargo stands: freighters only. Every other stand: passenger aircraft only.
            // Jet-bridge stands: no turboprops/pistons (their doors don't reach a jet bridge).
            var models = _catalog.Where(m => m.Airline == c.Airline && m.IsCargo == cargo && Fits(m, stand) &&
                                             !(stand.HasJetway && m.IsProp)).ToList();
            if (models.Count == 0) continue;

            // Prefer the types this airline is flying here right now.
            var live = models.Where(m => c.LiveTypes.Contains(m.Type)).ToList();
            if (live.Count > 0) models = live;
            else
            {
                // Otherwise match the airline's typical route length here.
                var byRange = models.Where(m => RangeFits(m, c.MedianRouteKm)).ToList();
                if (byRange.Count > 0) models = byRange; else if (c.MedianRouteKm > 0) continue;
            }
            options.Add((c, models));
        }
        if (options.Count == 0) return null;

        // Airline: its weight times how snugly its best-fitting aircraft fills this stand.
        var weighted = options.Select(o => (o.c, o.models, w: o.c.Weight * o.models.Max(m => Snug(m, stand)))).ToList();
        var chosen = WeightedPick(weighted, x => x.w);

        // Type: weighted by snug fit (an A320 beats an ATR on a 22 m gate), then a random livery of that type.
        var types = chosen.models.GroupBy(m => m.Type).Select(g => (type: g.Key, w: g.Max(m => Snug(m, stand)))).ToList();
        string type = WeightedPick(types, t => t.w).type;
        var liveries = chosen.models.Where(m => m.Type == type).ToList();
        return liveries[_rng.Next(liveries.Count)];
    }

    /// <summary>
    /// How well an aircraft fills a stand: 1 when the half-span equals the stand radius, falling off exponentially as it
    /// gets smaller. Strength 12: on a 22 m stand an A320 (72%) is ~3.7x as likely as an ATR 72 (61%).
    /// </summary>
    public double Snug(Model m, Stand stand)
    {
        double fill = Math.Min(1, m.HalfSpan / Math.Max(1, stand.Radius));
        return Math.Exp(-_config.SnugFitStrength * (1 - fill));
    }

    private T WeightedPick<T>(List<T> items, Func<T, double> weight)
    {
        double roll = _rng.NextDouble() * items.Sum(weight);
        foreach (var i in items) { roll -= weight(i); if (roll <= 0) return i; }
        return items[^1];
    }

    /// <summary>A specific airline + type for a stand (manual add from the TUI). Size limit is the stand radius only.</summary>
    public Model? PickExact(Stand stand, StandRule rule, string airline, string type)
    {
        var l = PreferKind(_catalog.Where(m => m.Airline == airline && m.Type == type && Fits(m, stand, strictSize: false)).ToList(), rule.WantsCargo);
        return l.Count == 0 ? null : l[_rng.Next(l.Count)];
    }

    /// <summary>For explicit picks: freighter liveries on cargo stands, passenger liveries elsewhere, if both exist.</summary>
    private static List<Model> PreferKind(List<Model> models, bool cargo)
    {
        var matching = models.Where(m => m.IsCargo == cargo).ToList();
        return matching.Count > 0 ? matching : models;
    }

    /// <summary>Long haul -> widebody (span 40 m+), short haul -> narrowbody/regional, in between -> anything.</summary>
    private bool RangeFits(Model m, double routeKm)
    {
        if (routeKm <= 0) return true;
        bool wide = m.WingspanMeters >= 40;
        if (routeKm >= _config.LongHaulKm) return wide;
        if (routeKm <= _config.ShortHaulKm) return !wide;
        return true;
    }
}
