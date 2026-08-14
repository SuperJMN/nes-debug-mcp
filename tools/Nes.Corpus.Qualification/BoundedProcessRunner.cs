using System.Diagnostics;

namespace Nes.Corpus.Qualification;

internal enum ProcessCompletion
{
    Exited,
    TimedOut,
    Canceled,
    StartFailed,
}

internal sealed record BoundedProcessResult(
    ProcessCompletion Completion,
    int? ExitCode,
    byte[] StandardOutput,
    bool StandardOutputOverflow,
    bool StandardErrorOverflow,
    bool CleanupTimedOut,
    long ElapsedMilliseconds);

internal static class BoundedProcessRunner
{
    private const int ErrorObservationLimit = 16 * 1024;
    internal static readonly TimeSpan PostTerminationGrace = TimeSpan.FromSeconds(1);

    public static async Task<BoundedProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        int standardOutputLimit,
        ReadOnlyMemory<byte> standardInput = default,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Process? process = null;
        Task<CaptureResult>? stdout = null;
        Task<CaptureResult>? stderr = null;
        Task? input = null;
        Task? exit = null;
        try
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardInput = true;
            startInfo.UseShellExecute = false;
            process = Process.Start(startInfo);
            if (process is null)
            {
                return Result(ProcessCompletion.StartFailed, null, [], false, false, false, stopwatch);
            }

            stdout = DrainAsync(process.StandardOutput.BaseStream, standardOutputLimit, capture: true);
            stderr = DrainAsync(process.StandardError.BaseStream, ErrorObservationLimit, capture: false);
            input = SendInputAsync(process.StandardInput.BaseStream, standardInput);
            exit = process.WaitForExitAsync(CancellationToken.None);
            var completion = ProcessCompletion.Exited;
            try
            {
                await exit.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                completion = ProcessCompletion.TimedOut;
                KillTree(process);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completion = ProcessCompletion.Canceled;
                KillTree(process);
            }

            var cleanupCompleted = await WaitForTasksWithinGraceAsync(
                [exit, stdout, stderr, input],
                PostTerminationGrace).ConfigureAwait(false);
            var capturedStdout = CompletedCapture(stdout);
            var capturedStderr = CompletedCapture(stderr);
            return Result(
                completion,
                TryGetExitCode(process),
                capturedStdout.Bytes,
                capturedStdout.Overflow,
                capturedStderr.Overflow,
                !cleanupCompleted,
                stopwatch);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Result(ProcessCompletion.StartFailed, null, [], false, false, false, stopwatch);
        }
        finally
        {
            if (process is not null)
            {
                KillTree(process);
                try
                {
                    process.Dispose();
                }
                catch (InvalidOperationException)
                {
                }
            }

            Observe(exit);
            Observe(stdout);
            Observe(stderr);
            Observe(input);
        }
    }

    internal static async Task<bool> WaitForTasksWithinGraceAsync(
        IReadOnlyCollection<Task> tasks,
        TimeSpan grace)
    {
        var all = Task.WhenAll(tasks);
        try
        {
            await all.WaitAsync(grace, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
        {
            return true;
        }
        finally
        {
            foreach (var task in tasks)
            {
                Observe(task);
            }
        }
    }

    private static async Task<CaptureResult> DrainAsync(Stream stream, int limit, bool capture)
    {
        await using (stream.ConfigureAwait(false))
        {
            using var retained = capture ? new MemoryStream(Math.Min(limit, 4096)) : null;
            var buffer = new byte[4096];
            var observed = 0L;
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (retained is not null && retained.Length < limit)
                {
                    var toKeep = (int)Math.Min(read, limit - retained.Length);
                    retained.Write(buffer, 0, toKeep);
                }

                observed += read;
            }

            return new CaptureResult(retained?.ToArray() ?? [], observed > limit);
        }
    }

    private static async Task SendInputAsync(Stream stream, ReadOnlyMemory<byte> input)
    {
        await using (stream.ConfigureAwait(false))
        {
            if (!input.IsEmpty)
            {
                await stream.WriteAsync(input).ConfigureAwait(false);
            }
        }
    }

    private static CaptureResult CompletedCapture(Task<CaptureResult> task)
    {
        return task.IsCompletedSuccessfully
            ? task.Result
            : new CaptureResult([], Overflow: true);
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void Observe(Task? task)
    {
        if (task is null)
        {
            return;
        }

        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static BoundedProcessResult Result(
        ProcessCompletion completion,
        int? exitCode,
        byte[] stdout,
        bool stdoutOverflow,
        bool stderrOverflow,
        bool cleanupTimedOut,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return new BoundedProcessResult(
            completion,
            exitCode,
            stdout,
            stdoutOverflow,
            stderrOverflow,
            cleanupTimedOut,
            stopwatch.ElapsedMilliseconds);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private sealed record CaptureResult(byte[] Bytes, bool Overflow);
}
