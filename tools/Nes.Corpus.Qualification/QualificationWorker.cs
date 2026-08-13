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

            if (request.Header.HasTrainer || request.Header.Format == NesImageFormat.Nes20)
            {
                // Discovery deliberately counts structurally valid images. Reject formats whose
                // offsets/extended sizes are not yet supported before any emulator is started.
                failure = FailureCategory.UnsupportedFormat;
            }
            else
            {
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
                    failure = smoke.FailureCategory;
                }
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
        TempArtifacts.AreInSamePrivateDirectory(request.StagingPath, request.StatePath) &&
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

internal sealed record TempArtifacts(string DirectoryPath, string ImagePath, string StatePath)
{
    private const string DirectoryPrefix = "nes-qualification-run-";
    private const string ImageName = "image.nes";
    private const string StateName = "state.bin";
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static TempArtifacts Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"{DirectoryPrefix}{Guid.NewGuid():N}");
        var image = Path.Combine(directory, ImageName);
        var state = Path.Combine(directory, StateName);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, PrivateDirectoryMode);
                File.SetUnixFileMode(directory, PrivateDirectoryMode);
            }

            using (CreatePrivateFile(state, asynchronous: false))
            {
            }

            return new TempArtifacts(directory, image, state);
        }
        catch
        {
            TryDelete(image);
            TryDelete(state);
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public bool TryDeleteAll()
    {
        var filesDeleted = TryDelete(ImagePath) & TryDelete(StatePath);
        return TryDeleteDirectory(DirectoryPath) & filesDeleted;
    }

    public static bool IsGenericImagePath(string path) => IsGenericFile(path, ImageName);

    public static bool IsGenericStatePath(string path) => IsGenericFile(path, StateName);

    public static bool AreInSamePrivateDirectory(string imagePath, string statePath)
    {
        try
        {
            var imageDirectory = Path.GetDirectoryName(Path.GetFullPath(imagePath));
            var stateDirectory = Path.GetDirectoryName(Path.GetFullPath(statePath));
            return imageDirectory is not null &&
                   string.Equals(imageDirectory, stateDirectory, StringComparison.Ordinal) &&
                   IsGenericDirectory(imageDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static FileStream CreatePrivateFile(string path, bool asynchronous)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 64 * 1024,
            Options = (asynchronous ? FileOptions.Asynchronous : FileOptions.None) |
                FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = PrivateFileMode;
        }

        return new FileStream(path, options);
    }

    private static bool IsGenericFile(string path, string expectedName)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            return directory is not null &&
                   string.Equals(Path.GetFileName(fullPath), expectedName, StringComparison.Ordinal) &&
                   IsGenericDirectory(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsGenericDirectory(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(fullPath), tempRoot, StringComparison.Ordinal))
        {
            return false;
        }

        var name = Path.GetFileName(fullPath);
        if (!name.StartsWith(DirectoryPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var identifier = name[DirectoryPrefix.Length..];
        return identifier.Length == 32 && identifier.All(Uri.IsHexDigit);
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

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
