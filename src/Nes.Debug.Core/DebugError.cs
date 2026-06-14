using System.Text.Json.Serialization;

namespace Nes.Debug.Core;

public sealed record DebugError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
