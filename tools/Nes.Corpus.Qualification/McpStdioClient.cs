using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Nes.Corpus.Qualification;

internal enum McpCallFailure
{
    None,
    ToolError,
    ResponseOverflow,
    InvalidContent,
    RequestWrite,
    ServerCrash,
    ResponseEof,
    InvalidJson,
    UnexpectedId,
}

internal sealed record McpResponse(bool IsSuccess, JsonElement Payload, McpCallFailure Failure)
{
    public static McpResponse Failed(McpCallFailure failure) => new(false, default, failure);
}

internal sealed record McpJsonCall(bool IsSuccess, JsonElement Payload, McpCallFailure Failure)
{
    public static McpJsonCall Failed(McpCallFailure failure) => new(false, default, failure);
}

internal sealed record McpImageCall(bool IsSuccess, string? MimeType, byte[] Data)
{
    public static McpImageCall Failure() => new(false, null, []);
}

internal sealed class McpStdioClient : IAsyncDisposable
{
    internal const string ProtocolVersion = "2025-06-18";
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const int MaximumMessagesPerResponse = 32;
    private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CleanupGrace = TimeSpan.FromSeconds(1);

    private readonly Process process;
    private readonly Stream input;
    private readonly BoundedLineReader output;
    private readonly Task stderrDrain;
    private int nextRequestId;
    private bool stopRequested;
    private bool fullyDisposed;

    private McpStdioClient(Process process)
    {
        this.process = process;
        input = process.StandardInput.BaseStream;
        output = new BoundedLineReader(process.StandardOutput.BaseStream, MaximumResponseBytes);
        stderrDrain = DiscardAsync(process.StandardError.BaseStream);
    }

    public static McpStdioClient? Start(string serverAssembly)
    {
        var startInfo = new ProcessStartInfo("dotnet");
        startInfo.ArgumentList.Add(serverAssembly);
        return Start(startInfo);
    }

    internal static McpStdioClient? Start(ProcessStartInfo startInfo)
    {
        try
        {
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;

            var process = Process.Start(startInfo);
            return process is null ? null : new McpStdioClient(process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public async Task<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        var response = await RequestAsync(
            "initialize",
            new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "nes-corpus-qualification", version = "1" },
            },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess ||
            !response.Payload.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("protocolVersion", out var protocolVersion) ||
            protocolVersion.ValueKind != JsonValueKind.String ||
            protocolVersion.GetString() != ProtocolVersion ||
            !result.TryGetProperty("capabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return await NotifyAsync("notifications/initialized", new { }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpJsonCall> CallJsonAsync(string tool, object arguments, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(
            "tools/call",
            new { name = tool, arguments },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return McpJsonCall.Failed(response.Failure);
        }

        if (!TryGetSingleContent(response.Payload, "text", out var content, out var isError))
        {
            return McpJsonCall.Failed(McpCallFailure.InvalidContent);
        }

        if (!content.TryGetProperty("text", out var textProperty) || textProperty.ValueKind != JsonValueKind.String)
        {
            return McpJsonCall.Failed(McpCallFailure.InvalidContent);
        }

        try
        {
            using var payload = JsonDocument.Parse(textProperty.GetString()!);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return McpJsonCall.Failed(McpCallFailure.InvalidContent);
            }

            return isError || root.TryGetProperty("error", out _)
                ? McpJsonCall.Failed(McpCallFailure.ToolError)
                : new McpJsonCall(true, root.Clone(), McpCallFailure.None);
        }
        catch (JsonException)
        {
            return McpJsonCall.Failed(McpCallFailure.InvalidContent);
        }
    }

    public async Task<McpImageCall> CallImageAsync(string tool, object arguments, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(
            "tools/call",
            new { name = tool, arguments },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess ||
            !TryGetSingleContent(response.Payload, "image", out var content, out var isError) || isError ||
            !content.TryGetProperty("mimeType", out var mimeTypeProperty) ||
            mimeTypeProperty.ValueKind != JsonValueKind.String ||
            !content.TryGetProperty("data", out var dataProperty) ||
            dataProperty.ValueKind != JsonValueKind.String)
        {
            return McpImageCall.Failure();
        }

        try
        {
            var data = Convert.FromBase64String(dataProperty.GetString()!);
            var mimeType = mimeTypeProperty.GetString();
            return mimeType == "image/png" && PngValidator.IsNesFrame(data)
                ? new McpImageCall(true, mimeType, data)
                : McpImageCall.Failure();
        }
        catch (FormatException)
        {
            return McpImageCall.Failure();
        }
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken)
    {
        if (stopRequested || fullyDisposed)
        {
            return false;
        }

        stopRequested = true;
        var closeInput = CloseInputAsync(input);
        var waitForExit = WaitForExitAsync();
        if (!await WaitForTasksWithinAsync(
                [closeInput, waitForExit],
                GracefulShutdownTimeout,
                cancellationToken).ConfigureAwait(false))
        {
            KillTree();
            _ = await WaitForTasksWithinAsync(
                [closeInput, waitForExit, stderrDrain],
                CleanupGrace,
                CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        if (!await WaitForTasksWithinAsync([stderrDrain], CleanupGrace, CancellationToken.None).ConfigureAwait(false))
        {
            return false;
        }

        return TryGetSuccessfulExit() && !output.Overflow;
    }

    public async ValueTask DisposeAsync()
    {
        if (fullyDisposed)
        {
            return;
        }

        fullyDisposed = true;
        stopRequested = true;
        var closeInput = CloseInputAsync(input);
        KillTree();
        var waitForExit = WaitForExitAsync();
        _ = await WaitForTasksWithinAsync(
            [closeInput, waitForExit, stderrDrain],
            CleanupGrace,
            CancellationToken.None).ConfigureAwait(false);

        try
        {
            process.Dispose();
        }
        catch
        {
            // Reaping is best effort and bounded above.
        }
    }

    internal static async Task CloseInputAsync(Stream stream)
    {
        try
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Closing the request pipe is cleanup; process termination below remains authoritative.
        }
    }

    private Task WaitForExitAsync()
    {
        try
        {
            return process.WaitForExitAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    private static async Task<bool> WaitForTasksWithinAsync(
        IReadOnlyList<Task> tasks,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var all = Task.WhenAll(tasks);
        try
        {
            await all.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            foreach (var task in tasks)
            {
                _ = task.ContinueWith(
                    completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return false;
        }
    }

    private bool TryGetSuccessfulExit()
    {
        try
        {
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task<McpResponse> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref nextRequestId);
        if (!await WriteMessageAsync(
                new { jsonrpc = "2.0", id, method, @params = parameters },
                cancellationToken).ConfigureAwait(false))
        {
            return McpResponse.Failed(McpCallFailure.RequestWrite);
        }

        for (var messageIndex = 0; messageIndex < MaximumMessagesPerResponse; messageIndex++)
        {
            byte[]? line;
            try
            {
                line = await output.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return McpResponse.Failed(McpCallFailure.ResponseOverflow);
            }

            if (line is null)
            {
                return McpResponse.Failed(await ClassifyEndOfOutputAsync(cancellationToken).ConfigureAwait(false));
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (IsServerNotification(root))
                {
                    continue;
                }

                if (!IsValidResponseEnvelope(root, id))
                {
                    return McpResponse.Failed(McpCallFailure.InvalidContent);
                }

                return new McpResponse(true, root.Clone(), McpCallFailure.None);
            }
            catch (JsonException)
            {
                return McpResponse.Failed(McpCallFailure.InvalidJson);
            }
        }

        return McpResponse.Failed(McpCallFailure.UnexpectedId);
    }

    private static bool IsValidResponseEnvelope(JsonElement root, int expectedId)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("jsonrpc", out var jsonRpc) ||
            jsonRpc.ValueKind != JsonValueKind.String ||
            jsonRpc.GetString() != "2.0" ||
            !root.TryGetProperty("id", out var responseId) ||
            responseId.ValueKind != JsonValueKind.Number ||
            !responseId.TryGetInt32(out var actualId) ||
            actualId != expectedId)
        {
            return false;
        }

        var hasResult = root.TryGetProperty("result", out _);
        var hasError = root.TryGetProperty("error", out var error);
        return hasResult != hasError && (!hasError || error.ValueKind == JsonValueKind.Object);
    }

    private static bool IsServerNotification(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("jsonrpc", out var jsonRpc) &&
        jsonRpc.ValueKind == JsonValueKind.String &&
        jsonRpc.GetString() == "2.0" &&
        root.TryGetProperty("method", out var method) &&
        method.ValueKind == JsonValueKind.String &&
        !root.TryGetProperty("id", out _) &&
        !root.TryGetProperty("result", out _) &&
        !root.TryGetProperty("error", out _);

    private async Task<McpCallFailure> ClassifyEndOfOutputAsync(CancellationToken cancellationToken)
    {
        if (!process.HasExited)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return McpCallFailure.ResponseEof;
            }
        }

        return process.ExitCode == 0 ? McpCallFailure.ResponseEof : McpCallFailure.ServerCrash;
    }

    private Task<bool> NotifyAsync(string method, object parameters, CancellationToken cancellationToken) =>
        WriteMessageAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);

    private async Task<bool> WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            await input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await input.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static bool TryGetSingleContent(
        JsonElement response,
        string expectedType,
        out JsonElement content,
        out bool isError)
    {
        content = default;
        isError = false;
        if (response.TryGetProperty("error", out _) ||
            !response.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("content", out var contents) ||
            contents.ValueKind != JsonValueKind.Array ||
            contents.GetArrayLength() != 1)
        {
            return false;
        }

        isError = result.TryGetProperty("isError", out var errorProperty) &&
                  errorProperty.ValueKind == JsonValueKind.True;

        content = contents[0];
        return content.ValueKind == JsonValueKind.Object &&
               content.TryGetProperty("type", out var type) &&
               type.ValueKind == JsonValueKind.String &&
               type.GetString() == expectedType;
    }

    private static async Task DiscardAsync(Stream stream)
    {
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

            }
        }
    }

    private void KillTree()
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
}

internal static class PngValidator
{
    public const int MaximumBytes = 1024 * 1024;
    private const int Width = 256;
    private const int Height = 240;
    private const int MaximumDecodedBytes = Height * (Width * 4 + 1);

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsNesFrame(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 57 or > MaximumBytes || !data[..8].SequenceEqual(Signature))
        {
            return false;
        }

        var offset = Signature.Length;
        var chunkIndex = 0;
        var sawIdat = false;
        var sawIend = false;
        var idatSequenceEnded = false;
        var bytesPerPixel = 0;
        using var compressed = new MemoryStream();
        while (offset < data.Length)
        {
            if (data.Length - offset < 12)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            if (length > MaximumBytes || length > (uint)(data.Length - offset - 12))
            {
                return false;
            }

            var type = data.Slice(offset + 4, 4);
            var payload = data.Slice(offset + 8, (int)length);
            var storedCrc = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8 + (int)length, 4));
            if (CalculateCrc(type, payload) != storedCrc)
            {
                return false;
            }

            if (chunkIndex == 0)
            {
                if (!type.SequenceEqual("IHDR"u8) || !IsValidIhdr(payload))
                {
                    return false;
                }

                bytesPerPixel = payload[9] == 2 ? 3 : 4;
            }
            else if (type.SequenceEqual("IHDR"u8))
            {
                return false;
            }

            if (type.SequenceEqual("IDAT"u8))
            {
                if (sawIend || idatSequenceEnded)
                {
                    return false;
                }

                sawIdat = true;
                compressed.Write(payload);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || !sawIdat || sawIend)
                {
                    return false;
                }

                sawIend = true;
                offset += 12;
                break;
            }
            else if (sawIdat)
            {
                idatSequenceEnded = true;
            }

            offset += 12 + (int)length;
            chunkIndex++;
        }

        return sawIend && offset == data.Length && HasValidImageData(compressed.ToArray(), bytesPerPixel);
    }

    private static bool IsValidIhdr(ReadOnlySpan<byte> payload) =>
        payload.Length == 13 &&
        BinaryPrimitives.ReadUInt32BigEndian(payload[..4]) == Width &&
        BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)) == Height &&
        payload[8] == 8 &&
        payload[9] is 2 or 6 &&
        payload[10] == 0 &&
        payload[11] == 0 &&
        payload[12] == 0;

    private static bool HasValidImageData(byte[] compressed, int bytesPerPixel)
    {
        try
        {
            using var source = new MemoryStream(compressed, writable: false);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            var decoded = new byte[MaximumDecodedBytes + 1];
            var total = 0;
            while (total < decoded.Length)
            {
                var read = zlib.Read(decoded, total, decoded.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            var expectedLength = Height * (Width * bytesPerPixel + 1);
            if (total != expectedLength || zlib.ReadByte() != -1)
            {
                return false;
            }

            var rowLength = Width * bytesPerPixel + 1;
            for (var row = 0; row < Height; row++)
            {
                if (decoded[row * rowLength] > 4)
                {
                    return false;
                }
            }

            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static uint CalculateCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        var crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, payload);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc;
    }
}

internal sealed class BoundedLineReader(Stream stream, int maximumBytes)
{
    private readonly byte[] buffer = new byte[4096];
    private int start;
    private int end;

    public bool Overflow { get; private set; }

    public async Task<byte[]?> ReadLineAsync(CancellationToken cancellationToken)
    {
        using var line = new MemoryStream(Math.Min(maximumBytes, 4096));
        while (true)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n', start, end - start);
            if (newline >= 0)
            {
                Append(line, buffer.AsSpan(start, newline - start));
                start = newline + 1;
                var bytes = line.ToArray();
                return bytes.Length > 0 && bytes[^1] == (byte)'\r' ? bytes[..^1] : bytes;
            }

            if (start < end)
            {
                Append(line, buffer.AsSpan(start, end - start));
                start = end;
            }

            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            start = 0;
            end = read;
            if (read == 0)
            {
                return line.Length == 0 ? null : line.ToArray();
            }
        }
    }

    private void Append(MemoryStream line, ReadOnlySpan<byte> bytes)
    {
        if (line.Length + bytes.Length > maximumBytes)
        {
            Overflow = true;
            throw new InvalidDataException("MCP response exceeded its bound.");
        }

        line.Write(bytes);
    }
}
