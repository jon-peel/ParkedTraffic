using System.Collections.Concurrent;

namespace ParkedTraffic;

/// <summary>
/// Owns the SimConnect session and the filler on one background thread. The UI only enqueues commands
/// and reads <see cref="Filler.Snapshot"/>.
/// </summary>
public sealed class SimWorker : IDisposable
{
    private string _icao;

    /// <summary>The airport being filled (detected from the user's position when none was given).</summary>
    public string Icao => _icao;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private volatile bool _stop;

    public Airport? Airport { get; private set; }
    public Filler? Filler { get; private set; }
    public AirportConfig? Config { get; private set; }
    public GlobalConfig? Global { get; private set; }
    public RouteDb? Routes { get; private set; }
    public bool AutoRefill { get; set; } = true;
    public string Status { get; private set; } = "starting";

    public event Action? Ready;
    public event Action? Stopped;

    /// <param name="icao">Airport to fill, or null/empty to use the airport the user's aircraft is at.</param>
    public SimWorker(string? icao)
    {
        _icao = (icao ?? "").ToUpperInvariant();
        _thread = new Thread(Run) { IsBackground = true, Name = "sim" };
    }

    public void Start() => _thread.Start();

    /// <summary>Runs an action on the sim thread.</summary>
    public void Post(Action action) { if (!_stop) _queue.Add(action); }

    /// <summary>Removes all parked aircraft, closes SimConnect and ends the thread.</summary>
    public void Stop() { _stop = true; _queue.Add(() => { }); }

    private void Run()
    {
        Sim? sim = null;
        try
        {
            Status = "connecting";
            sim = Sim.Connect();
            Log.Write($"Connected to simulator (SimConnect {sim.ServerVersion}).");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ParkedTraffic/0.1");
            try { Routes = RouteDb.LoadAsync(http).GetAwaiter().GetResult(); Log.Write($"Route table: {Routes.RouteCount:N0} callsigns."); }
            catch (Exception e) { Log.Write($"Route table unavailable ({e.Message}); using config mix."); }

            Airport = _icao.Length > 0 ? sim.GetAirport(_icao) : DetectAirport(sim);
            _icao = Airport.Icao;
            Log.Write($"{Airport.Icao}: {Airport.Stands.Count} parking spots.");

            Config = AirportConfig.LoadOrCreate(_icao, out var path);
            Global = GlobalConfig.LoadOrCreate();
            Log.Write($"Config {Path.GetFileName(path)}: {Config.Aprons.Count} apron rules, {Config.Stands.Count} stand rules, {Config.ExcludeAirlines.Count} excluded airlines.");

            var models = Catalog.Load();
            Log.Write($"FSLTL catalog: {models.Count} liveries.");

            var rng = new Random();
            var airport = Airport;
            MixPlanner BuildPlanner()
            {
                var live = LiveTraffic.NearAsync(http, airport.Latitude, airport.Longitude, Config.LiveRadiusNm).GetAwaiter().GetResult();
                int related = Routes is null ? 0 : live.Count(c => Routes.Lookup(c.Callsign)?.Airports.Contains(airport.Icao) == true);
                Log.Write($"Live traffic: {live.Count} aircraft within {Config.LiveRadiusNm} nm, {related} to/from {airport.Icao}.");
                lock (Config) return MixPlanner.Build(airport.Icao, Routes, live, models, Config, Global, rng);
            }

            // The fill gives way whenever a TUI command is waiting, so keys respond at once even at big airports.
            Filler = new Filler(sim, Airport, Config, BuildPlanner, rng, shouldYield: () => _queue.Count > 0);
            Filler.SetTarget();
            Status = "running";
            Ready?.Invoke();
            Filler.TopUp();

            var nextWatch = DateTime.UtcNow.AddSeconds(5);
            var lastTopUp = DateTime.UtcNow;
            while (!_stop)
            {
                if (_queue.TryTake(out var action, 200))
                {
                    try { action(); } catch (Exception e) { Log.Write($"Error: {e.Message}"); }
                    continue;
                }
                // A fill that paused for a command carries on now the queue is empty (same mix, no new lookup).
                if (Filler.Interrupted && AutoRefill)
                {
                    Filler.TopUp(freshMix: false);
                    continue;
                }
                sim.Idle(TimeSpan.FromMilliseconds(50));
                if (DateTime.UtcNow < nextWatch) continue;
                nextWatch = DateTime.UtcNow.AddSeconds(5);
                Filler.Watch();
                if (AutoRefill && Filler.PlacedCount < Filler.Target && DateTime.UtcNow - lastTopUp > TimeSpan.FromSeconds(60))
                {
                    Filler.TopUp();
                    lastTopUp = DateTime.UtcNow;
                }
            }
        }
        catch (Exception e)
        {
            Status = "error";
            Log.Write($"Stopped: {e.Message}");
        }
        finally
        {
            try { Filler?.RemoveAll(); } catch { }
            sim?.Dispose();
            Status = "stopped";
            Log.Write("Removed all parked aircraft. Disconnected.");
            Stopped?.Invoke();
        }
    }

    /// <summary>The airport the user's aircraft is at: the nearest one (within 15 km) the sim has parking stands for.</summary>
    private Airport DetectAirport(Sim sim)
    {
        var me = sim.Probe(0) ?? throw new InvalidOperationException("Could not read your aircraft's position from the sim.");
        if (Routes is null) throw new InvalidOperationException("No airport given and the airport table is unavailable; start with an ICAO, e.g. ParkedTraffic FACT.");

        foreach (var (a, km) in Routes.Nearest(me.Lat, me.Lon).Take(5))
        {
            try
            {
                var airport = sim.GetAirport(a.Icao);
                if (airport.Stands.Count == 0) continue;
                Log.Write($"You are at {a.Icao} ({km:F1} km from its reference point).");
                return airport;
            }
            catch { /* not in the sim's database; try the next one */ }
        }
        throw new InvalidOperationException($"No airport with parking stands within 15 km of your aircraft ({me.Lat:F4}, {me.Lon:F4}).");
    }

    public void Dispose() => Stop();
}
