using System.IO.Compression;
using Nes.Corpus.Qualification;

namespace Nes.Debug.Tests;

[Collection(NesDebugSessionCollection.Name)]
public sealed class QualificationCoordinatorTests
{
    [Fact]
    public async Task Generated_mapper_zero_through_three_corpus_passes_both_backends_and_stays_immutable()
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
        Assert.Equal(4, run.Report.Valid);
        Assert.Equal(4, run.Report.Attempted);
        Assert.Equal(4, run.Report.Passed);
        Assert.Equal(0, run.Report.Failed);
        Assert.All(run.Report.HeaderMappers, outcome => Assert.Equal((1, 1, 0), (outcome.Attempted, outcome.Passed, outcome.Failed)));
        Assert.Equal(4, run.Report.IndependentSmoke.Attempted);
        Assert.Equal(4, run.Report.IndependentSmoke.Passed);
        Assert.Equal(0, run.Report.IndependentSmoke.Failed);
        Assert.All(run.Report.IndependentSmoke.HeaderMappers, outcome => Assert.Equal((1, 1, 0), (outcome.Attempted, outcome.Passed, outcome.Failed)));
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
    public async Task Missing_expected_cohort_and_independent_representatives_fail_closed()
    {
        using var corpus = new TemporaryCorpus();
        var options = Options(corpus.Path, 1, new SortedDictionary<int, int> { [0] = 1 });

        var run = await QualificationCoordinator.RunAsync(options, CancellationToken.None);

        Assert.False(run.Succeeded);
        Assert.Equal(0, run.Report.Valid);
        Assert.Contains(run.Report.FailureCategories, failure =>
            failure.Category == FailureCategory.MissingCoverage && failure.HeaderMapper == 0);
        Assert.Equal([0, 1, 2, 3], run.Report.IndependentSmoke.HeaderMappers.Select(item => item.HeaderMapper));
        Assert.All(run.Report.IndependentSmoke.HeaderMappers, outcome => Assert.Equal(0, outcome.Attempted));
    }

    [Fact]
    public void Parent_owned_generic_artifacts_are_deleted_without_exposing_their_paths()
    {
        var artifacts = TempArtifacts.Create();
        File.WriteAllBytes(artifacts.ImagePath, [1]);
        File.WriteAllBytes(artifacts.StatePath, [2]);

        var deleted = artifacts.TryDeleteAll();

        Assert.True(deleted);
        Assert.False(File.Exists(artifacts.ImagePath));
        Assert.False(File.Exists(artifacts.StatePath));
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
