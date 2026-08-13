using System.IO.Compression;

namespace Nes.Corpus.Qualification;

internal sealed class StagedRom : IAsyncDisposable
{
    public StagedRom(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public bool TryDelete()
    {
        try
        {
            File.Delete(Path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        _ = TryDelete();
        return ValueTask.CompletedTask;
    }
}

internal sealed record StagingResult(
    bool IsSuccess,
    StagedRom? StagedRom,
    FailureCategory? FailureCategory)
{
    public static StagingResult Success(StagedRom stagedRom) => new(true, stagedRom, null);

    public static StagingResult Failure(FailureCategory category) => new(false, null, category);
}

internal static class RomStager
{
    private const int BufferSize = 64 * 1024;

    public static async Task<StagingResult> StageAsync(
        RomCandidate candidate,
        int maximumImageBytes,
        TimeSpan timeout)
    {
        var temporaryPath = CreateGenericPath();
        using var cancellation = new CancellationTokenSource(timeout);
        return await StageAsync(candidate, temporaryPath, maximumImageBytes, cancellation.Token).ConfigureAwait(false);
    }

    internal static async Task<StagingResult> StageAsync(
        RomCandidate candidate,
        string temporaryPath,
        int maximumImageBytes,
        CancellationToken cancellationToken)
    {
        var ownershipTransferred = false;
        try
        {
            await using var source = OpenSource(candidate.Source);
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[BufferSize];
                var total = 0L;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total = checked(total + read);
                    if (total > maximumImageBytes || total > candidate.ObservedBytes)
                    {
                        return StagingResult.Failure(
                            total > maximumImageBytes ? FailureCategory.OversizeImage : FailureCategory.Staging);
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            if (!Revalidate(temporaryPath, candidate.Header, candidate.ObservedBytes, maximumImageBytes))
            {
                return StagingResult.Failure(FailureCategory.Staging);
            }

            ownershipTransferred = true;
            return StagingResult.Success(new StagedRom(temporaryPath));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return StagingResult.Failure(FailureCategory.StagingTimeout);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OverflowException)
        {
            return StagingResult.Failure(FailureCategory.Staging);
        }
        finally
        {
            if (!ownershipTransferred && File.Exists(temporaryPath))
            {
                TryDelete(temporaryPath);
            }
        }
    }

    internal static string CreateGenericPath() => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"nes-qualification-image-{Guid.NewGuid():N}.nes");

    private static Stream OpenSource(RomSource source)
    {
        if (source is RomSource.Direct direct)
        {
            return new FileStream(
                direct.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        var zipSource = (RomSource.ZipEntry)source;
        var archiveStream = new FileStream(
            zipSource.ArchivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
            if (zipSource.EntryIndex < 0 || zipSource.EntryIndex >= archive.Entries.Count)
            {
                archive.Dispose();
                throw new InvalidDataException("Archive entry is unavailable.");
            }

            return new OwnedZipEntryStream(archive, archive.Entries[zipSource.EntryIndex].Open());
        }
        catch
        {
            archiveStream.Dispose();
            throw;
        }
    }

    private static bool Revalidate(
        string path,
        RomImageHeader expected,
        long expectedBytes,
        int maximumImageBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> headerBytes = stackalloc byte[16];
        var read = stream.ReadAtLeast(headerBytes, headerBytes.Length, throwOnEndOfStream: false);
        return stream.Length == expectedBytes && RomImageHeaderParser.TryParse(
                   headerBytes[..read],
                   stream.Length,
                   maximumImageBytes,
                   out var actual,
                   out _) &&
               actual == expected;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The coordinator performs a second best-effort deletion and records TempCleanup.
        }
    }

    private sealed class OwnedZipEntryStream(ZipArchive archive, Stream entryStream) : Stream
    {
        public override bool CanRead => entryStream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => entryStream.Length;
        public override long Position
        {
            get => entryStream.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => entryStream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => entryStream.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => entryStream.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            entryStream.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                entryStream.Dispose();
                archive.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await entryStream.DisposeAsync().ConfigureAwait(false);
            archive.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
