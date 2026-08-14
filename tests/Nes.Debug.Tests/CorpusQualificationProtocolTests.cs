using System.Text.Json;
using Nes.Corpus.Qualification;

namespace Nes.Debug.Tests;

public sealed class CorpusQualificationProtocolTests
{
    [Fact]
    public void Command_line_requires_complete_consistent_expected_cohort()
    {
        var parsed = QualificationCommandLine.Parse(
        [
            "--root", "private-root",
            "--server", "server.dll",
            "--expected-total", "3",
            "--expect-mapper", "0=1",
            "--expect-mapper", "4=2",
        ]);

        Assert.True(parsed.IsSuccess);
        Assert.Equal(3, parsed.Options!.Expected.Total);
        Assert.Equal(1, parsed.Options.Expected.HeaderMapperCounts[0]);
        Assert.Equal(2, parsed.Options.Expected.HeaderMapperCounts[4]);
    }

    [Theory]
    [InlineData("2", "0=1", "invalid_expected_cohort")]
    [InlineData("0", "0=0", "invalid_expected_total")]
    [InlineData("1", "0=0", "invalid_mapper_expectation")]
    [InlineData("1", "4096=1", "invalid_mapper_expectation")]
    [InlineData("1", "0=-1", "invalid_mapper_expectation")]
    public void Command_line_rejects_invalid_expected_cohort(string total, string mapper, string errorCode)
    {
        var parsed = QualificationCommandLine.Parse(
        [
            "--root", "private-root",
            "--server", "server.dll",
            "--expected-total", total,
            "--expect-mapper", mapper,
        ]);

        Assert.False(parsed.IsSuccess);
        Assert.Equal(errorCode, parsed.ErrorCode);
    }

    [Fact]
    public void Aggregate_json_is_deterministic_and_contains_only_the_public_schema()
    {
        var report = CreateReport();

        var first = AggregateJson.Serialize(report);
        var second = AggregateJson.Serialize(report);

        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(first);
        Assert.Equal(
        [
            "schemaVersion", "succeeded", "discovered", "valid", "attempted", "passed", "failed",
            "headerMappers", "skipped", "failureCategories", "totalElapsedMilliseconds",
            "maximumRomElapsedMilliseconds", "backends", "bounds", "expected",
        ],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Aggregate_schema_has_no_source_or_worker_detail_fields()
    {
        var report = CreateReport();

        var json = AggregateJson.Serialize(report);

        Assert.DoesNotContain("path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("screenshot", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stderr", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arguments", json, StringComparison.OrdinalIgnoreCase);
    }

    private static QualificationReport CreateReport() => new(
        AggregateJson.SchemaVersion,
        Succeeded: false,
        Discovered: 4,
        Valid: 3,
        Attempted: 3,
        Passed: 2,
        Failed: 1,
        HeaderMappers: [new MapperOutcome(0, 3, 2, 1)],
        Skipped: [new SkippedCount(SkippedCategory.NonNesEntry, 1)],
        FailureCategories: [new FailureCount(FailureCategory.Trace, 0, 1)],
        TotalElapsedMilliseconds: 30,
        MaximumRomElapsedMilliseconds: 20,
        Backends: [new BackendIdentity(QualificationBackend.AprNes, "backend-build", "server-build")],
        Bounds: new QualificationBounds(30, 10, 1024, 4, 10000, 128),
        Expected: new ExpectedCohort(3, new SortedDictionary<int, int> { [0] = 3 }));
}
