using System.Diagnostics;
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
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const int MaximumMessagesPerResponse = 32;

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

    public static McpStdioClient? Start(string serverAssembly, QualificationBackend backend)
    {
        var startInfo = new ProcessStartInfo("dotnet");
        startInfo.ArgumentList.Add(serverAssembly);
        return Start(startInfo, backend);
    }

    internal static McpStdioClient? Start(ProcessStartInfo startInfo, QualificationBackend backend)
    {
        try
        {
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.Environment["NES_MCP_EMULATOR_BACKEND"] = backend switch
            {
                QualificationBackend.AprNes => "aprnes",
                QualificationBackend.Adnes => "adnes",
                _ => throw new ArgumentOutOfRangeException(nameof(backend)),
            };
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
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "nes-corpus-qualification", version = "1" },
            },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess || response.Payload.TryGetProperty("error", out _))
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
        await CloseInputAsync(input).ConfigureAwait(false);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            KillTree();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await stderrDrain.ConfigureAwait(false);
        return process.ExitCode == 0 && !output.Overflow;
    }

    public async ValueTask DisposeAsync()
    {
        if (fullyDisposed)
        {
            return;
        }

        fullyDisposed = true;
        stopRequested = true;
        await CloseInputAsync(input).ConfigureAwait(false);
        KillTree();
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }

        await stderrDrain.ConfigureAwait(false);

        process.Dispose();
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
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("id", out var responseId) &&
                    responseId.ValueKind == JsonValueKind.Number &&
                    responseId.TryGetInt32(out var actualId) &&
                    actualId == id)
                {
                    return new McpResponse(true, root.Clone(), McpCallFailure.None);
                }
            }
            catch (JsonException)
            {
                return McpResponse.Failed(McpCallFailure.InvalidJson);
            }
        }

        return McpResponse.Failed(McpCallFailure.UnexpectedId);
    }

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

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsNesFrame(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 33 or > MaximumBytes || !data[..8].SequenceEqual(Signature))
        {
            return false;
        }

        var ihdrLength = ReadUInt32BigEndian(data[8..12]);
        return ihdrLength == 13 &&
               data[12..16].SequenceEqual("IHDR"u8) &&
               ReadUInt32BigEndian(data[16..20]) == 256 &&
               ReadUInt32BigEndian(data[20..24]) == 240;
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> bytes) =>
        ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
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
