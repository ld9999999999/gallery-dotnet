using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gallery.Develop;

/// <summary>Source-generated JSON for the header and settings, so the WebAssembly build can do without reflection.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DevelopHeader))]
[JsonSerializable(typeof(DevelopSettings))]
public sealed partial class DevelopJson : JsonSerializerContext;
