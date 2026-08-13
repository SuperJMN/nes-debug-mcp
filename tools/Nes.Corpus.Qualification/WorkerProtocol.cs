using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nes.Corpus.Qualification;

[JsonConverter(typeof(JsonStringEnumConverter<WorkerSourceKind>))]
internal enum WorkerSourceKind
{
    Direct,
    ZipEntry,
}

[JsonConverter(typeof(JsonStringEnumConverter<QualificationLaunchMode>))]
internal enum QualificationLaunchMode
{
    PrimaryDefault,
    Adnes,
}

internal sealed record WorkerRequest(
    int SchemaVersion,
    WorkerSourceKind SourceKind,
    string SourcePath,
    int? EntryIndex,
    long ObservedBytes,
    RomImageHeader Header,
    string StagingPath,
    string StatePath,
    string ServerAssembly,
    QualificationLaunchMode LaunchMode,
    QualificationBounds Bounds);

internal sealed record WorkerResult(
    int SchemaVersion,
    bool Passed,
    FailureCategory? FailureCategory,
    int HeaderMapper,
    long ElapsedMilliseconds,
    QualificationBackend Backend,
    string? BackendVersion,
    string? ServerVersion);

internal static class WorkerProtocol
{
    public const int SchemaVersion = 2;
    public const int MaximumResultBytes = 16 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static byte[] SerializeRequest(WorkerRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, Options);

    public static bool TryDeserializeRequest(ReadOnlySpan<byte> utf8, out WorkerRequest? request)
    {
        request = null;
        if (utf8.Length is 0 or > 64 * 1024)
        {
            return false;
        }

        try
        {
            request = JsonSerializer.Deserialize<WorkerRequest>(utf8, Options);
            return request is not null &&
                   request.SchemaVersion == SchemaVersion &&
                   Enum.IsDefined(request.SourceKind) &&
                   Enum.IsDefined(request.LaunchMode) &&
                   request.ObservedBytes >= 16 &&
                   request.EntryIndex is null or >= 0;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    public static string SerializeResult(WorkerResult result) => JsonSerializer.Serialize(result, Options);

    public static bool TryDeserializeResult(ReadOnlySpan<byte> utf8, out WorkerResult? result)
    {
        result = null;
        if (utf8.Length is 0 or > MaximumResultBytes)
        {
            return false;
        }

        try
        {
            result = JsonSerializer.Deserialize<WorkerResult>(utf8, Options);
            return result is not null &&
                   result.SchemaVersion == SchemaVersion &&
                   result.HeaderMapper >= 0 &&
                   result.ElapsedMilliseconds >= 0 &&
                   Enum.IsDefined(result.Backend) &&
                   (!result.FailureCategory.HasValue || Enum.IsDefined(result.FailureCategory.Value)) &&
                   result.Passed == !result.FailureCategory.HasValue &&
                   IsSafeVersion(result.BackendVersion) &&
                   IsSafeVersion(result.ServerVersion);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsSafeVersion(string? value) =>
        value is null || value.Length is > 0 and <= 128 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+' or '_');
}
