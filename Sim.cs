using System.Runtime.InteropServices;

namespace ParkedTraffic;

public sealed record Stand(
    int Index, string Name, int TypeCode, int NameCode, int SuffixCode, uint Number,
    float Heading, float Radius, float BiasX, float BiasZ, int AirlineCount,
    double Latitude, double Longitude, bool HasJetway = false)
{
    public string TypeName => TypeCode switch
    {
        0 => "None", 1 => "Ramp GA", 2 => "Ramp GA S", 3 => "Ramp GA M", 4 => "Ramp GA L",
        5 => "Cargo", 6 => "Mil cargo", 7 => "Mil combat", 8 => "Gate S", 9 => "Gate M",
        10 => "Gate H", 11 => "Dock GA", 12 => "Fuel", 13 => "Vehicles", 14 => "Ramp GA XL",
        15 => "Gate XL", _ => $"Type {TypeCode}",
    };
}

public sealed record Airport(string Icao, double Latitude, double Longitude, double AltitudeFeet, List<Stand> Stands);

/// <summary>Thin, synchronous SimConnect session: send a request, pump messages until an answer arrives.</summary>
public sealed class Sim : IDisposable
{
    private const uint AirportDef = 1;
    private IntPtr _h;
    private readonly Dictionary<uint, string> _sent = new();   // sendId -> what we sent (for exception reports)
    private uint _nextRequest = 100;

    public string ServerVersion { get; private set; } = "?";

    public static Sim Connect(string clientName = "ParkedTraffic")
    {
        int hr = Native.SimConnect_Open(out var h, clientName, IntPtr.Zero, 0, IntPtr.Zero, 0);
        if (hr != 0) throw new InvalidOperationException($"Could not connect to the simulator (is MSFS running?) hr=0x{hr:X8}");
        var sim = new Sim { _h = h };
        sim.Pump(TimeSpan.FromSeconds(5), m => m.Id == RecvId.Open);
        return sim;
    }

    private void Track(string label)
    {
        if (Native.SimConnect_GetLastSentPacketID(_h, out var id) == 0) _sent[id] = label;
    }

    // ---------------------------------------------------------------- facilities

    private bool _defined;
    private static readonly string[] AirportFields = ["LATITUDE", "LONGITUDE", "ALTITUDE"];
    private static readonly string[] ParkingFields =
        ["TYPE", "TAXI_POINT_TYPE", "NAME", "SUFFIX", "NUMBER", "ORIENTATION", "HEADING", "RADIUS", "BIAS_X", "BIAS_Z"]; // N_AIRLINES is rejected by MSFS2020 (DATA_ERROR)

    private void DefineAirport()
    {
        if (_defined) return;
        void Add(string f) { Check(Native.SimConnect_AddToFacilityDefinition(_h, AirportDef, f), f); Track($"facility field {f}"); }
        Add("OPEN AIRPORT");
        foreach (var f in AirportFields) Add(f);
        Add("OPEN TAXI_PARKING");
        foreach (var f in ParkingFields) Add(f);
        Add("CLOSE TAXI_PARKING");
        Add("CLOSE AIRPORT");
        _defined = true;
    }

    public Airport GetAirport(string icao)
    {
        DefineAirport();
        uint req = _nextRequest++;
        Check(Native.SimConnect_RequestFacilityData(_h, AirportDef, req, icao.ToUpperInvariant(), ""), "RequestFacilityData");
        Track($"RequestFacilityData {icao}");

        double lat = 0, lon = 0, alt = 0;
        bool gotAirport = false;
        var raw = new List<(int idx, byte[] data)>();

        Pump(TimeSpan.FromSeconds(15), m =>
        {
            if (m.Id == RecvId.FacilityData && m.U32(12) == req)
            {
                bool isList = m.U32(28) != 0;
                int itemIndex = (int)m.U32(32);
                if (!isList && !gotAirport)
                {
                    lat = m.F64(40); lon = m.F64(48); alt = m.F64(56);
                    gotAirport = true;
                }
                else raw.Add((itemIndex, m.Bytes(40, 44))); // 40 bytes of fields; padded to 44
            }
            return m.Id == RecvId.FacilityDataEnd && m.U32(12) == req;
        });

        if (!gotAirport) throw new InvalidOperationException($"The simulator returned no data for {icao}.");

        var stands = raw.OrderBy(r => r.idx).Select(r =>
        {
            var d = r.data;
            int type = BitConverter.ToInt32(d, 0), name = BitConverter.ToInt32(d, 8), suffix = BitConverter.ToInt32(d, 12);
            uint number = BitConverter.ToUInt32(d, 16);
            float hdg = BitConverter.ToSingle(d, 24), radius = BitConverter.ToSingle(d, 28);
            float bx = BitConverter.ToSingle(d, 32), bz = BitConverter.ToSingle(d, 36);
            int nAirlines = 0; // not available in MSFS2020
            var (plat, plon) = Offset(lat, lon, bx, bz);
            return new Stand(r.idx, FormatName(name, number, suffix), type, name, suffix, number, hdg, radius, bx, bz, nAirlines, plat, plon);
        }).ToList();

        // Some airports come back with every stand listed twice (e.g. two sceneries for the same airport).
        // Keep the first of any stands with the same name within 2 m of each other.
        var unique = new List<Stand>();
        foreach (var s in stands)
            if (!unique.Any(u => u.Name == s.Name && DistanceMeters(u.Latitude, u.Longitude, s.Latitude, s.Longitude) < 2))
                unique.Add(s);
        if (unique.Count < stands.Count)
            Log.Write($"{icao.ToUpperInvariant()}: ignored {stands.Count - unique.Count} duplicate stands.");
        stands = unique;

        // Mark stands that have a jet bridge (separate request, so a failure here never breaks the stand list).
        var jetways = GetJetways(icao);
        if (jetways.Count > 0)
            stands = stands.Select(s => jetways.Contains((s.NameCode, s.Number, s.SuffixCode)) ? s with { HasJetway = true } : s).ToList();
        Log.Write($"{icao.ToUpperInvariant()}: {stands.Count(s => s.HasJetway)} stands with a jet bridge.");

        return new Airport(icao.ToUpperInvariant(), lat, lon, alt * 3.28084, stands); // facility ALTITUDE is metres
    }

    private const uint JetwayDef = 3;
    private bool _jetwayDefined;

    /// <summary>Jet bridges at the airport, as (parking name code, number, suffix) of the stand they serve.</summary>
    private HashSet<(int name, uint number, int suffix)> GetJetways(string icao)
    {
        var result = new HashSet<(int, uint, int)>();
        try
        {
            if (!_jetwayDefined)
            {
                void Add(string f) { Check(Native.SimConnect_AddToFacilityDefinition(_h, JetwayDef, f), f); Track($"jetway field {f}"); }
                Add("OPEN AIRPORT");
                Add("OPEN JETWAY");
                Add("PARKING_GATE");
                Add("PARKING_SUFFIX");
                Add("PARKING_SPOT");
                Add("CLOSE JETWAY");
                Add("CLOSE AIRPORT");
                _jetwayDefined = true;
            }
            uint req = _nextRequest++;
            Check(Native.SimConnect_RequestFacilityData(_h, JetwayDef, req, icao.ToUpperInvariant(), ""), "RequestFacilityData (jetways)");
            Track($"RequestFacilityData jetways {icao}");
            Pump(TimeSpan.FromSeconds(10), m =>
            {
                if (m.Id == RecvId.FacilityData && m.U32(12) == req && m.U32(28) != 0)
                {
                    var d = m.Bytes(40, 12);
                    result.Add((BitConverter.ToInt32(d, 0), BitConverter.ToUInt32(d, 8), BitConverter.ToInt32(d, 4)));
                }
                return m.Id == RecvId.FacilityDataEnd && m.U32(12) == req;
            });
        }
        catch (Exception e) { Log.Write($"Jet bridge data unavailable ({e.Message})."); }
        return result;
    }

    private static string FormatName(int nameCode, uint number, int suffix)
    {
        string prefix = nameCode switch
        {
            0 => "", 1 => "Parking", 2 => "N Parking", 3 => "NE Parking", 4 => "E Parking", 5 => "SE Parking",
            6 => "S Parking", 7 => "SW Parking", 8 => "W Parking", 9 => "NW Parking", 10 => "Gate", 11 => "Dock",
            >= 12 and <= 37 => $"Gate {(char)('A' + nameCode - 12)}",
            _ => $"Name{nameCode}",
        };
        string sfx = suffix is >= 1 and <= 26 ? ((char)('A' + suffix - 1)).ToString() : "";
        return $"{prefix} {number}{sfx}".Trim();
    }

    /// <summary>East/north offset in metres from a reference point -> lat/lon (WGS84 local radii).</summary>
    private static (double lat, double lon) Offset(double lat0, double lon0, double east, double north)
    {
        const double a = 6378137.0, e2 = 6.69437999014e-3;
        double phi = lat0 * Math.PI / 180, s = Math.Sin(phi);
        double w = 1 - e2 * s * s;
        double m = a * (1 - e2) / Math.Pow(w, 1.5);   // meridional radius
        double n = a / Math.Sqrt(w);                   // prime-vertical radius
        return (lat0 + north / m * 180 / Math.PI, lon0 + east / (n * Math.Cos(phi)) * 180 / Math.PI);
    }

    // ---------------------------------------------------------------- AI objects

    /// <summary>Creates a static (non-AI-controlled) aircraft on the ground. Returns the sim object id.</summary>
    // Default: AICreateSimulatedObject (plain static object, never moved by sim AI). Verified visible with FSLTL models in MSFS2020.
    public uint SpawnStatic(string title, double lat, double lon, double altFeet, double heading, bool asNonAtcAircraft = false)
    {
        uint req = _nextRequest++;
        var pos = new InitPosition { Latitude = lat, Longitude = lon, Altitude = altFeet, Heading = heading, OnGround = 1 };
        if (asNonAtcAircraft)
        {
            Check(Native.SimConnect_AICreateNonATCAircraft(_h, title, $"PT{req}", pos, req), "AICreateNonATCAircraft");
            Track($"AICreateNonATCAircraft \"{title}\"");
        }
        else
        {
            Check(Native.SimConnect_AICreateSimulatedObject(_h, title, pos, req), "AICreateSimulatedObject");
            Track($"AICreateSimulatedObject \"{title}\"");
        }

        uint objectId = 0;
        Pump(TimeSpan.FromSeconds(15), m =>
        {
            if (m.Id == RecvId.AssignedObjectId && m.U32(12) == req) { objectId = m.U32(16); return true; }
            return false;
        });
        if (objectId == 0) throw new InvalidOperationException($"The simulator did not create \"{title}\" (see messages above).");
        return objectId;
    }

    private const uint CallsignDef = 4;
    private bool _callsignDefined;

    /// <summary>
    /// Sets the aircraft's callsign: ATC AIRLINE = "BAW", ATC FLIGHT NUMBER = "55" and ATC ID = "BAW55" (shown on
    /// MSFS labels; liveries that paint the registration will show it too). Returns the ATC ID the sim reports back.
    /// </summary>
    public string? SetCallsign(uint objectId, string callsign)
    {
        if (callsign.Length < 4) return null;
        if (!_callsignDefined)
        {
            Check(Native.SimConnect_AddToDataDefinition(_h, CallsignDef, "ATC AIRLINE", null, 7 /* STRING64 */, 0, 0xFFFFFFFF), "ATC AIRLINE");
            Track("data field ATC AIRLINE");
            Check(Native.SimConnect_AddToDataDefinition(_h, CallsignDef, "ATC FLIGHT NUMBER", null, 5 /* STRING8 */, 0, 0xFFFFFFFF), "ATC FLIGHT NUMBER");
            Track("data field ATC FLIGHT NUMBER");
            Check(Native.SimConnect_AddToDataDefinition(_h, CallsignDef, "ATC ID", null, 6 /* STRING32 */, 0, 0xFFFFFFFF), "ATC ID");
            Track("data field ATC ID");
            _callsignDefined = true;
        }

        // ATC AIRLINE (64) | ATC FLIGHT NUMBER (8) | ATC ID (32) - the ID is what MSFS labels show.
        var buf = new byte[64 + 8 + 32];
        System.Text.Encoding.ASCII.GetBytes(callsign[..3], 0, 3, buf, 0);
        var number = callsign[3..];
        System.Text.Encoding.ASCII.GetBytes(number, 0, Math.Min(7, number.Length), buf, 64);
        System.Text.Encoding.ASCII.GetBytes(callsign, 0, Math.Min(31, callsign.Length), buf, 72);
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            Check(Native.SimConnect_SetDataOnSimObject(_h, CallsignDef, objectId, 0, 0, (uint)buf.Length, pin.AddrOfPinnedObject()), "SetDataOnSimObject");
            Track($"set callsign {callsign} on {objectId}");
        }
        finally { pin.Free(); }

        // Read it back so we know whether the sim accepted it.
        uint req = _nextRequest++;
        Check(Native.SimConnect_RequestDataOnSimObject(_h, req, CallsignDef, objectId, 1 /* ONCE */, 0, 0, 0, 0), "RequestDataOnSimObject");
        Track($"read callsign on {objectId}");
        string? result = null;
        Pump(TimeSpan.FromSeconds(3), m =>
        {
            if (m.Id != RecvId.SimObjectData || m.U32(12) != req) return false;
            result = m.Ansi(40 + 72); // ATC ID as the sim now reports it
            return true;
        });
        return result;
    }

    private const uint BeaconDef = 5;
    private const uint BeaconEvent = 1;
    private bool _beaconDefined;

    /// <summary>
    /// Switch an aircraft's beacon on/off. Tries both ways the sim offers - setting LIGHT BEACON directly and sending
    /// the BEACON_LIGHTS_SET event to that object - then returns the lights the sim reports afterwards (e.g. "B---").
    /// </summary>
    public string? SetBeacon(uint objectId, bool on)
    {
        if (!_beaconDefined)
        {
            Check(Native.SimConnect_AddToDataDefinition(_h, BeaconDef, "LIGHT BEACON", "bool", 4, 0, 0xFFFFFFFF), "LIGHT BEACON");
            Track("data field LIGHT BEACON (set)");
            Check(Native.SimConnect_MapClientEventToSimEvent(_h, BeaconEvent, "BEACON_LIGHTS_SET"), "BEACON_LIGHTS_SET");
            Track("map event BEACON_LIGHTS_SET");
            _beaconDefined = true;
        }

        var value = BitConverter.GetBytes(on ? 1.0 : 0.0);
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(value, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            Check(Native.SimConnect_SetDataOnSimObject(_h, BeaconDef, objectId, 0, 0, 8, pin.AddrOfPinnedObject()), "SetDataOnSimObject beacon");
            Track($"set LIGHT BEACON on {objectId}");
        }
        finally { pin.Free(); }

        Check(Native.SimConnect_TransmitClientEvent(_h, objectId, BeaconEvent, on ? 1u : 0u, 1, 0x10), "TransmitClientEvent beacon");
        Track($"BEACON_LIGHTS_SET to {objectId}");

        Pump(TimeSpan.FromMilliseconds(300), _ => false); // let the sim apply it (and report any exception)
        return Probe(objectId)?.Lights;
    }

    public void Remove(uint objectId)
    {
        Check(Native.SimConnect_AIRemoveObject(_h, objectId, _nextRequest++), "AIRemoveObject");
        Track($"AIRemoveObject {objectId}");
        Pump(TimeSpan.FromMilliseconds(10), _ => false); // just drain any reply; no need to wait per aircraft
    }

    /// <param name="AtcId">ATC ID: for VATSIM aircraft injected by vPilot this is normally the callsign.</param>
    /// <param name="Lights">Beacon/Taxi/Strobe/Landing as "BTSL", a dash for each light that is off (e.g. "B---").</param>
    public sealed record ObjectState(double Lat, double Lon, double AltFeet, bool OnGround, double Heading, string Title, string AtcId = "",
                                     string Lights = "----");

    private static string LightsAt(Msg m, int off) =>
        new(new[] { m.F64(off) != 0 ? 'B' : '-', m.F64(off + 8) != 0 ? 'T' : '-', m.F64(off + 16) != 0 ? 'S' : '-', m.F64(off + 24) != 0 ? 'L' : '-' });

    private const uint StateDef = 2;
    private bool _stateDefined;

    /// <summary>Every aircraft (user, AI, multiplayer/vPilot, ours) within a radius of the user's aircraft.</summary>
    public List<(uint id, ObjectState state)> ScanAircraft(uint radiusMeters)
    {
        DefineState();
        uint req = _nextRequest++;
        Check(Native.SimConnect_RequestDataOnSimObjectType(_h, req, StateDef, radiusMeters, 2 /* AIRCRAFT */), "RequestDataOnSimObjectType");
        Track("RequestDataOnSimObjectType");
        var list = new List<(uint, ObjectState)>();
        Pump(TimeSpan.FromSeconds(3), m =>
        {
            if (m.Id != RecvId.SimObjectDataByType || m.U32(12) != req) return false;
            uint entry = m.U32(28), outOf = m.U32(32);
            if (outOf == 0) return true; // nothing in range
            list.Add((m.U32(16), new ObjectState(m.F64(40), m.F64(48), m.F64(56), m.F64(64) != 0, m.F64(72), m.Ansi(80), m.Ansi(80 + 256),
                                                 LightsAt(m, 368))));
            return entry >= outOf;
        });
        return list;
    }

    private void DefineState()
    {
        if (_stateDefined) return;
        void Add(string name, string? units, uint type)
        {
            Check(Native.SimConnect_AddToDataDefinition(_h, StateDef, name, units, type, 0, 0xFFFFFFFF), name);
            Track($"data field {name}");
        }
        Add("PLANE LATITUDE", "degrees", 4);
        Add("PLANE LONGITUDE", "degrees", 4);
        Add("PLANE ALTITUDE", "feet", 4);
        Add("SIM ON GROUND", "bool", 4);
        Add("PLANE HEADING DEGREES TRUE", "degrees", 4);
        Add("TITLE", null, 9);  // STRING256, offset 80
        Add("ATC ID", null, 6); // STRING32,  offset 336
        Add("LIGHT BEACON", "bool", 4);  // offset 368
        Add("LIGHT TAXI", "bool", 4);    // offset 376
        Add("LIGHT STROBE", "bool", 4);  // offset 384
        Add("LIGHT LANDING", "bool", 4); // offset 392
        _stateDefined = true;
    }

    /// <summary>Asks the sim where an object is right now. objectId 0 = the user's aircraft.</summary>
    public ObjectState? Probe(uint objectId)
    {
        DefineState();
        uint req = _nextRequest++;
        Check(Native.SimConnect_RequestDataOnSimObject(_h, req, StateDef, objectId, 1 /* ONCE */, 0, 0, 0, 0), "RequestDataOnSimObject");
        Track($"RequestDataOnSimObject object {objectId}");
        ObjectState? result = null;
        Pump(TimeSpan.FromSeconds(5), m =>
        {
            if (m.Id != RecvId.SimObjectData || m.U32(12) != req) return false;
            result = new ObjectState(m.F64(40), m.F64(48), m.F64(56), m.F64(64) != 0, m.F64(72), m.Ansi(80), m.Ansi(80 + 256), LightsAt(m, 368));
            return true;
        });
        return result;
    }

    /// <summary>Moves a point a distance (metres) along a true heading.</summary>
    public static (double lat, double lon) Move(double lat, double lon, double headingDeg, double meters)
    {
        double h = headingDeg * Math.PI / 180;
        return Offset(lat, lon, meters * Math.Sin(h), meters * Math.Cos(h));
    }

    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        double r = 6371000, p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180;
        double dp = p2 - p1, dl = (lon2 - lon1) * Math.PI / 180;
        double h = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * r * Math.Asin(Math.Sqrt(h));
    }

    /// <summary>Process incoming messages for a short while (keeps the connection healthy).</summary>
    public void Idle(TimeSpan t) => Pump(t, _ => false);

    // ---------------------------------------------------------------- message pump

    internal readonly struct Msg(IntPtr p, uint size)
    {
        public RecvId Id => (RecvId)U32(8);
        public uint U32(int off) => (uint)Marshal.ReadInt32(p, off);
        public double F64(int off) => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p, off));
        public byte[] Bytes(int off, int len) { var b = new byte[len]; Marshal.Copy(p + off, b, 0, Math.Min(len, (int)size - off)); return b; }
        public string Ansi(int off) => Marshal.PtrToStringAnsi(p + off) ?? "";
    }

    private void Pump(TimeSpan timeout, Func<Msg, bool> done)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (Native.SimConnect_GetNextDispatch(_h, out var p, out var size) != 0) { Thread.Sleep(10); continue; }
            var m = new Msg(p, size);
            switch (m.Id)
            {
                case RecvId.Open:
                    ServerVersion = $"{m.U32(12 + 256 + 16)}.{m.U32(12 + 256 + 20)} build {m.U32(12 + 256 + 24)}";
                    break;
                case RecvId.Exception:
                    uint ex = m.U32(12), send = m.U32(16), idx = m.U32(20);
                    Log.Write($"  ! SimConnect exception {ex} ({ExceptionName(ex)}) for {(_sent.TryGetValue(send, out var what) ? what : $"send #{send}")}, index {idx}");
                    break;
                case RecvId.Quit:
                    throw new InvalidOperationException("The simulator closed the connection.");
            }
            if (done(m)) return;
        }
    }

    private static string ExceptionName(uint e) => e switch
    {
        1 => "ERROR", 2 => "SIZE_MISMATCH", 3 => "UNRECOGNIZED_ID", 4 => "UNOPENED", 5 => "VERSION_MISMATCH",
        7 => "NAME_UNRECOGNIZED", 11 => "TOO_MANY_OBJECTS", 12 => "TOO_MANY_REQUESTS", 18 => "INVALID_DATA_TYPE",
        19 => "INVALID_DATA_SIZE", 20 => "DATA_ERROR", 21 => "INVALID_ARRAY", 22 => "CREATE_OBJECT_FAILED",
        23 => "LOAD_FLIGHTPLAN_FAILED", 24 => "OPERATION_INVALID_FOR_OBJECT_TYPE", 25 => "ILLEGAL_OPERATION",
        _ => "?",
    };

    private static void Check(int hr, string what)
    {
        if (hr != 0) throw new InvalidOperationException($"{what} failed: hr=0x{hr:X8}");
    }

    public void Dispose()
    {
        if (_h != IntPtr.Zero) { Native.SimConnect_Close(_h); _h = IntPtr.Zero; }
    }
}
