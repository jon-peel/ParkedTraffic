using System.Globalization;
using System.Text.RegularExpressions;

namespace ParkedTraffic;

/// <summary>One spawnable livery from the FSLTL traffic base.</summary>
/// <param name="IsProp">Turboprop or piston (engines.cfg engine_type 5 or 0): never parked at a jet bridge.</param>
public sealed record Model(string Title, string Airline, string Type, double WingspanMeters, double NoseGearMeters, bool IsCargo, bool IsProp)
{
    public double HalfSpan => WingspanMeters / 2;
}

/// <summary>Reads the FSLTL traffic base (read-only) and lists usable liveries with airline, type and size.</summary>
public static class Catalog
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\LocalCache\Packages\Community\fsltl-traffic-base\SimObjects\Airplanes");

    private static readonly Regex Section = new(@"^\s*\[(?<name>[^\]]+)\]", RegexOptions.Compiled);
    private static readonly Regex KeyValue = new(@"^\s*(?<key>[A-Za-z0-9_.]+)\s*=\s*(?<value>[^;\r\n]*)", RegexOptions.Compiled);

    public static List<Model> Load(string? root = null)
    {
        root ??= DefaultRoot;
        var geometry = new Dictionary<string, (double span, double nose, bool prop)?>(StringComparer.OrdinalIgnoreCase);
        var models = new List<Model>();

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var cfg = Path.Combine(dir, "aircraft.cfg");
            if (!File.Exists(cfg)) continue;

            var sections = Parse(cfg);
            string type = Get(sections, "GENERAL", "icao_type_designator");
            string baseRel = Get(sections, "VARIATION", "base_container");
            string baseDir = string.IsNullOrEmpty(baseRel) ? dir : Path.GetFullPath(Path.Combine(dir, baseRel));

            if (!geometry.TryGetValue(baseDir, out var geo))
                geometry[baseDir] = geo = ReadGeometry(baseDir);
            if (geo is null) continue;

            if (string.IsNullOrEmpty(type))
                type = Get(Parse(Path.Combine(baseDir, "aircraft.cfg")), "GENERAL", "icao_type_designator");

            foreach (var (name, kv) in sections)
            {
                if (!name.StartsWith("FLTSIM.", StringComparison.OrdinalIgnoreCase)) continue;
                kv.TryGetValue("title", out var title);
                kv.TryGetValue("icao_airline", out var airline);
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(airline)) continue;
                if (IsPlaceholder(title)) continue;
                models.Add(new Model(title, airline.ToUpperInvariant(), type.ToUpperInvariant(), geo.Value.span, geo.Value.nose,
                                     IsCargo(title, type), geo.Value.prop));
            }
        }

        // Same title can appear more than once across packages; keep the first.
        return models.GroupBy(m => m.Title, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    private static bool IsPlaceholder(string title) =>
        title.Contains("STUB", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("ZZZZ", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("Historical", StringComparison.OrdinalIgnoreCase);

    /// <summary>Freighter liveries: only used on Cargo stands, never on passenger stands.</summary>
    private static bool IsCargo(string title, string type) =>
        title.Contains("Cargo", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("Freight", StringComparison.OrdinalIgnoreCase) ||
        type.EndsWith('F') || type.EndsWith('f');

    /// <summary>Wingspan and nose-gear distance ahead of the reference point, both in metres.</summary>
    private static (double span, double nose, bool prop)? ReadGeometry(string baseDir)
    {
        var fm = Path.Combine(baseDir, "flight_model.cfg");
        if (!File.Exists(fm)) return null;
        var s = Parse(fm);
        if (!double.TryParse(Get(s, "AIRPLANE_GEOMETRY", "wing_span"), NumberStyles.Float, CultureInfo.InvariantCulture, out var spanFt) || spanFt <= 0)
            return null;
        double noseFt = 0;
        var p0 = Get(s, "CONTACT_POINTS", "point.0").Split(',');
        if (p0.Length > 1) double.TryParse(p0[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out noseFt);
        // engines.cfg engine_type: 0 piston, 1 jet, 5 turboprop.
        var engineType = Get(Parse(Path.Combine(baseDir, "engines.cfg")), "GENERALENGINEDATA", "engine_type");
        bool prop = engineType is "0" or "5";
        return (spanFt * 0.3048, noseFt * 0.3048, prop);
    }

    private static List<(string name, Dictionary<string, string> kv)> Parse(string path)
    {
        var result = new List<(string, Dictionary<string, string>)>();
        if (!File.Exists(path)) return result;
        Dictionary<string, string>? current = null;
        foreach (var line in File.ReadLines(path))
        {
            var sm = Section.Match(line);
            if (sm.Success)
            {
                current = new(StringComparer.OrdinalIgnoreCase);
                result.Add((sm.Groups["name"].Value.Trim(), current));
                continue;
            }
            if (current is null) continue;
            var km = KeyValue.Match(line);
            if (km.Success) current.TryAdd(km.Groups["key"].Value, km.Groups["value"].Value.Trim().Trim('"').Trim());
        }
        return result;
    }

    private static string Get(List<(string name, Dictionary<string, string> kv)> sections, string section, string key) =>
        sections.Where(s => s.name.Equals(section, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.kv.TryGetValue(key, out var v) ? v : null)
                .FirstOrDefault(v => v is not null) ?? "";
}
