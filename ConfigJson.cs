using System.Text.Json.Serialization;

namespace ParkedTraffic;

/// <summary>
/// Compile-time JSON code for the config files, so they keep working in the trimmed exe
/// (reflection-based System.Text.Json is not trim-safe).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AirportConfig))]
[JsonSerializable(typeof(GlobalConfig))]
internal partial class ConfigJson : JsonSerializerContext;
