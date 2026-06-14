using System.Text.Json.Serialization;
using Nes.Debug.Core;

namespace Nes.Debug.Mcp;

public sealed record ToolError([property: JsonPropertyName("error")] DebugError Error);
