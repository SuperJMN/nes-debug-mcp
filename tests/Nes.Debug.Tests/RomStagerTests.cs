using System.IO.Compression;
using Nes.Corpus.Qualification;

namespace Nes.Debug.Tests;

public sealed class RomStagerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Staging_streams_one_generic_image_and_dispose_removes_it(bool archived)
    {
        using var source = new TemporarySource();
        var bytes = CreateImage();
        var candidate = archived ? source.CreateArchiveCandidate(bytes) : source.CreateDirectCandidate(bytes);

        var result = await RomStager.StageAsync(candidate, bytes.Length, TimeSpan.FromSeconds(2));

        Assert.True(result.IsSuccess);
        var staged = result.StagedRom!;
        Assert.True(File.Exists(staged.Path));
        Assert.DoesNotContain("source", System.IO.Path.GetFileName(staged.Path), StringComparison.OrdinalIgnoreCase);
        Assert.False(System.IO.Path.GetFullPath(staged.Path).StartsWith(source.Path + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(staged.Path));

        await staged.DisposeAsync();
        Assert.False(File.Exists(staged.Path));
    }

    [Fact]
    public async Task Cancelled_staging_leaves_no_partial_image()
    {
        using var source = new TemporarySource();
        var bytes = CreateImage();
        var candidate = source.CreateDirectCandidate(bytes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = CurrentStagingFiles();

        var result = await RomStager.StageAsync(candidate, RomStager.CreateGenericPath(), bytes.Length, cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCategory.StagingTimeout, result.FailureCategory);
        Assert.Equal(before, CurrentStagingFiles());
    }

    [Fact]
    public async Task Changed_source_is_rejected_and_partial_image_is_removed()
    {
        using var source = new TemporarySource();
        var candidate = source.CreateDirectCandidate(CreateImage());
        File.WriteAllBytes(((RomSource.Direct)candidate.Source).Path, [1, 2, 3]);
        var before = CurrentStagingFiles();

        var result = await RomStager.StageAsync(candidate, 64 * 1024, TimeSpan.FromSeconds(2));

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCategory.Staging, result.FailureCategory);
        Assert.Equal(before, CurrentStagingFiles());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Changed_source_length_is_rejected_and_partial_image_is_removed(bool append)
    {
        using var source = new TemporarySource();
        var bytes = CreateImage();
        var candidate = source.CreateDirectCandidate(bytes);
        var sourcePath = ((RomSource.Direct)candidate.Source).Path;
        if (append)
        {
            await using var stream = new FileStream(sourcePath, FileMode.Append, FileAccess.Write);
            await stream.WriteAsync(new byte[] { 0xAA });
        }
        else
        {
            File.WriteAllBytes(sourcePath, bytes[..^1]);
        }

        var before = CurrentStagingFiles();
        var result = await RomStager.StageAsync(candidate, 64 * 1024, TimeSpan.FromSeconds(2));

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureCategory.Staging, result.FailureCategory);
        Assert.Equal(before, CurrentStagingFiles());
    }

    private static string[] CurrentStagingFiles() => Directory
        .GetFiles(System.IO.Path.GetTempPath(), "nes-qualification-image-*.nes")
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static byte[] CreateImage()
    {
        var bytes = new byte[16 + 16 * 1024 + 8 * 1024];
        bytes[0] = (byte)'N';
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'S';
        bytes[3] = 0x1A;
        bytes[4] = 1;
        bytes[5] = 1;
        return bytes;
    }

    private sealed class TemporarySource : IDisposable
    {
        public TemporarySource()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nes-stager-source-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public RomCandidate CreateDirectCandidate(byte[] bytes)
        {
            var path = System.IO.Path.Combine(Path, "source.nes");
            File.WriteAllBytes(path, bytes);
            return Candidate(new RomSource.Direct(path), bytes.Length);
        }

        public RomCandidate CreateArchiveCandidate(byte[] bytes)
        {
            var path = System.IO.Path.Combine(Path, "source.zip");
            using (var stream = File.Create(path))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            using (var entry = archive.CreateEntry("source.nes").Open())
            {
                entry.Write(bytes);
            }

            return Candidate(new RomSource.ZipEntry(path, 0), bytes.Length);
        }

        private static RomCandidate Candidate(RomSource source, int length)
        {
            var header = new RomImageHeader(0, length, false, NesImageFormat.INes);
            return new RomCandidate(source, header, length);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
