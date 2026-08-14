using System.IO.Compression;
using Nes.Corpus.Qualification;

namespace Nes.Debug.Tests;

[Collection(NesDebugSessionCollection.Name)]
public sealed class QualificationCoordinatorTests
{
    [Fact]
    public async Task Generated_mapper_zero_through_three_corpus_passes_aprnes_and_stays_immutable()
    {
        using var corpus = new TemporaryCorpus();
        var program = CreateObservableLoop();
        corpus.WriteDirect("one.nes", NromTestRomBuilder.CreateProgram(program).Bytes);
        corpus.WriteArchive(
            "three.zip",
            ("two.nes", Mmc1TestRomBuilder.CreateContractProgram(program, 2, 1, NromMirroring.Horizontal).Bytes),
            ("three.nes", UxromTestRomBuilder.CreateConformanceProgram(program).Bytes),
            ("four.nes", CnromTestRomBuilder.CreateProgram(program).Bytes));
        var before = corpus.Snapshot();
        var options = Options(corpus.Path, 4, new SortedDictionary<int, int>
        {
            [0] = 1,
            [1] = 1,
            [2] = 1,
            [3] = 1,
        });

        var run = await QualificationCoordinator.RunAsync(options, CancellationToken.None);

        Assert.True(run.Succeeded, AggregateJson.Serialize(run.Report));
        Assert.True(run.Report.Succeeded);
        Assert.Equal(4, run.Report.Valid);
        Assert.Equal(4, run.Report.Attempted);
        Assert.Equal(4, run.Report.Passed);
        Assert.Equal(0, run.Report.Failed);
        Assert.All(run.Report.HeaderMappers, outcome => Assert.Equal((1, 1, 0), (outcome.Attempted, outcome.Passed, outcome.Failed)));
        Assert.Empty(run.Report.FailureCategories);
        Assert.Equal(before, corpus.Snapshot());

        var json = AggregateJson.Serialize(run.Report);
        Assert.DoesNotContain("one.nes", json, StringComparison.Ordinal);
        Assert.DoesNotContain("three.zip", json, StringComparison.Ordinal);
        Assert.DoesNotContain(corpus.Path, json, StringComparison.Ordinal);
        Assert.DoesNotContain("title", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("screenshot", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_expected_cohort_fails_closed()
    {
        using var corpus = new TemporaryCorpus();
        var options = Options(corpus.Path, 1, new SortedDictionary<int, int> { [0] = 1 });

        var run = await QualificationCoordinator.RunAsync(options, CancellationToken.None);

        Assert.False(run.Succeeded);
        Assert.False(run.Report.Succeeded);
        Assert.Equal(0, run.Report.Valid);
        Assert.Contains(run.Report.FailureCategories, failure =>
            failure.Category == FailureCategory.MissingCoverage && failure.HeaderMapper == 0);
    }

    [Fact]
    public async Task Empty_zero_expected_cohort_cannot_succeed_without_running_aprnes()
    {
        using var corpus = new TemporaryCorpus();
        var options = Options(corpus.Path, 0, new SortedDictionary<int, int> { [0] = 0 });

        var run = await QualificationCoordinator.RunAsync(options, CancellationToken.None);

        Assert.False(run.Succeeded);
        Assert.False(run.Report.Succeeded);
        Assert.Equal(0, run.Report.Attempted);
        Assert.Contains(run.Report.FailureCategories, failure =>
            failure.Category == FailureCategory.MissingCoverage);
        Assert.All(run.Report.Backends, identity =>
        {
            Assert.Equal("unavailable", identity.BackendVersion);
            Assert.Equal("unavailable", identity.ServerVersion);
        });
    }

    [Fact]
    public async Task Complete_single_mapper_cohort_needs_no_separate_backend_smoke()
    {
        using var corpus = new TemporaryCorpus();
        corpus.WriteDirect("only.nes", NromTestRomBuilder.CreateProgram(CreateObservableLoop()).Bytes);
        var options = Options(corpus.Path, 1, new SortedDictionary<int, int> { [0] = 1 });

        var run = await QualificationCoordinator.RunAsync(options, CancellationToken.None);

        Assert.True(run.Succeeded, AggregateJson.Serialize(run.Report));
        Assert.True(run.Report.Succeeded);
        Assert.Equal((1, 1, 0), (run.Report.Attempted, run.Report.Passed, run.Report.Failed));
        Assert.Empty(run.Report.FailureCategories);
    }

    [Fact]
    public async Task Trainer_and_nes20_candidates_are_attempted_and_fail_as_unsupported_without_being_skipped()
    {
        using var corpus = new TemporaryCorpus();
        corpus.WriteDirect("trainer.nes", CreateUnsupportedImage(trainer: true, nes20: false));
        corpus.WriteDirect("nes20.nes", CreateUnsupportedImage(trainer: false, nes20: true));
        var options = Options(corpus.Path, 2, new SortedDictionary<int, int> { [4] = 2 }) with
        {
            // If the format guard launched MCP, this existing non-assembly would crash it.
            ServerAssembly = System.IO.Path.Combine(corpus.Path, "trainer.nes"),
        };

        var run = await QualificationCoordinator.RunAsync(options, CancellationToken.None);

        Assert.False(run.Succeeded);
        Assert.False(run.Report.Succeeded);
        Assert.Equal(2, run.Report.Valid);
        Assert.Equal(2, run.Report.Attempted);
        Assert.Equal(0, run.Report.Passed);
        Assert.Equal(2, run.Report.Failed);
        Assert.Empty(run.Report.Skipped);
        Assert.Contains(run.Report.FailureCategories, failure =>
            failure is { Category: FailureCategory.UnsupportedFormat, HeaderMapper: 4, Count: 2 });
    }

    [Fact]
    public void Parent_owned_generic_artifacts_are_deleted_without_exposing_their_paths()
    {
        var artifacts = TempArtifacts.Create();
        using (var image = TempArtifacts.CreatePrivateFile(artifacts.ImagePath, asynchronous: false))
        {
            image.WriteByte(1);
        }

        File.WriteAllBytes(artifacts.StatePath, [2]);

        Assert.True(Directory.Exists(artifacts.DirectoryPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(artifacts.DirectoryPath));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(artifacts.ImagePath));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(artifacts.StatePath));
        }

        var deleted = artifacts.TryDeleteAll();

        Assert.True(deleted);
        Assert.False(File.Exists(artifacts.ImagePath));
        Assert.False(File.Exists(artifacts.StatePath));
        Assert.False(Directory.Exists(artifacts.DirectoryPath));
    }

    private static QualificationOptions Options(
        string root,
        int expectedTotal,
        SortedDictionary<int, int> expectedMappers) => new(
            root,
            FindServerAssembly(),
            new QualificationBounds(20, 5, 2 * 1024 * 1024, 1, 100, 16),
            new ExpectedCohort(expectedTotal, expectedMappers));

    private static byte[] CreateObservableLoop()
    {
        var program = new List<byte>();
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2000, 0x80));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2005, 0x01));
        program.AddRange([0xE6, 0x00, 0x4C, 0x00, 0x80]);
        return program.ToArray();
    }

    private static byte[] CreateUnsupportedImage(bool trainer, bool nes20)
    {
        var bytes = new byte[16 + (trainer ? 512 : 0) + (16 * 1024) + (8 * 1024)];
        bytes[0] = (byte)'N';
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'S';
        bytes[3] = 0x1A;
        bytes[4] = 1;
        bytes[5] = 1;
        bytes[6] = (byte)(0x40 | (trainer ? 0x04 : 0));
        bytes[7] = nes20 ? (byte)0x08 : (byte)0;
        return bytes;
    }

    private static string FindServerAssembly()
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new DirectoryNotFoundException("Could not determine test build configuration.");
        return Path.Combine(
            repositoryRoot,
            "src",
            "Nes.Debug.Mcp",
            "bin",
            configuration,
            "net10.0",
            "Nes.Mcp.dll");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "nes-debug-mcp.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class TemporaryCorpus : IDisposable
    {
        public TemporaryCorpus()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nes-qualification-corpus-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void WriteDirect(string name, byte[] bytes) => File.WriteAllBytes(System.IO.Path.Combine(Path, name), bytes);

        public void WriteArchive(string name, params (string EntryName, byte[] Bytes)[] entries)
        {
            using var stream = File.Create(System.IO.Path.Combine(Path, name));
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var entry in entries)
            {
                using var target = archive.CreateEntry(entry.EntryName).Open();
                target.Write(entry.Bytes);
            }
        }

        public SourceSnapshot[] Snapshot() => Directory
            .EnumerateFiles(Path)
            .Order(StringComparer.Ordinal)
            .Select(path => new FileInfo(path))
            .Select(file => new SourceSnapshot(
                System.IO.Path.GetFileName(file.FullName),
                file.Length,
                file.LastWriteTimeUtc,
                Convert.ToHexString(File.ReadAllBytes(file.FullName))))
            .ToArray();

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed record SourceSnapshot(string SyntheticName, long Length, DateTime LastWriteTimeUtc, string SyntheticBytes);
}
