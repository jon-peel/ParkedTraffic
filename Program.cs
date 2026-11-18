using ParkedTraffic;

//   ParkedTraffic                                   -> TUI at the airport your aircraft is at (double-click)
//   ParkedTraffic FACT                              -> TUI at a given airport
//   ParkedTraffic stands <ICAO>                     -> list every parking spot (size, heading, position)
//   ParkedTraffic spawn <ICAO> "Gate A 12" "<title>"-> park one model on that stand until Enter is pressed

// One-click: no arguments -> TUI, airport detected from your position. A lone ICAO -> TUI at that airport.
if (args.Length == 0 || (args.Length == 1 && args[0] == "tui"))
    return Tui.Run(null);
if (args.Length == 1 && args[0].Length == 4 && args[0].All(char.IsLetterOrDigit))
    return Tui.Run(args[0]);
if (args[0] == "tui")
    return Tui.Run(args[1]);

if (args.Length < 2)
{
    Console.WriteLine("""
        Usage:
          ParkedTraffic                  (TUI at the airport you're at)
          ParkedTraffic <ICAO>           (TUI at that airport)
          ParkedTraffic stands <ICAO>
          ParkedTraffic spawn  <ICAO> "<stand name>" "<model title>"
        """);
    return 1;
}

if (args[0] == "catalog")
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var models = Catalog.Load();
    Console.WriteLine($"{models.Count} usable liveries, {models.Select(m => m.Airline).Distinct().Count()} airlines, {models.Select(m => m.Type).Distinct().Count()} types, loaded in {sw.ElapsedMilliseconds} ms.");
    foreach (var m in models.Where(m => m.Airline == args[1].ToUpperInvariant()).OrderBy(m => m.Type))
        Console.WriteLine($"  {m.Airline} {m.Type,-5} span {m.WingspanMeters,5:F1} m  nose +{m.NoseGearMeters,4:F1} m  {m.Title}");
    return 0;
}

try
{
    using var sim = Sim.Connect();
    Console.WriteLine($"Connected to simulator (SimConnect {sim.ServerVersion}).");

    var airport = sim.GetAirport(args[1]);
    Console.WriteLine($"{airport.Icao}: reference {airport.Latitude:F6}, {airport.Longitude:F6}, elevation {airport.AltitudeFeet:F0} ft, {airport.Stands.Count} parking spots.");

    switch (args[0].ToLowerInvariant())
    {
        case "stands":
            PrintStands(airport);
            return 0;

        case "fill":
        {
            var config = AirportConfig.LoadOrCreate(airport.Icao, out var configPath);
            var global = GlobalConfig.LoadOrCreate();
            Console.WriteLine($"Config: {configPath}");
            Console.WriteLine($"Excluded: {global.DefunctAirlines.Count} defunct airlines (global.json), {config.ExcludeAirlines.Count} at {airport.Icao} ({string.Join(", ", config.ExcludeAirlines)}).");
            var models = Catalog.Load();
            Console.WriteLine($"FSLTL catalog: {models.Count} liveries.");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ParkedTraffic/0.1");
            RouteDb? routes = null;
            try { routes = await RouteDb.LoadAsync(http); Console.WriteLine($"Route table: {routes.RouteCount:N0} callsigns."); }
            catch (Exception e) { Console.WriteLine($"Route table unavailable ({e.Message}); using config mix."); }

            var rng = new Random();
            MixPlanner BuildPlanner()
            {
                var live = LiveTraffic.NearAsync(http, airport.Latitude, airport.Longitude, config.LiveRadiusNm).GetAwaiter().GetResult();
                var related = routes is null ? 0 : live.Count(c => routes.Lookup(c.Callsign)?.Airports.Contains(airport.Icao) == true);
                Console.WriteLine($"Live traffic: {live.Count} aircraft within {config.LiveRadiusNm} nm, {related} flying to/from {airport.Icao}.");
                return MixPlanner.Build(airport.Icao, routes, live, models, config, global, rng);
            }

            var filler = new Filler(sim, airport, config, BuildPlanner, rng);
            filler.SetTarget();
            filler.TopUp();
            filler.PrintSummary();
            var lastTopUp = DateTime.UtcNow;

            string stopFile = Path.Combine(Environment.CurrentDirectory, "stop");
            File.Delete(stopFile);
            bool quit = false;
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit = true; };
            Console.WriteLine($"Running. Watching stands every 5 s. Ctrl+C (or create {stopFile}) to remove all and exit.");
            try
            {
                while (!quit && !File.Exists(stopFile))
                {
                    filler.Watch();
                    // Below target (someone took a stand)? Top up with a fresh live lookup, at most once a minute.
                    if (filler.PlacedCount < filler.Target && DateTime.UtcNow - lastTopUp > TimeSpan.FromSeconds(60))
                    {
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {filler.PlacedCount}/{filler.Target} parked, topping up...");
                        filler.TopUp();
                        lastTopUp = DateTime.UtcNow;
                    }
                    for (int i = 0; i < 25 && !quit && !File.Exists(stopFile); i++) sim.Idle(TimeSpan.FromMilliseconds(200));
                }
            }
            finally
            {
                filler.RemoveAll();
                File.Delete(stopFile);
                Console.WriteLine("Removed all parked aircraft.");
            }
            return 0;
        }

        case "test":
        {
            // Visibility matrix: which model / which creation method actually renders?
            var cases = new (string stand, string title, bool nonAtc)[]
            {
                ("Gate B 6", "Airbus A320 Neo Asobo", false),       // default model, simulated object
                ("Gate B 8", "FSLTL_FAIB_B738_FlySafair", true),    // FSLTL model, non-ATC aircraft
                ("Gate B 3", "Airbus A320 Neo Asobo", true),        // default model, non-ATC aircraft
            };
            var made = new List<(uint id, Stand s, string label)>();
            foreach (var (standName, title, nonAtc) in cases)
            {
                var s = FindStand(airport, standName)!;
                string label = $"{s.Name}: {title} via {(nonAtc ? "AICreateNonATCAircraft" : "AICreateSimulatedObject")}";
                try { made.Add((sim.SpawnStatic(title, s.Latitude, s.Longitude, airport.AltitudeFeet, s.Heading, nonAtc), s, label)); Console.WriteLine($"Created  {label}"); }
                catch (Exception e) { Console.WriteLine($"FAILED   {label}: {e.Message}"); }
            }
            int hold = args.Length >= 3 && int.TryParse(args[2], out var h) ? h : 300;
            var until = DateTime.UtcNow.AddSeconds(hold);
            while (DateTime.UtcNow < until)
            {
                foreach (var (oid, s, label) in made)
                {
                    var o = sim.Probe(oid);
                    Console.WriteLine(o is null ? $"  {label}: gone"
                        : $"  {label}: {Sim.DistanceMeters(o.Lat, o.Lon, s.Latitude, s.Longitude):F1} m from stand, ground={o.OnGround}, hdg {o.Heading:F0}");
                }
                sim.Idle(TimeSpan.FromSeconds(30));
            }
            foreach (var (oid, _, _) in made) sim.Remove(oid);
            Console.WriteLine("Removed all.");
            return 0;
        }

        case "probe" when args.Length >= 3 && uint.TryParse(args[2], out var probeId):
            var st = sim.Probe(probeId);
            Console.WriteLine(st is null ? $"Object {probeId}: no data (does not exist)." : $"Object {probeId}: \"{st.Title}\" at {st.Lat:F6},{st.Lon:F6} ground={st.OnGround}.");
            return 0;

        case "spawn" when args.Length >= 4:
            var stand = FindStand(airport, args[2]);
            if (stand is null) { Console.Error.WriteLine($"No stand matching \"{args[2]}\"."); return 2; }
            Console.WriteLine($"Spawning \"{args[3]}\" on {stand.Name} ({stand.TypeName}, radius {stand.Radius:F0} m, heading {stand.Heading:F0}).");
            bool useNonAtc = args.Length >= 6 && args[5] == "nonatc";
            Console.WriteLine($"Method: {(useNonAtc ? "AICreateNonATCAircraft" : "AICreateSimulatedObject")}");
            // Optional "offset=<metres>": move the aircraft forward (+) or back (-) along the stand heading.
            double offset = args.Where(a => a.StartsWith("offset=")).Select(a => double.Parse(a[7..], System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
            var (spawnLat, spawnLon) = Sim.Move(stand.Latitude, stand.Longitude, stand.Heading, offset);
            if (offset != 0) Console.WriteLine($"Offset {offset:F1} m along heading {stand.Heading:F0}.");
            uint id = sim.SpawnStatic(args[3], spawnLat, spawnLon, airport.AltitudeFeet, stand.Heading, useNonAtc);
            if (args.Length >= 5 && args[4] == "untilstop")
            {
                // Holds until a file named "stop" appears next to the exe's working folder (or the sim closes).
                string stopFile = Path.Combine(Environment.CurrentDirectory, "stop");
                File.Delete(stopFile);
                Console.WriteLine($"Created sim object {id}. Holding until {stopFile} exists...");
                var next = DateTime.UtcNow;
                while (!File.Exists(stopFile))
                {
                    if (DateTime.UtcNow >= next)
                    {
                        var o = sim.Probe(id);
                        Console.WriteLine(o is null ? "  object gone" : $"  {Sim.DistanceMeters(o.Lat, o.Lon, stand.Latitude, stand.Longitude):F1} m from stand, ground={o.OnGround}, hdg {o.Heading:F0}");
                        next = DateTime.UtcNow.AddSeconds(30);
                    }
                    sim.Idle(TimeSpan.FromSeconds(1));
                }
                File.Delete(stopFile);
            }
            else if (args.Length >= 5 && int.TryParse(args[4], out var holdSeconds))
            {
                Console.WriteLine($"Created sim object {id}. Holding for {holdSeconds} s, then removing...");
                var end = DateTime.UtcNow.AddSeconds(holdSeconds);
                while (DateTime.UtcNow < end)
                {
                    var o = sim.Probe(id);
                    var me = sim.Probe(0);
                    if (o is null) Console.WriteLine("  object: no data (object no longer exists?)");
                    else
                    {
                        double fromStand = Sim.DistanceMeters(o.Lat, o.Lon, stand.Latitude, stand.Longitude);
                        double fromMe = me is null ? double.NaN : Sim.DistanceMeters(o.Lat, o.Lon, me.Lat, me.Lon);
                        Console.WriteLine($"  object \"{o.Title}\" at {o.Lat:F6},{o.Lon:F6} alt {o.AltFeet:F0} ft ground={o.OnGround} hdg {o.Heading:F0} | {fromStand:F1} m from stand, {fromMe:F0} m from you");
                    }
                    sim.Idle(TimeSpan.FromSeconds(10));
                }
            }
            else
            {
                Console.WriteLine($"Created sim object {id}. Press Enter to remove it...");
                while (!Console.KeyAvailable) sim.Idle(TimeSpan.FromMilliseconds(200));
                Console.ReadKey(true);
            }
            sim.Remove(id);
            Console.WriteLine("Removed.");
            return 0;

        default:
            Console.Error.WriteLine("Unknown command.");
            return 1;
    }
}
catch (Exception e)
{
    Console.Error.WriteLine(e.Message);
    return 3;
}

static Stand? FindStand(Airport a, string query)
{
    string Norm(string s) => new(s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
    string q = Norm(query);
    return a.Stands.FirstOrDefault(s => Norm(s.Name) == q)
        ?? a.Stands.FirstOrDefault(s => Norm(s.Name).EndsWith(q))
        ?? a.Stands.FirstOrDefault(s => Norm(s.Name).Replace("GATE", "") == q);
}

static void PrintStands(Airport a)
{
    Console.WriteLine();
    Console.WriteLine($"{"#",4}  {"Stand",-16} {"Type",-11} {"Radius",7} {"Hdg",5} {"Airl",4}  {"Latitude",11} {"Longitude",11}");
    foreach (var s in a.Stands.OrderBy(s => s.NameCode).ThenBy(s => s.Number).ThenBy(s => s.SuffixCode))
        Console.WriteLine($"{s.Index,4}  {s.Name,-16} {s.TypeName,-11} {s.Radius,6:F0}m {s.Heading,5:F0} {s.AirlineCount,4}  {s.Latitude,11:F6} {s.Longitude,11:F6}");
}
