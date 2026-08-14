using System.Text.Json.Serialization;
using Nes.Debug.Core;

namespace Nes.Debug.Mcp;

public sealed record ToolError(
    [property: JsonPropertyName("error")] DebugError Error,
    [property: JsonPropertyName("diagnostics")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ExecutionFailureDiagnostics? Diagnostics = null);

public sealed record ExecutionFailureDiagnostics(
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("backendVersion")] string BackendVersion,
    [property: JsonPropertyName("serverVersion")] string ServerVersion,
    [property: JsonPropertyName("debugCycleLimit")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? DebugCycleLimit);
