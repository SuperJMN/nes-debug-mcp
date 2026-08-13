using System.Diagnostics;

namespace Nes.Corpus.Qualification;

internal static class QualificationWorker
{
    private const int MaximumRequestBytes = 64 * 1024;

    public static async Task<WorkerResult> RunAsync(Stream standardInput)
    {
        var stopwatch = Stopwatch.StartNew();
        WorkerRequest? request = null;
        SmokeResult? smoke = null;
        FailureCategory? failure = null;
        try
        {
            var requestBytes = await ReadBoundedAsync(standardInput, MaximumRequestBytes).ConfigureAwait(false);
            if (requestBytes is null ||
                !WorkerProtocol.TryDeserializeRequest(requestBytes, out request) ||
                request is null ||
                !ValidateRequest(request))
            {
                return ClosedFailure(FailureCategory.ProtocolViolation, stopwatch);
            }

            var candidate = new RomCandidate(ToSource(request), request.Header, request.ObservedBytes);
            using var stagingCancellation = new CancellationTokenSource(
                TimeSpan.FromSeconds(request.Bounds.StagingTimeoutSeconds));
            var staging = await RomStager.StageAsync(
                candidate,
                request.StagingPath,
                request.Bounds.MaxImageBytes,
                stagingCancellation.Token).ConfigureAwait(false);
            if (!staging.IsSuccess)
            {
                failure = staging.FailureCategory ?? FailureCategory.Staging;
            }
            else
            {
                smoke = await McpQualificationSmoke.RunAsync(
                    request.ServerAssembly,
                    staging.StagedRom!.Path,
                    request.StatePath,
                    request.Header.HeaderMapper,
                    request.Backend,
                    request.Bounds,
                    CancellationToken.None).ConfigureAwait(false);
                failure = MapValidFormatFailure(request.Header, smoke.FailureCategory);
            }
        }
        catch (OperationCanceledException)
        {
            failure = FailureCategory.WorkerTimeout;
        }
        catch
        {
            failure = FailureCategory.WorkerCrash;
        }

        var cleanupSucceeded = request is null ||
            TryDelete(request.StagingPath) & TryDelete(request.StatePath);
        if (!cleanupSucceeded)
        {
            failure = FailureCategory.TempCleanup;
        }

        stopwatch.Stop();
        var passed = !failure.HasValue;
        return new WorkerResult(
            WorkerProtocol.SchemaVersion,
            passed,
            failure,
            request?.Header.HeaderMapper ?? 0,
            stopwatch.ElapsedMilliseconds,
            request?.Backend ?? QualificationBackend.AprNes,
            smoke?.BackendVersion,
            smoke?.ServerVersion);
    }

    private static FailureCategory? MapValidFormatFailure(RomImageHeader header, FailureCategory? failure) =>
        failure.HasValue &&
        failure.Value is FailureCategory.Load or FailureCategory.Identity &&
        (header.HasTrainer || header.Format == NesImageFormat.Nes20)
            ? FailureCategory.UnsupportedFormat
            : failure;

    private static WorkerResult ClosedFailure(FailureCategory category, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return new WorkerResult(
            WorkerProtocol.SchemaVersion,
            false,
            category,
            0,
            stopwatch.ElapsedMilliseconds,
            QualificationBackend.AprNes,
            null,
            null);
    }

    private static bool ValidateRequest(WorkerRequest request) =>
        request.ObservedBytes >= request.Header.RequiredBytes &&
        request.ObservedBytes <= request.Bounds.MaxImageBytes &&
        request.Header.HeaderMapper is >= 0 and <= 0x0FFF &&
        Enum.IsDefined(request.Header.Format) &&
        Enum.IsDefined(request.Backend) &&
        File.Exists(request.ServerAssembly) &&
        TempArtifacts.IsGenericImagePath(request.StagingPath) &&
        TempArtifacts.IsGenericStatePath(request.StatePath) &&
        request.StagingPath != request.StatePath &&
        request.Bounds is
        {
            WallTimeoutSeconds: > 0,
            StagingTimeoutSeconds: > 0,
            MaxImageBytes: > 0,
            MaxFrames: > 0 and <= 600,
            MaxInstructions: > 0 and <= 10_000_000,
            MaxTraceEvents: > 0 and <= 10_000,
            WorkflowFrameOperationCount: 6,
            WorkflowInstructionOperationCount: 2,
            TraceInstructionLimitPerFrame: 100_000,
        } &&
        request.Bounds.StagingTimeoutSeconds <= request.Bounds.WallTimeoutSeconds &&
        request.SourceKind switch
        {
            WorkerSourceKind.Direct => request.EntryIndex is null,
            WorkerSourceKind.ZipEntry => request.EntryIndex is >= 0,
            _ => false,
        };

    private static RomSource ToSource(WorkerRequest request) => request.SourceKind switch
    {
        WorkerSourceKind.Direct => new RomSource.Direct(request.SourcePath),
        WorkerSourceKind.ZipEntry => new RomSource.ZipEntry(request.SourcePath, request.EntryIndex!.Value),
        _ => throw new InvalidOperationException(),
    };

    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int maximumBytes)
    {
        using var retained = new MemoryStream(Math.Min(maximumBytes, 4096));
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return retained.ToArray();
            }

            if (retained.Length + read > maximumBytes)
            {
                return null;
            }

            retained.Write(buffer, 0, read);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

internal sealed record TempArtifacts(string ImagePath, string StatePath)
{
    private const string ImagePrefix = "nes-qualification-image-";
    private const string StatePrefix = "nes-qualification-state-";

    public static TempArtifacts Create() => new(
        RomStager.CreateGenericPath(),
        Path.Combine(Path.GetTempPath(), $"{StatePrefix}{Guid.NewGuid():N}.state"));

    public bool TryDeleteAll() => TryDelete(ImagePath) & TryDelete(StatePath);

    public static bool IsGenericImagePath(string path) => IsGeneric(path, ImagePrefix, ".nes");

    public static bool IsGenericStatePath(string path) => IsGeneric(path, StatePrefix, ".state");

    private static bool IsGeneric(string path, string prefix, string suffix)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(fullPath), tempRoot, StringComparison.Ordinal))
            {
                return false;
            }

            var fileName = Path.GetFileName(fullPath);
            if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
                !fileName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }

            var identifier = fileName[prefix.Length..^suffix.Length];
            return identifier.Length == 32 && identifier.All(Uri.IsHexDigit);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
