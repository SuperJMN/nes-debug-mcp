using System.IO.Compression;
using Nes.Corpus.Qualification;

namespace Nes.Debug.Tests;

public sealed class CorpusDiscoveryTests
{
    [Fact]
    public void Discovery_finds_direct_and_archive_images_without_extracting_entries()
    {
        using var corpus = new CorpusFixture();
        corpus.WriteDirect("direct.nes", CreateImage(mapper: 0));
        corpus.WriteArchive("nested.zip",
            ("folder/game.nes", CreateImage(mapper: 4)),
            ("notes.txt", [1, 2, 3]));

        var result = CorpusDiscovery.Discover(corpus.Path, 2 * 1024 * 1024);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal([0, 4], result.Candidates.Select(candidate => candidate.Header.HeaderMapper).Order());
        Assert.Equal(1, result.Skipped[SkippedCategory.NonNesEntry]);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(corpus.Path, "*.nes", SearchOption.AllDirectories),
            path => !path.EndsWith("direct.nes", StringComparison.Ordinal));
    }

    [Fact]
    public void Discovery_classifies_invalid_truncated_and_oversize_images()
    {
        using var corpus = new CorpusFixture();
        corpus.WriteDirect("bad.nes", [1, 2, 3]);
        corpus.WriteDirect("truncated.nes", CreateImage(mapper: 0, declaredPrgBanks: 2, actualPrgBanks: 1));
        corpus.WriteDirect("large.nes", CreateImage(mapper: 0, declaredPrgBanks: 3, actualPrgBanks: 3));

        var result = CorpusDiscovery.Discover(corpus.Path, 42 * 1024);

        Assert.Empty(result.Candidates);
        Assert.Equal(2, result.Skipped[SkippedCategory.InvalidImage]);
        Assert.Equal(1, result.Skipped[SkippedCategory.OversizeImage]);
    }

    [Fact]
    public void Discovery_accepts_trainer_and_nes20_images()
    {
        using var corpus = new CorpusFixture();
        corpus.WriteDirect("trainer.nes", CreateImage(mapper: 1, trainer: true));
        corpus.WriteDirect("nes20.nes", CreateImage(mapper: 257, nes20: true));

        var result = CorpusDiscovery.Discover(corpus.Path, 2 * 1024 * 1024);

        Assert.Equal(2, result.Candidates.Count);
        var trainer = Assert.Single(result.Candidates, candidate => candidate.Header.HasTrainer);
        Assert.Equal(NesImageFormat.INes, trainer.Header.Format);
        var nes20 = Assert.Single(result.Candidates, candidate => candidate.Header.Format == NesImageFormat.Nes20);
        Assert.Equal(257, nes20.Header.HeaderMapper);
    }

    [Fact]
    public void Discovery_classifies_corrupt_archives_without_crashing()
    {
        using var corpus = new CorpusFixture();
        corpus.WriteDirect("broken.zip", [0x50, 0x4B, 0x03]);

        var result = CorpusDiscovery.Discover(corpus.Path, 2 * 1024 * 1024);

        Assert.Empty(result.Candidates);
        Assert.Equal(1, result.Skipped[SkippedCategory.InvalidImage]);
    }

    [Fact]
    public void Discovery_classifies_an_oversize_archive_entry_without_extracting_it()
    {
        using var corpus = new CorpusFixture();
        corpus.WriteArchive(
            "oversize.zip",
            ("oversize.nes", CreateImage(mapper: 4, declaredPrgBanks: 3, actualPrgBanks: 3)));

        var result = CorpusDiscovery.Discover(corpus.Path, 42 * 1024);

        Assert.Empty(result.Candidates);
        Assert.Equal(1, result.Skipped[SkippedCategory.OversizeImage]);
        Assert.Empty(Directory.EnumerateFiles(corpus.Path, "*.nes", SearchOption.AllDirectories));
    }

    [Fact]
    public void Invalid_archive_entry_does_not_hide_a_later_valid_entry()
    {
        using var corpus = new CorpusFixture();
        corpus.WriteArchive("mixed.zip",
            ("first.nes", [1, 2, 3]),
            ("second.nes", CreateImage(mapper: 3)));

        var result = CorpusDiscovery.Discover(corpus.Path, 2 * 1024 * 1024);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(3, candidate.Header.HeaderMapper);
        Assert.Equal(1, result.Skipped[SkippedCategory.InvalidImage]);
    }

    [Fact]
    public void Discovery_does_not_modify_source_files()
    {
        using var corpus = new CorpusFixture();
        corpus.WriteDirect("direct.nes", CreateImage(mapper: 0));
        corpus.WriteArchive("images.zip", ("inside.nes", CreateImage(mapper: 2)));
        var before = corpus.Snapshot();

        _ = CorpusDiscovery.Discover(corpus.Path, 2 * 1024 * 1024);

        Assert.Equal(before, corpus.Snapshot());
    }

    private static byte[] CreateImage(
        int mapper,
        bool trainer = false,
        bool nes20 = false,
        int declaredPrgBanks = 1,
        int actualPrgBanks = 1)
    {
        var header = new byte[16];
        header[0] = (byte)'N';
        header[1] = (byte)'E';
        header[2] = (byte)'S';
        header[3] = 0x1A;
        header[4] = (byte)declaredPrgBanks;
        header[5] = 1;
        header[6] = (byte)((mapper & 0x0F) << 4);
        if (trainer)
        {
            header[6] |= 0x04;
        }

        header[7] = (byte)(mapper & 0xF0);
        if (nes20)
        {
            header[7] = (byte)((header[7] & 0xF0) | 0x08);
            header[8] = (byte)((mapper >> 8) & 0x0F);
        }

        return [
            .. header,
            .. new byte[trainer ? 512 : 0],
            .. new byte[actualPrgBanks * 16 * 1024],
            .. new byte[8 * 1024],
        ];
    }

    private sealed class CorpusFixture : IDisposable
    {
        public CorpusFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nes-corpus-fixture-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void WriteDirect(string relativePath, byte[] bytes) =>
            File.WriteAllBytes(System.IO.Path.Combine(Path, relativePath), bytes);

        public void WriteArchive(string relativePath, params (string Name, byte[] Bytes)[] entries)
        {
            using var stream = File.Create(System.IO.Path.Combine(Path, relativePath));
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var (name, bytes) in entries)
            {
                using var target = archive.CreateEntry(name).Open();
                target.Write(bytes);
            }
        }

        public IReadOnlyList<SourceSnapshot> Snapshot() => Directory
            .EnumerateFiles(Path, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => new FileInfo(path))
            .Select(file => new SourceSnapshot(
                System.IO.Path.GetRelativePath(Path, file.FullName),
                file.Length,
                file.LastWriteTimeUtc,
                Convert.ToHexString(File.ReadAllBytes(file.FullName))))
            .ToArray();

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed record SourceSnapshot(string RelativePath, long Length, DateTime LastWriteTimeUtc, string SyntheticBytes);
}
