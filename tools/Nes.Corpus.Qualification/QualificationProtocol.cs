using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nes.Corpus.Qualification;

[JsonConverter(typeof(JsonStringEnumConverter<QualificationBackend>))]
public enum QualificationBackend
{
    AprNes,
    Adnes,
}

[JsonConverter(typeof(JsonStringEnumConverter<FailureCategory>))]
public enum FailureCategory
{
    InvalidImage,
    UnsupportedFormat,
    OversizeImage,
    Staging,
    StagingTimeout,
    TempCleanup,
    WorkerTimeout,
    WorkerCrash,
    ProtocolViolation,
    Load,
    Identity,
    Reset,
    FrameExecution,
    InstructionExecution,
    ControllerInput,
    CpuInspection,
    PpuInspection,
    ScreenCapture,
    Trace,
    TraceStop,
    TraceEventBounds,
    TraceInstructionBounds,
    TraceBackendError,
    TraceResponseBounds,
    TraceProtocol,
    McpRequestWrite,
    McpServerCrash,
    McpResponseEof,
    McpResponseInvalidJson,
    McpResponseUnexpectedId,
    McpResponseContent,
    McpStdoutOverflow,
    McpShutdown,
    SaveStateReplay,
    IndependentSmoke,
    MissingCoverage,
}

[JsonConverter(typeof(JsonStringEnumConverter<SkippedCategory>))]
public enum SkippedCategory
{
    NonNesEntry,
    InvalidImage,
    OversizeImage,
}

public sealed record QualificationBounds(
    int WallTimeoutSeconds,
    int StagingTimeoutSeconds,
    int MaxImageBytes,
    int MaxFrames,
    int MaxInstructions,
    int MaxTraceEvents,
    int WorkflowFrameOperationCount = 6,
    int WorkflowInstructionOperationCount = 2,
    int TraceInstructionLimitPerFrame = 100_000);

public sealed record ExpectedCohort(
    int Total,
    IReadOnlyDictionary<int, int> HeaderMapperCounts);

public sealed record MapperOutcome(
    int HeaderMapper,
    int Attempted,
    int Passed,
    int Failed);

public sealed record FailureCount(
    FailureCategory Category,
    int? HeaderMapper,
    int Count);

public sealed record SkippedCount(
    SkippedCategory Category,
    int Count);

public sealed record BackendIdentity(
    QualificationBackend Backend,
    string BackendVersion,
    string ServerVersion);

public sealed record IndependentSmokeCoverage(
    QualificationBackend Backend,
    string BackendVersion,
    string ServerVersion,
    int Attempted,
    int Passed,
    int Failed,
    IReadOnlyList<MapperOutcome> HeaderMappers);

public sealed record QualificationReport(
    int SchemaVersion,
    bool Succeeded,
    int Discovered,
    int Valid,
    int Attempted,
    int Passed,
    int Failed,
    IReadOnlyList<MapperOutcome> HeaderMappers,
    IReadOnlyList<SkippedCount> Skipped,
    IReadOnlyList<FailureCount> FailureCategories,
    long TotalElapsedMilliseconds,
    long MaximumRomElapsedMilliseconds,
    IReadOnlyList<BackendIdentity> Backends,
    QualificationBounds Bounds,
    ExpectedCohort Expected,
    IndependentSmokeCoverage IndependentSmoke);

public static class AggregateJson
{
    public const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string Serialize(QualificationReport report) => JsonSerializer.Serialize(report, Options);

    public static QualificationReport? Deserialize(string json) =>
        JsonSerializer.Deserialize<QualificationReport>(json, Options);
}
