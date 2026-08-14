using System.Diagnostics;
using System.Reflection;

namespace Nes.Corpus.Qualification;

internal sealed record QualificationRun(QualificationReport Report, bool Succeeded);

internal sealed record WorkerExecution(WorkerResult Result, long WallElapsedMilliseconds);

internal static class QualificationCoordinator
{
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

        totalStopwatch.Stop();
        var mapperOutcomes = mapperCounters.Values
            .OrderBy(counter => counter.HeaderMapper)
            .Select(counter => counter.ToImmutable())
            .ToArray();
        var attempted = mapperOutcomes.Sum(outcome => outcome.Attempted);
        var passed = mapperOutcomes.Sum(outcome => outcome.Passed);
        var failed = mapperOutcomes.Sum(outcome => outcome.Failed);
        var hasObservedIdentity = aprNesIdentities.Count == 1;
        if (aprNesIdentities.Count == 0)
        {
            aprNesIdentities.Add(new BackendIdentity(QualificationBackend.AprNes, "unavailable", "unavailable"));
        }

        var succeeded = attempted > 0 && failed == 0 && failures.Count == 0 && hasObservedIdentity;
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
            options.Expected);
        return new QualificationRun(report, succeeded);
    }

    public static QualificationRun CreateClosedFailure(QualificationOptions options)
    {
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
            options.Expected);
        return new QualificationRun(report, false);
    }

    private static async Task<WorkerExecution> RunWorkerAsync(
        RomCandidate candidate,
        QualificationOptions options,
        CancellationToken cancellationToken)
    {
        var artifacts = TempArtifacts.Create();
        WorkerResult workerResult = FailureResult(
            candidate.Header.HeaderMapper,
            FailureCategory.WorkerCrash,
            0);
        long elapsed = 0;
        var cleanupSucceeded = false;
        try
        {
            var request = new WorkerRequest(
                WorkerProtocol.SchemaVersion,
                candidate.Source is RomSource.Direct ? WorkerSourceKind.Direct : WorkerSourceKind.ZipEntry,
                candidate.Source.ContainerPath,
                candidate.Source is RomSource.ZipEntry zip ? zip.EntryIndex : null,
                candidate.ObservedBytes,
                candidate.Header,
                artifacts.ImagePath,
                artifacts.StatePath,
                options.ServerAssembly,
                options.Bounds);
            var process = await BoundedProcessRunner.RunAsync(
                CreateWorkerStartInfo(),
                TimeSpan.FromSeconds(options.Bounds.WallTimeoutSeconds),
                WorkerProtocol.MaximumResultBytes,
                WorkerProtocol.SerializeRequest(request),
                cancellationToken).ConfigureAwait(false);
            elapsed = process.ElapsedMilliseconds;
            workerResult = InterpretWorkerResult(process, candidate.Header.HeaderMapper);
        }
        catch
        {
            workerResult = FailureResult(
                candidate.Header.HeaderMapper,
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
        int headerMapper)
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
            parsed.Backend != QualificationBackend.AprNes ||
            process.ExitCode != (parsed.Passed ? 0 : 1) ||
            parsed.Passed && (parsed.BackendVersion is null || parsed.ServerVersion is null))
        {
            return FailureResult(headerMapper, category ?? FailureCategory.ProtocolViolation, process.ElapsedMilliseconds);
        }

        return parsed;
    }

    private static WorkerResult FailureResult(
        int headerMapper,
        FailureCategory category,
        long elapsedMilliseconds) => new(
            WorkerProtocol.SchemaVersion,
            false,
            category,
            headerMapper,
            elapsedMilliseconds,
            QualificationBackend.AprNes,
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
        if (expected.Total <= 0)
        {
            failures.Add(FailureCategory.MissingCoverage, null);
        }

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
