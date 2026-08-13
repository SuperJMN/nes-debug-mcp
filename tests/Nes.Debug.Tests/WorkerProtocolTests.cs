using System.Diagnostics;
using System.Text;
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
        var startInfo = Shell("printf '123456789'; printf 'secret-stderr' >&2");

        var result = await BoundedProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(2), 4);

        Assert.Equal(ProcessCompletion.Exited, result.Completion);
        Assert.Equal("1234", Encoding.UTF8.GetString(result.StandardOutput));
        Assert.True(result.StandardOutputOverflow);
        Assert.False(result.StandardErrorOverflow);
    }

    [Fact]
    public async Task Worker_descriptor_is_delivered_only_through_stdin_with_constant_arguments()
    {
        const string secret = "private-source-descriptor";
        var startInfo = Shell("IFS= read -r payload; test \"${#payload}\" -eq 25; printf '%s' \"$0\"");
        startInfo.ArgumentList.Add("worker");

        var result = await BoundedProcessRunner.RunAsync(
            startInfo,
            TimeSpan.FromSeconds(2),
            64,
            Encoding.UTF8.GetBytes(secret + "\n"));

        Assert.Equal(ProcessCompletion.Exited, result.Completion);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("worker", Encoding.UTF8.GetString(result.StandardOutput));
        Assert.All(startInfo.ArgumentList, argument => Assert.DoesNotContain(secret, argument, StringComparison.Ordinal));
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(result.StandardOutput), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Process_runner_enforces_a_real_wall_timeout()
    {
        var startInfo = Shell("sleep 30");

        var result = await BoundedProcessRunner.RunAsync(startInfo, TimeSpan.FromMilliseconds(100), 32);

        Assert.Equal(ProcessCompletion.TimedOut, result.Completion);
        Assert.InRange(result.ElapsedMilliseconds, 0, 5000);
    }

    [Fact]
    public async Task Process_runner_kills_a_real_grandchild_on_timeout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var pidFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nes-worker-grandchild-{Guid.NewGuid():N}.pid");
        try
        {
            var startInfo = Shell($"sleep 30 & child=$!; printf '%s' \"$child\" > '{pidFile}'; wait");

            var result = await BoundedProcessRunner.RunAsync(startInfo, TimeSpan.FromMilliseconds(500), 32);

            Assert.Equal(ProcessCompletion.TimedOut, result.Completion);
            var grandchildPid = int.Parse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
            await AssertProcessExitedAsync(grandchildPid);
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    private static ProcessStartInfo Shell(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            var windows = new ProcessStartInfo("cmd.exe");
            windows.ArgumentList.Add("/c");
            windows.ArgumentList.Add(command);
            return windows;
        }

        var unix = new ProcessStartInfo("/bin/sh");
        unix.ArgumentList.Add("-c");
        unix.ArgumentList.Add(command);
        return unix;
    }

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
        WorkerSourceKind.Direct,
        "source.nes",
        null,
        24592,
        new RomImageHeader(0, 24592, false, NesImageFormat.INes),
        "staging.nes",
        "state.tmp",
        "server.dll",
        QualificationBackend.AprNes,
        new QualificationBounds(30, 10, 1024 * 1024, 4, 10000, 128));

    private static QualificationReport CreateReport() => new(
        AggregateJson.SchemaVersion,
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
        new ExpectedCohort(1, new SortedDictionary<int, int> { [0] = 1 }),
        new IndependentSmokeCoverage(
            QualificationBackend.Adnes,
            "backend",
            "server",
            1,
            1,
            0,
            [new MapperOutcome(0, 1, 1, 0)]));
}
