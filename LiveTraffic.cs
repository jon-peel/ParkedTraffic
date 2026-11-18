using System.Globalization;
using System.Text.Json;

namespace ParkedTraffic;

/// <summary>Live aircraft around a point from open ADS-B aggregators (adsb.lol, falling back to adsb.fi).</summary>
public static class LiveTraffic
{
    public sealed record Contact(string Callsign, string Type, string Registration, bool OnGround, double Lat, double Lon);

    public static async Task<List<Contact>> NearAsync(HttpClient http, double lat, double lon, int radiusNm)
    {
        string la = lat.ToString("F4", CultureInfo.InvariantCulture), lo = lon.ToString("F4", CultureInfo.InvariantCulture);
        string[] urls =
        [
            $"https://api.adsb.lol/v2/point/{la}/{lo}/{radiusNm}",
            $"https://opendata.adsb.fi/api/v2/lat/{la}/lon/{lo}/dist/{radiusNm}",
        ];
        foreach (var url in urls)
        {
            try
            {
                // Short per-request limit: this runs on the sim thread, which must not stall waiting on a slow service.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var doc = JsonDocument.Parse(await http.GetStringAsync(url, cts.Token));
                var root = doc.RootElement;
                if (!root.TryGetProperty("ac", out var list) && !root.TryGetProperty("aircraft", out list)) continue;
                var result = new List<Contact>();
                foreach (var a in list.EnumerateArray())
                {
                    string cs = Str(a, "flight").Trim();
                    string type = Str(a, "t").Trim();
                    if (cs.Length == 0 || type.Length == 0) continue;
                    bool ground = a.TryGetProperty("alt_baro", out var alt) && alt.ValueKind == JsonValueKind.String && alt.GetString() == "ground";
                    double plat = a.TryGetProperty("lat", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0;
                    double plon = a.TryGetProperty("lon", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetDouble() : 0;
                    result.Add(new Contact(cs.ToUpperInvariant(), type.ToUpperInvariant(), Str(a, "r"), ground, plat, plon));
                }
                return result;
            }
            catch (Exception e) { Log.Write($"  live traffic: {new Uri(url).Host} failed ({e.Message})"); }
        }
        return [];
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
