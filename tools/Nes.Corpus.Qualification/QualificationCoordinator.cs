using System.Diagnostics;
using System.Reflection;

namespace Nes.Corpus.Qualification;

internal sealed record QualificationRun(QualificationReport Report, bool Succeeded);

internal sealed record WorkerExecution(WorkerResult Result, long WallElapsedMilliseconds);

internal static class QualificationCoordinator
{
    private static readonly int[] IndependentMappers = [0, 1, 2, 3];

    public static async Task<QualificationRun> RunAsync(
        QualificationOptions options,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        if (!Directory.Exists(options.CorpusRoot) || !File.Exists(options.ServerAssembly))
        {
            return CreateClosedFailure(options);
        }

        var discovery = CorpusDiscovery.Discover(options.CorpusRoot, options.Bounds.MaxImageBytes);
        var failures = new FailureAccumulator();
        AddCohortFailures(discovery.Candidates, options.Expected, failures);

        var mapperCounters = discovery.Candidates
            .Select(candidate => candidate.Header.HeaderMapper)
            .Distinct()
            .ToDictionary(mapper => mapper, mapper => new MutableMapperOutcome(mapper));
        var aprNesIdentities = new HashSet<BackendIdentity>();
        var maximumElapsed = 0L;
        foreach (var candidate in discovery.Candidates)
        {
            var execution = await RunWorkerAsync(
                candidate,
                options,
                QualificationBackend.AprNes,
                cancellationToken).ConfigureAwait(false);
            maximumElapsed = Math.Max(maximumElapsed, execution.WallElapsedMilliseconds);
            var counter = mapperCounters[candidate.Header.HeaderMapper];
            counter.Attempted++;
            if (execution.Result.Passed)
            {
                counter.Passed++;
                if (execution.Result.BackendVersion is not null && execution.Result.ServerVersion is not null)
                {
                    aprNesIdentities.Add(new BackendIdentity(
                        QualificationBackend.AprNes,
                        execution.Result.BackendVersion,
                        execution.Result.ServerVersion));
                }
            }
            else
            {
                counter.Failed++;
                failures.Add(execution.Result.FailureCategory ?? FailureCategory.WorkerCrash, candidate.Header.HeaderMapper);
            }
        }

        if (aprNesIdentities.Count > 1)
        {
            failures.Add(FailureCategory.ProtocolViolation, null);
        }

        var independentCounters = IndependentMappers.ToDictionary(
            mapper => mapper,
            mapper => new MutableMapperOutcome(mapper));
        string? adnesBackendVersion = null;
        string? adnesServerVersion = null;
        foreach (var mapper in IndependentMappers)
        {
            var candidate = SelectIndependentCandidate(discovery.Candidates, mapper);
            if (candidate is null)
            {
                failures.Add(FailureCategory.MissingCoverage, mapper);
                continue;
            }

            var execution = await RunWorkerAsync(
                candidate,
                options,
                QualificationBackend.Adnes,
                cancellationToken).ConfigureAwait(false);
            maximumElapsed = Math.Max(maximumElapsed, execution.WallElapsedMilliseconds);
            var counter = independentCounters[mapper];
            counter.Attempted++;
            if (execution.Result.Passed)
            {
                if (adnesBackendVersion is not null &&
                    (adnesBackendVersion != execution.Result.BackendVersion ||
                     adnesServerVersion != execution.Result.ServerVersion))
                {
                    counter.Failed++;
                    failures.Add(FailureCategory.ProtocolViolation, mapper);
                }
                else
                {
                    counter.Passed++;
                    adnesBackendVersion = execution.Result.BackendVersion;
                    adnesServerVersion = execution.Result.ServerVersion;
                }
            }
            else
            {
                counter.Failed++;
                failures.Add(execution.Result.FailureCategory ?? FailureCategory.IndependentSmoke, mapper);
            }
        }

        totalStopwatch.Stop();
        var mapperOutcomes = mapperCounters.Values
            .OrderBy(counter => counter.HeaderMapper)
            .Select(counter => counter.ToImmutable())
            .ToArray();
        var independentOutcomes = independentCounters.Values
            .OrderBy(counter => counter.HeaderMapper)
            .Select(counter => counter.ToImmutable())
            .ToArray();
        var attempted = mapperOutcomes.Sum(outcome => outcome.Attempted);
        var passed = mapperOutcomes.Sum(outcome => outcome.Passed);
        var failed = mapperOutcomes.Sum(outcome => outcome.Failed);
        if (aprNesIdentities.Count == 0)
        {
            aprNesIdentities.Add(new BackendIdentity(QualificationBackend.AprNes, "unavailable", "unavailable"));
        }

        var succeeded = failed == 0 && failures.Count == 0 &&
            independentOutcomes.All(outcome => outcome is { Attempted: 1, Passed: 1, Failed: 0 });
        var report = new QualificationReport(
            AggregateJson.SchemaVersion,
            succeeded,
            discovery.Discovered,
            discovery.Candidates.Count,
            attempted,
            passed,
            failed,
            mapperOutcomes,
            discovery.Skipped
                .OrderBy(item => item.Key)
                .Select(item => new SkippedCount(item.Key, item.Value))
                .ToArray(),
            failures.ToImmutable(),
            totalStopwatch.ElapsedMilliseconds,
            maximumElapsed,
            aprNesIdentities
                .OrderBy(identity => identity.BackendVersion, StringComparer.Ordinal)
                .ThenBy(identity => identity.ServerVersion, StringComparer.Ordinal)
                .ToArray(),
            options.Bounds,
            options.Expected,
            new IndependentSmokeCoverage(
                QualificationBackend.Adnes,
                adnesBackendVersion ?? "unavailable",
                adnesServerVersion ?? "unavailable",
                independentOutcomes.Sum(outcome => outcome.Attempted),
                independentOutcomes.Sum(outcome => outcome.Passed),
                independentOutcomes.Sum(outcome => outcome.Failed),
                independentOutcomes));
        return new QualificationRun(report, succeeded);
    }

    public static QualificationRun CreateClosedFailure(QualificationOptions options)
    {
        var independent = IndependentMappers.Select(mapper => new MapperOutcome(mapper, 0, 0, 0)).ToArray();
        var report = new QualificationReport(
            AggregateJson.SchemaVersion,
            false,
            0,
            0,
            0,
            0,
            0,
            [],
            [],
            [new FailureCount(FailureCategory.MissingCoverage, null, Math.Max(1, options.Expected.Total))],
            0,
            0,
            [new BackendIdentity(QualificationBackend.AprNes, "unavailable", "unavailable")],
            options.Bounds,
            options.Expected,
            new IndependentSmokeCoverage(
                QualificationBackend.Adnes,
                "unavailable",
                "unavailable",
                0,
                0,
                0,
                independent));
        return new QualificationRun(report, false);
    }

    internal static RomCandidate? SelectIndependentCandidate(
        IReadOnlyList<RomCandidate> candidates,
        int headerMapper) => candidates.FirstOrDefault(candidate =>
            candidate.Header.HeaderMapper == headerMapper &&
            !candidate.Header.HasTrainer &&
            candidate.Header.Format == NesImageFormat.INes);

    private static async Task<WorkerExecution> RunWorkerAsync(
        RomCandidate candidate,
        QualificationOptions options,
        QualificationBackend backend,
        CancellationToken cancellationToken)
    {
        var artifacts = TempArtifacts.Create();
        WorkerResult workerResult = FailureResult(
            candidate.Header.HeaderMapper,
            backend,
            FailureCategory.WorkerCrash,
            0);
        long elapsed = 0;
        var cleanupSucceeded = false;
        try
        {
            var request = new WorkerRequest(
                candidate.Source is RomSource.Direct ? WorkerSourceKind.Direct : WorkerSourceKind.ZipEntry,
                candidate.Source.ContainerPath,
                candidate.Source is RomSource.ZipEntry zip ? zip.EntryIndex : null,
                candidate.ObservedBytes,
                candidate.Header,
                artifacts.ImagePath,
                artifacts.StatePath,
                options.ServerAssembly,
                backend,
                options.Bounds);
            var process = await BoundedProcessRunner.RunAsync(
                CreateWorkerStartInfo(),
                TimeSpan.FromSeconds(options.Bounds.WallTimeoutSeconds),
                WorkerProtocol.MaximumResultBytes,
                WorkerProtocol.SerializeRequest(request),
                cancellationToken).ConfigureAwait(false);
            elapsed = process.ElapsedMilliseconds;
            workerResult = InterpretWorkerResult(process, candidate.Header.HeaderMapper, backend);
        }
        catch
        {
            workerResult = FailureResult(
                candidate.Header.HeaderMapper,
                backend,
                FailureCategory.WorkerCrash,
                elapsed);
        }
        finally
        {
            // A killed worker cannot run its finally block, so the parent always owns final cleanup.
            cleanupSucceeded = artifacts.TryDeleteAll();
        }

        if (!cleanupSucceeded)
        {
            workerResult = workerResult with
            {
                Passed = false,
                FailureCategory = FailureCategory.TempCleanup,
                BackendVersion = null,
                ServerVersion = null,
            };
        }

        return new WorkerExecution(workerResult, elapsed);
    }

    internal static WorkerResult InterpretWorkerResult(
        BoundedProcessResult process,
        int headerMapper,
        QualificationBackend backend)
    {
        var category = process.CleanupTimedOut
            ? FailureCategory.WorkerTimeout
            : process.StandardOutputOverflow
            ? FailureCategory.ProtocolViolation
            : process.Completion switch
            {
                ProcessCompletion.TimedOut => FailureCategory.WorkerTimeout,
                ProcessCompletion.Canceled => FailureCategory.WorkerTimeout,
                ProcessCompletion.StartFailed => FailureCategory.WorkerCrash,
                ProcessCompletion.Exited when process.ExitCode is not (0 or 1) => FailureCategory.WorkerCrash,
                _ => (FailureCategory?)null,
            };
        if (category.HasValue ||
            !WorkerProtocol.TryDeserializeResult(process.StandardOutput, out var parsed) ||
            parsed is null ||
            parsed.HeaderMapper != headerMapper ||
            parsed.Backend != backend ||
            process.ExitCode != (parsed.Passed ? 0 : 1) ||
            parsed.Passed && (parsed.BackendVersion is null || parsed.ServerVersion is null))
        {
            return FailureResult(headerMapper, backend, category ?? FailureCategory.ProtocolViolation, process.ElapsedMilliseconds);
        }

        return parsed;
    }

    private static WorkerResult FailureResult(
        int headerMapper,
        QualificationBackend backend,
        FailureCategory category,
        long elapsedMilliseconds) => new(
            WorkerProtocol.SchemaVersion,
            false,
            category,
            headerMapper,
            elapsedMilliseconds,
            backend,
            null,
            null);

    private static ProcessStartInfo CreateWorkerStartInfo()
    {
        var startInfo = new ProcessStartInfo("dotnet");
        startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        startInfo.ArgumentList.Add("worker");
        return startInfo;
    }

    private static void AddCohortFailures(
        IReadOnlyList<RomCandidate> candidates,
        ExpectedCohort expected,
        FailureAccumulator failures)
    {
        var actual = candidates
            .GroupBy(candidate => candidate.Header.HeaderMapper)
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var mapper in expected.HeaderMapperCounts.Keys.Union(actual.Keys).Order())
        {
            var expectedCount = expected.HeaderMapperCounts.GetValueOrDefault(mapper);
            var actualCount = actual.GetValueOrDefault(mapper);
            if (expectedCount != actualCount)
            {
                failures.Add(FailureCategory.MissingCoverage, mapper, Math.Abs(expectedCount - actualCount));
            }
        }

        if (candidates.Count != expected.Total)
        {
            failures.Add(FailureCategory.MissingCoverage, null);
        }
    }

    private sealed class MutableMapperOutcome(int headerMapper)
    {
        public int HeaderMapper { get; } = headerMapper;
        public int Attempted { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }

        public MapperOutcome ToImmutable() => new(HeaderMapper, Attempted, Passed, Failed);
    }

    private sealed class FailureAccumulator
    {
        private readonly Dictionary<(FailureCategory Category, int? HeaderMapper), int> counts = [];

        public int Count => counts.Values.Sum();

        public void Add(FailureCategory category, int? headerMapper, int count = 1)
        {
            var key = (category, headerMapper);
            counts[key] = counts.GetValueOrDefault(key) + count;
        }

        public FailureCount[] ToImmutable() => counts
            .OrderBy(item => item.Key.Category)
            .ThenBy(item => item.Key.HeaderMapper)
            .Select(item => new FailureCount(item.Key.Category, item.Key.HeaderMapper, item.Value))
            .ToArray();
    }
}
