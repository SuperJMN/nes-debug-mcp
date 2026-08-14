using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nes.Corpus.Qualification;

namespace Nes.Debug.Tests;

public sealed class WorkerProtocolTests
{
    [Fact]
    public void Worker_request_round_trips_without_entering_public_aggregate_schema()
    {
        var request = CreateRequest();

        var encoded = WorkerProtocol.SerializeRequest(request);
        var decoded = WorkerProtocol.TryDeserializeRequest(encoded, out var actual);

        Assert.True(decoded);
        Assert.Equal(request, actual);
        Assert.DoesNotContain("source.nes", AggregateJson.Serialize(CreateReport()), StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_request_rejects_missing_and_old_schema_contracts()
    {
        var request = CreateRequest();
        using var valid = JsonDocument.Parse(WorkerProtocol.SerializeRequest(request));
        var root = valid.RootElement;
        var withoutSchema = root.EnumerateObject()
            .Where(property => property.Name != "schemaVersion")
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        var oldSchema = request with { SchemaVersion = 1 };

        Assert.False(WorkerProtocol.TryDeserializeRequest(JsonSerializer.SerializeToUtf8Bytes(withoutSchema), out _));
        Assert.False(WorkerProtocol.TryDeserializeRequest(WorkerProtocol.SerializeRequest(oldSchema), out _));
    }

    [Fact]
    public void Worker_result_rejects_hostile_versions_and_inconsistent_status()
    {
        var hostile = new WorkerResult(
            WorkerProtocol.SchemaVersion,
            true,
            null,
            0,
            1,
            QualificationBackend.AprNes,
            "version/with/path",
            "server");
        var inconsistent = hostile with { BackendVersion = "backend", FailureCategory = FailureCategory.Load };
        var unknownBackend = hostile with { BackendVersion = "backend", Backend = (QualificationBackend)99 };
        var unknownFailure = hostile with
        {
            Passed = false,
            BackendVersion = null,
            ServerVersion = null,
            FailureCategory = (FailureCategory)99,
        };

        Assert.False(WorkerProtocol.TryDeserializeResult(Encoding.UTF8.GetBytes(WorkerProtocol.SerializeResult(hostile)), out _));
        Assert.False(WorkerProtocol.TryDeserializeResult(Encoding.UTF8.GetBytes(WorkerProtocol.SerializeResult(inconsistent)), out _));
        Assert.False(WorkerProtocol.TryDeserializeResult(Encoding.UTF8.GetBytes(WorkerProtocol.SerializeResult(unknownBackend)), out _));
        Assert.False(WorkerProtocol.TryDeserializeResult(Encoding.UTF8.GetBytes(WorkerProtocol.SerializeResult(unknownFailure)), out _));
    }

    [Fact]
    public async Task Process_runner_bounds_and_discards_both_output_streams()
    {
        var startInfo = QualificationTestChild.CreateStartInfo();

        var result = await RunTestChildAsync(
            startInfo,
            new TestChildRequest("bounded-output", StandardOutputBytes: 9, StandardErrorBytes: 13),
            TimeSpan.FromSeconds(2),
            4);

        Assert.Equal(ProcessCompletion.Exited, result.Completion);
        Assert.Equal("oooo", Encoding.UTF8.GetString(result.StandardOutput));
        Assert.True(result.StandardOutputOverflow);
        Assert.False(result.StandardErrorOverflow);
    }

    [Fact]
    public void Drained_stderr_overflow_does_not_turn_a_valid_worker_result_into_a_failure()
    {
        var worker = new WorkerResult(
            WorkerProtocol.SchemaVersion,
            true,
            null,
            0,
            1,
            QualificationBackend.AprNes,
            "backend",
            "server");
        var process = new BoundedProcessResult(
            ProcessCompletion.Exited,
            0,
            Encoding.UTF8.GetBytes(WorkerProtocol.SerializeResult(worker)),
            StandardOutputOverflow: false,
            StandardErrorOverflow: true,
            CleanupTimedOut: false,
            ElapsedMilliseconds: 1);

        var result = QualificationCoordinator.InterpretWorkerResult(
            process,
            headerMapper: 0);

        Assert.True(result.Passed);
        Assert.Null(result.FailureCategory);
    }

    [Fact]
    public async Task Worker_descriptor_is_delivered_only_through_stdin_with_constant_arguments()
    {
        const string secret = "private-source-descriptor";
        var startInfo = QualificationTestChild.CreateStartInfo();

        var result = await RunTestChildAsync(
            startInfo,
            new TestChildRequest("constant-args", secret),
            TimeSpan.FromSeconds(2),
            64);

        Assert.Equal(ProcessCompletion.Exited, result.Completion);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("test-child", JsonDocument.Parse(result.StandardOutput).RootElement.GetProperty("argument").GetString());
        Assert.All(startInfo.ArgumentList, argument => Assert.DoesNotContain(secret, argument, StringComparison.Ordinal));
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(result.StandardOutput), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Process_runner_enforces_a_real_wall_timeout()
    {
        var startInfo = QualificationTestChild.CreateStartInfo();

        var result = await RunTestChildAsync(
            startInfo,
            new TestChildRequest("hang"),
            TimeSpan.FromMilliseconds(100),
            32);

        Assert.Equal(ProcessCompletion.TimedOut, result.Completion);
        Assert.InRange(result.ElapsedMilliseconds, 0, 2500);
    }

    [Fact]
    public async Task Process_runner_kills_a_real_grandchild_on_timeout()
    {
        var startInfo = QualificationTestChild.CreateStartInfo();

        var result = await RunTestChildAsync(
            startInfo,
            new TestChildRequest("spawn-grandchild"),
            TimeSpan.FromMilliseconds(750),
            128);

        Assert.Equal(ProcessCompletion.TimedOut, result.Completion);
        var grandchildPid = JsonDocument.Parse(result.StandardOutput).RootElement.GetProperty("processId").GetInt32();
        await AssertProcessExitedAsync(grandchildPid);
    }

    [Fact]
    public async Task Post_kill_grace_returns_when_exit_and_stream_tasks_never_complete()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        var completed = await BoundedProcessRunner.WaitForTasksWithinGraceAsync(
            [never.Task, never.Task, never.Task],
            TimeSpan.FromMilliseconds(100));

        Assert.False(completed);
        Assert.InRange(stopwatch.ElapsedMilliseconds, 50, 1000);
    }

    private static Task<BoundedProcessResult> RunTestChildAsync(
        ProcessStartInfo startInfo,
        TestChildRequest request,
        TimeSpan timeout,
        int outputLimit) => BoundedProcessRunner.RunAsync(
            startInfo,
            timeout,
            outputLimit,
            JsonSerializer.SerializeToUtf8Bytes(request));

    private static async Task AssertProcessExitedAsync(int processId)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("Grandchild process survived the process-tree timeout.");
    }

    private static WorkerRequest CreateRequest() => new(
        WorkerProtocol.SchemaVersion,
        WorkerSourceKind.Direct,
        "source.nes",
        null,
        24592,
        new RomImageHeader(0, 24592, false, NesImageFormat.INes),
        "staging.nes",
        "state.tmp",
        "server.dll",
        new QualificationBounds(30, 10, 1024 * 1024, 4, 10000, 128));

    private static QualificationReport CreateReport() => new(
        AggregateJson.SchemaVersion,
        true,
        1,
        1,
        1,
        1,
        0,
        [new MapperOutcome(0, 1, 1, 0)],
        [],
        [],
        1,
        1,
        [new BackendIdentity(QualificationBackend.AprNes, "backend", "server")],
        new QualificationBounds(30, 10, 1024 * 1024, 4, 10000, 128),
        new ExpectedCohort(1, new SortedDictionary<int, int> { [0] = 1 }));
}
