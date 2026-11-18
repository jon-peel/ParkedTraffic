namespace ParkedTraffic;

/// <summary>One of our parked aircraft.</summary>
public sealed class Placement(Stand stand, Model model, uint objectId, string flight)
{
    public Stand Stand { get; } = stand;
    public Model Model { get; } = model;
    public uint ObjectId { get; } = objectId;
    /// <summary>A real flight behind this pick, e.g. "BAW55 EGLL-FAOR" or "QTR21X OTHH-FAOR (live)".</summary>
    public string Flight { get; } = flight;

    /// <summary>"BAW55" from the flight, or "" when there is none (manual adds).</summary>
    public string Callsign => Flight.StartsWith('(') ? "" : Flight.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    public bool Live => Flight.EndsWith("(live)");

    /// <summary>Lights as last reported by the sim after we changed them ("" until then).</summary>
    public string Lights { get; set; } = "";
    public int ConflictHits { get; set; }
}

/// <summary>One row of the stand table, copied for the UI thread.</summary>
public sealed record StandRow(Stand Stand, string Apron, string Rule, string Aircraft, string State, string Callsign = "", string Lights = "");

/// <summary>
/// Keeps N parked FSLTL aircraft on free stands at one airport and keeps them out of everyone's way.
/// All methods except <see cref="Snapshot"/> must be called on the sim thread.
/// </summary>
/// <param name="shouldYield">Checked between aircraft during a fill; true = a command is waiting, so stop and let it run.</param>
public sealed class Filler(Sim sim, Airport airport, AirportConfig config, Func<MixPlanner> buildPlanner, Random? random = null,
                           Func<bool>? shouldYield = null)
{
    /// <summary>The last fill stopped early to let a command run; call TopUp(freshMix: false) to carry on.</summary>
    public bool Interrupted { get; private set; }

    private const uint ScanRadiusMeters = 6000;
    private readonly Random _rng = random ?? new Random();
    private readonly Dictionary<int, Placement> _placed = new();   // stand index -> placement
    private readonly HashSet<int> _held = new();                   // stands kept empty (user removed, or someone else arrived)
    private readonly Dictionary<int, string> _occupiedBy = new();  // stands with a foreign aircraft (last scan): its title
    private readonly Dictionary<int, string> _occupiedId = new();  // ... and its ATC ID (VATSIM callsign via vPilot)
    private readonly Dictionary<int, string> _occupiedLights = new(); // ... and its lights, e.g. "BT--"
    private readonly Dictionary<int, uint> _occupiedObject = new();   // ... and its sim object ID
    private readonly object _lock = new();
    private MixPlanner? _planner;
    private bool _callsignChecked;

    public int Target { get; set; }
    public int PlacedCount { get { lock (_lock) return _placed.Count; } }
    public string MixSource => _planner?.Source ?? "";
    public event Action? Changed;

    public bool IsEligible(Stand s) =>
        config.StandTypes.Contains(s.TypeName, StringComparer.OrdinalIgnoreCase) && StandRule.Effective(config, s).Disabled != true;

    /// <summary>Radius around a stand centre inside which another aircraft means "occupied".</summary>
    private static double OccupiedRadius(Stand s) => Math.Max(10, s.Radius * 0.8);

    /// <summary>Works out N from the free stands right now (call once at start-up).</summary>
    public void SetTarget()
    {
        var eligible = airport.Stands.Where(IsEligible).ToList();
        var others = ScanOthers();
        int free = eligible.Count(s => !_occupiedBy.ContainsKey(s.Index));
        Target = config.FillCount is { } n ? Math.Clamp(n, 0, free) : (int)Math.Round(free * config.FillPercent / 100.0);
        Log.Write($"{eligible.Count} eligible stands, {eligible.Count - free} occupied by other aircraft, {free} free. Target: {Target} parked (quota {config.QuotaText}).");
        Changed?.Invoke();
    }

    /// <summary>After a quota change: recompute the target, remove extra aircraft (never manual adds), or top up.</summary>
    public void ApplyQuota()
    {
        SetTarget();
        List<int> removable;
        lock (_lock)
            removable = _placed.Where(kv => kv.Value.Flight != "(manual)").Select(kv => kv.Key).OrderBy(_ => _rng.Next()).ToList();
        int extra = PlacedCount - Target, removed = 0;
        foreach (var idx in removable)
        {
            if (removed >= extra) break;
            Remove(idx, hold: false);
            removed++;
        }
        if (removed > 0) Log.Write($"Quota {config.QuotaText}: removed {removed} aircraft ({PlacedCount}/{Target}).");
        TopUp();
    }

    private MixPlanner Planner(bool fresh)
    {
        if (fresh || _planner is null)
        {
            _planner = buildPlanner();
            Log.Write($"Mix: {_planner.Source}, {_planner.Candidates.Count} airlines " +
                      $"({_planner.Candidates.Count(c => c.Frequent)} frequent, {_planner.Candidates.Count(c => c.SeenLive)} seen live).");
        }
        return _planner;
    }

    /// <summary>Callsigns of aircraft already parked, so a flight number is never used twice.</summary>
    private HashSet<string> UsedCallsigns()
    {
        lock (_lock) return _placed.Values.Select(p => p.Callsign).Where(c => c.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, int> CountsPerAirline()
    {
        // Freighters are counted separately ("ETH/F"), so an airline can have one passenger and one cargo aircraft.
        lock (_lock) return _placed.Values.GroupBy(p => MixPlanner.CapKey(p.Model.Airline, p.Model.IsCargo)).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>Tops the apron up to the target with a freshly built mix (fresh live lookup).</summary>
    /// <param name="freshMix">false = reuse the last mix (no new live lookup), e.g. when resuming an interrupted fill.</param>
    public int TopUp(bool freshMix = true)
    {
        Interrupted = false;
        int needed = Target - PlacedCount;
        if (needed <= 0) return 0;
        var planner = Planner(fresh: freshMix);
        bool Yield()
        {
            if (shouldYield?.Invoke() != true) return false;
            Interrupted = true;
            return true;
        }
        ScanOthers();

        List<Stand> free;
        lock (_lock)
            free = airport.Stands.Where(s => IsEligible(s) && !_placed.ContainsKey(s.Index) && !_held.Contains(s.Index) && !_occupiedBy.ContainsKey(s.Index))
                                 .OrderBy(_ => _rng.Next()).ToList();
        int made = 0;

        // Pass 1: live flights to/from this airport, each on the best-fitting free stand it is allowed on.
        var used = UsedCallsigns();
        var live = planner.LiveFlights.Where(f => !used.Contains(f.Callsign)).ToList();
        int liveMade = 0;
        foreach (var f in live)
        {
            if (made >= needed || Yield()) break;
            var counts = CountsPerAirline();
            Stand? best = null; Model? bestModel = null; double bestScore = -1;
            foreach (var stand in free)
            {
                var m = planner.PickLive(f, stand, StandRule.Effective(config, stand), counts);
                if (m is null) continue;
                double score = planner.Snug(m, stand);
                if (score > bestScore) { best = stand; bestModel = m; bestScore = score; }
            }
            if (best is null || bestModel is null) continue;
            if (Spawn(best, bestModel, $"{f.Callsign} {f.Route} (live)")) { made++; liveMade++; free.Remove(best); }
            sim.Idle(TimeSpan.FromMilliseconds(250));
        }
        if (live.Count > 0) Log.Write($"Live: {live.Count} flights to/from {airport.Icao}, {liveMade} placed.");

        // Pass 2: quota not reached -> random free stands, filled from the route table.
        foreach (var stand in free)
        {
            if (made >= needed || Interrupted || Yield()) break;
            var model = planner.Pick(stand, StandRule.Effective(config, stand), CountsPerAirline());
            if (model is null) continue;
            if (Spawn(stand, model, planner.FlightFor(model, UsedCallsigns()))) made++;
            sim.Idle(TimeSpan.FromMilliseconds(250));
        }
        Log.Write(Interrupted ? $"Placed {made}, paused for a command ({PlacedCount}/{Target})." : $"Topped up {made} ({PlacedCount}/{Target}).");
        return made;
    }

    private bool Spawn(Stand stand, Model model, string flight)
    {
        try
        {
            double nudge = StandRule.Effective(config, stand).Nudge ?? 0;
            var (lat, lon) = Sim.Move(stand.Latitude, stand.Longitude, stand.Heading, nudge);
            uint id = sim.SpawnStatic(model.Title, lat, lon, airport.AltitudeFeet, stand.Heading);
            var placement = new Placement(stand, model, id, flight);
            lock (_lock) _placed[stand.Index] = placement;
            Log.Write($"{stand.Name}: {model.Airline} {model.Type} {flight} ({model.Title})");

            // Give the aircraft its callsign in the sim (labels / ATC), e.g. BAW55.
            if (placement.Callsign.Length >= 4)
            {
                var back = sim.SetCallsign(id, placement.Callsign);
                if (!_callsignChecked || back != placement.Callsign)
                    Log.Write(back == placement.Callsign ? $"Callsign {placement.Callsign} accepted by the sim."
                                                         : $"Callsign {placement.Callsign}: sim reports \"{back ?? "no data"}\".");
                _callsignChecked = true;
            }
            Changed?.Invoke();
            return true;
        }
        catch (Exception e) { Log.Write($"{stand.Name}: spawn failed: {e.Message}"); return false; }
    }

    /// <summary>Manual add/replace: "SAA-A320", or null for an automatic pick. Clears any hold.</summary>
    public bool PlaceManual(Stand stand, string? airline, string? type)
    {
        // Never drop an aircraft onto one that is already there (yours, FSLTL, VATSIM).
        ScanOthers();
        lock (_lock)
            if (_occupiedBy.TryGetValue(stand.Index, out var who))
            {
                Log.Write($"{stand.Name}: occupied by \"{who}\", not adding.");
                return false;
            }
        Remove(stand.Index, hold: false);
        lock (_lock) _held.Remove(stand.Index);
        var planner = Planner(fresh: false);
        var model = airline is null || type is null
            ? planner.Pick(stand, StandRule.Effective(config, stand), CountsPerAirline())
            : planner.PickExact(stand, StandRule.Effective(config, stand), airline, type);
        if (model is null) { Log.Write($"{stand.Name}: no FSLTL model {(airline is null ? "allowed here" : $"{airline}-{type} that fits {stand.Radius:F0} m")}."); Changed?.Invoke(); return false; }
        return Spawn(stand, model, airline is null ? planner.FlightFor(model, UsedCallsigns()) : "(manual)");
    }

    /// <summary>Removes our aircraft (if any) and keeps the stand empty until released.</summary>
    public void RemoveAndHold(Stand stand)
    {
        Remove(stand.Index, hold: true);
        Log.Write($"{stand.Name}: held empty.");
    }

    public void ReleaseHold(Stand stand)
    {
        lock (_lock) if (!_held.Remove(stand.Index)) return;
        Log.Write($"{stand.Name}: released for auto-fill.");
        Changed?.Invoke();
    }

    /// <summary>
    /// Toggle the beacon of the aircraft on this stand - ours, or another one (VATSIM via vPilot, FSLTL, ...) -
    /// and log what the sim reports back. For vPilot aircraft, vPilot may set it back on a later update.
    /// </summary>
    public void ToggleBeacon(Stand stand)
    {
        Placement? p;
        uint otherId = 0;
        string current, who;
        lock (_lock)
        {
            _placed.TryGetValue(stand.Index, out p);
            _occupiedObject.TryGetValue(stand.Index, out otherId);
            current = p?.Lights ?? (_occupiedLights.TryGetValue(stand.Index, out var l) ? l : "");
            who = p is not null ? $"our {p.Callsign}" : _occupiedId.TryGetValue(stand.Index, out var id) && id.Length > 0 ? id : "other aircraft";
        }
        uint objectId = p?.ObjectId ?? otherId;
        if (objectId == 0) { Log.Write($"{stand.Name}: no aircraft on this stand."); return; }

        bool on = !current.StartsWith('B');
        var reported = sim.SetBeacon(objectId, on);
        if (p is not null && reported is not null) p.Lights = reported;
        else if (reported is not null) lock (_lock) _occupiedLights[stand.Index] = reported;
        bool worked = reported is not null && reported.StartsWith('B') == on;
        Log.Write($"{stand.Name} ({who}): beacon {(on ? "ON" : "OFF")} -> sim reports {reported ?? "no data"}{(worked ? "" : " (not applied)")}.");
        Changed?.Invoke();
    }

    public bool IsHeld(Stand stand) { lock (_lock) return _held.Contains(stand.Index); }

    /// <summary>Removes every parked aircraft of this airline (after it was excluded).</summary>
    public void RemoveAirline(string airline)
    {
        List<int> idx;
        lock (_lock) idx = _placed.Where(kv => kv.Value.Model.Airline == airline).Select(kv => kv.Key).ToList();
        foreach (var i in idx) Remove(i, hold: false);
        _planner = null;
    }

    /// <summary>After a rule change: removes aircraft that no longer satisfy their stand's rule.</summary>
    public void EnforceRules()
    {
        var planner = Planner(fresh: false);
        List<Placement> all;
        lock (_lock) all = _placed.Values.ToList();
        foreach (var p in all)
        {
            var rule = StandRule.Effective(config, p.Stand);
            bool ok = rule.Disabled != true && planner.Allowed(p.Model.Airline, rule) &&
                      (rule.Options.Count > 0 || p.Model.IsCargo == rule.WantsCargo) &&
                      (rule.Options.Count == 0 || rule.Options.Select(StandRule.ParseOption).Any(o => o?.airline == p.Model.Airline && o?.type == p.Model.Type));
            if (ok) continue;
            Log.Write($"{p.Stand.Name}: {p.Model.Airline} {p.Model.Type} no longer matches the rule, removing.");
            Remove(p.Stand.Index, hold: false);
        }
    }

    private List<Sim.ObjectState> ScanOthers()
    {
        HashSet<uint> ours;
        lock (_lock) ours = _placed.Values.Select(p => p.ObjectId).ToHashSet();
        var withIds = sim.ScanAircraft(ScanRadiusMeters).Where(a => !ours.Contains(a.id)).ToList();
        var others = withIds.Select(a => a.state).ToList();
        lock (_lock)
        {
            _occupiedBy.Clear();
            _occupiedId.Clear();
            _occupiedLights.Clear();
            _occupiedObject.Clear();
            foreach (var s in airport.Stands)
            {
                var hit = withIds.FirstOrDefault(a => Sim.DistanceMeters(a.state.Lat, a.state.Lon, s.Latitude, s.Longitude) <= OccupiedRadius(s));
                if (hit.state is not { } o) continue;
                _occupiedBy[s.Index] = o.Title; _occupiedId[s.Index] = o.AtcId.Trim(); _occupiedLights[s.Index] = o.Lights;
                _occupiedObject[s.Index] = hit.id;
            }
        }
        return others;
    }

    /// <summary>Removes our aircraft from any stand another aircraft is moving onto (seen on two scans in a row).</summary>
    public int Watch()
    {
        int removed = 0;
        var others = ScanOthers();
        List<Placement> all;
        lock (_lock) all = _placed.Values.ToList();
        foreach (var p in all)
        {
            var intruder = others.FirstOrDefault(o => Sim.DistanceMeters(o.Lat, o.Lon, p.Stand.Latitude, p.Stand.Longitude) <= OccupiedRadius(p.Stand));
            p.ConflictHits = intruder is null ? 0 : p.ConflictHits + 1;
            if (p.ConflictHits < 2) continue;
            Log.Write($"{p.Stand.Name}: \"{intruder!.Title}\" is on the stand, removing our {p.Model.Airline} {p.Model.Type}.");
            Remove(p.Stand.Index, hold: true);
            removed++;
        }
        Changed?.Invoke();
        return removed;
    }

    private void Remove(int standIndex, bool hold)
    {
        Placement? p;
        lock (_lock)
        {
            _placed.Remove(standIndex, out p);
            if (hold) _held.Add(standIndex);
        }
        if (p is not null) sim.Remove(p.ObjectId);
        Changed?.Invoke();
    }

    public void RemoveAll()
    {
        List<int> idx;
        lock (_lock) idx = _placed.Keys.ToList();
        foreach (var i in idx) Remove(i, hold: false);
        sim.Idle(TimeSpan.FromMilliseconds(300)); // let the sim process the removals before we disconnect
    }

    /// <summary>Thread-safe copy of the stand table, ordered by bay.</summary>
    public List<StandRow> Snapshot(bool allStands)
    {
        lock (_lock)
        lock (config) // rule edits happen under lock(config) on the sim thread
        {
            return airport.Stands
                .Where(s => allStands || config.StandTypes.Contains(s.TypeName, StringComparer.OrdinalIgnoreCase))
                .OrderBy(s => s.NameCode).ThenBy(s => s.Number).ThenBy(s => s.SuffixCode)
                .Select(s =>
                {
                    var rule = StandRule.Effective(config, s);
                    string aircraft = _placed.TryGetValue(s.Index, out var p) ? $"{p.Model.Airline} {p.Model.Type,-4}  {p.Model.Title}"
                                    : _occupiedBy.TryGetValue(s.Index, out var t) ? $"({t})" : "";
                    string state = _placed.ContainsKey(s.Index) ? "parked"
                                 : _held.Contains(s.Index) ? "HELD"
                                 : _occupiedBy.ContainsKey(s.Index) ? "occupied"
                                 : !IsEligible(s) ? "-" : "free";
                    // Callsign column: our callsign, "- <its ATC ID>" when another aircraft (yours, FSLTL, VATSIM) is on the stand,
                    // "held" when kept empty, blank when free.
                    // Placed with no flight available -> blank callsign; only true manual adds say "(manual)".
                    string callsign = p is not null ? (p.Callsign.Length > 0 ? p.Callsign + (p.Live ? " *" : "") : p.Flight == "(manual)" ? "(manual)" : "")
                                    : _occupiedBy.ContainsKey(s.Index) ? ("- " + (_occupiedId.TryGetValue(s.Index, out var oid) ? oid : "")).TrimEnd()
                                    : _held.Contains(s.Index) ? "held"
                                    : "";
                    string lights = p is not null ? p.Lights : _occupiedLights.TryGetValue(s.Index, out var l) ? l : "";
                    return new StandRow(s, StandRule.ApronOf(s), rule.Describe(), aircraft, state, callsign, lights);
                }).ToList();
        }
    }

    /// <summary>Console summary (used by the non-TUI "fill" command).</summary>
    public void PrintSummary()
    {
        foreach (var r in Snapshot(allStands: false))
            Console.WriteLine($"{r.Stand.Name,-12} {r.Stand.TypeName,-7} {r.Stand.Radius,3:F0}m  {r.State,-8} {r.Aircraft}");
    }
}
