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
    long ElapsedMilliseconds);

internal static class BoundedProcessRunner
{
    private const int ErrorObservationLimit = 16 * 1024;

    public static async Task<BoundedProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        int standardOutputLimit,
        ReadOnlyMemory<byte> standardInput = default,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Process? process = null;
        try
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardInput = true;
            startInfo.UseShellExecute = false;
            process = Process.Start(startInfo);
            if (process is null)
            {
                return Result(ProcessCompletion.StartFailed, null, [], false, false, stopwatch);
            }

            var stdout = DrainAsync(process.StandardOutput.BaseStream, standardOutputLimit, capture: true);
            var stderr = DrainAsync(process.StandardError.BaseStream, ErrorObservationLimit, capture: false);
            var input = SendInputAsync(process.StandardInput.BaseStream, standardInput);
            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                KillTree(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                var timedOutStdout = await stdout.ConfigureAwait(false);
                var timedOutStderr = await stderr.ConfigureAwait(false);
                await IgnoreInputFailureAsync(input).ConfigureAwait(false);
                return Result(
                    ProcessCompletion.TimedOut,
                    process.ExitCode,
                    timedOutStdout.Bytes,
                    timedOutStdout.Overflow,
                    timedOutStderr.Overflow,
                    stopwatch);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                KillTree(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                var canceledStdout = await stdout.ConfigureAwait(false);
                var canceledStderr = await stderr.ConfigureAwait(false);
                await IgnoreInputFailureAsync(input).ConfigureAwait(false);
                return Result(
                    ProcessCompletion.Canceled,
                    process.ExitCode,
                    canceledStdout.Bytes,
                    canceledStdout.Overflow,
                    canceledStderr.Overflow,
                    stopwatch);
            }

            var capturedStdout = await stdout.ConfigureAwait(false);
            var capturedStderr = await stderr.ConfigureAwait(false);
            await IgnoreInputFailureAsync(input).ConfigureAwait(false);
            return Result(
                ProcessCompletion.Exited,
                process.ExitCode,
                capturedStdout.Bytes,
                capturedStdout.Overflow,
                capturedStderr.Overflow,
                stopwatch);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Result(ProcessCompletion.StartFailed, null, [], false, false, stopwatch);
        }
        finally
        {
            if (process is not null)
            {
                KillTree(process);
                process.Dispose();
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

    private static async Task IgnoreInputFailureAsync(Task input)
    {
        try
        {
            await input.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private static BoundedProcessResult Result(
        ProcessCompletion completion,
        int? exitCode,
        byte[] stdout,
        bool stdoutOverflow,
        bool stderrOverflow,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return new BoundedProcessResult(
            completion,
            exitCode,
            stdout,
            stdoutOverflow,
            stderrOverflow,
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
