using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Nes.Debug.Core;

namespace Nes.Corpus.Qualification;

internal sealed record TestChildRequest(
    string Mode,
    string? Payload = null,
    int StandardOutputBytes = 0,
    int StandardErrorBytes = 0);

internal static class QualificationTestChild
{
    private const int MaximumRequestBytes = 4096;
    private const string McpModeEnvironment = "NES_QUALIFICATION_TEST_CHILD_MCP_MODE";

    public static async Task<int> RunAsync(Stream input, Stream output, Stream error)
    {
        var mcpMode = Environment.GetEnvironmentVariable(McpModeEnvironment);
        if (!string.IsNullOrEmpty(mcpMode))
        {
            return await RunMcpServerAsync(mcpMode, input, output, error).ConfigureAwait(false);
        }

        var request = await ReadRequestAsync(input).ConfigureAwait(false);
        if (request is null)
        {
            return 2;
        }

        switch (request.Mode)
        {
            case "constant-args":
                await JsonSerializer.SerializeAsync(output, new { argument = "test-child" }).ConfigureAwait(false);
                return 0;
            case "bounded-output":
                await WriteRepeatedAsync(output, (byte)'o', request.StandardOutputBytes).ConfigureAwait(false);
                await WriteRepeatedAsync(error, (byte)'e', request.StandardErrorBytes).ConfigureAwait(false);
                return 0;
            case "hang":
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                return 3;
            case "spawn-grandchild":
                return await SpawnGrandchildAsync(output).ConfigureAwait(false);
            default:
                return 2;
        }
    }

    private static async Task<int> SpawnGrandchildAsync(Stream output)
    {
        using var grandchild = Process.Start(CreateStartInfo()) ?? throw new InvalidOperationException();
        await JsonSerializer.SerializeAsync(grandchild.StandardInput.BaseStream, new TestChildRequest("hang")).ConfigureAwait(false);
        await grandchild.StandardInput.BaseStream.DisposeAsync().ConfigureAwait(false);
        await JsonSerializer.SerializeAsync(output, new { processId = grandchild.Id }).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        return 3;
    }

    internal static ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo("dotnet");
        startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        startInfo.ArgumentList.Add("test-child");
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        return startInfo;
    }

    internal static ProcessStartInfo CreateMcpStartInfo(string mode)
    {
        var startInfo = CreateStartInfo();
        startInfo.Environment[McpModeEnvironment] = mode;
        return startInfo;
    }

    private static async Task<int> RunMcpServerAsync(string mode, Stream input, Stream output, Stream error)
    {
        await error.WriteAsync("discarded-test-stderr"u8.ToArray()).ConfigureAwait(false);
        await error.FlushAsync().ConfigureAwait(false);
        using var reader = new StreamReader(input, leaveOpen: true);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            using var request = JsonDocument.Parse(line);
            var root = request.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "notifications/initialized")
            {
                continue;
            }

            var id = root.GetProperty("id").GetInt32();
            if (mode == "oversize")
            {
                await WriteRepeatedAsync(output, (byte)'x', 2 * 1024 * 1024 + 1).ConfigureAwait(false);
                await output.WriteAsync("\n"u8.ToArray()).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
                continue;
            }

            if (method == "initialize")
            {
                await WriteInitializeAsync(mode, output, id).ConfigureAwait(false);
                continue;
            }

            if (mode == "crash")
            {
                return 23;
            }

            var tool = root.GetProperty("params").GetProperty("name").GetString();
            object result = tool switch
            {
                "json" => TextResult(new { value = 7 }),
                "error" => new
                {
                    isError = true,
                    content = new[] { new { type = "text", text = "{\"error\":{\"code\":\"test\"}}" } },
                },
                "image" => ImageResult(),
                "pid" => TextResult(new { pid = Environment.ProcessId }),
                _ => TextResult(new { value = 0 }),
            };
            await WriteEnvelopeAsync(output, id, result).ConfigureAwait(false);
        }

        if (mode == "hang-after-input")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        }

        return 0;
    }

    private static Task WriteInitializeAsync(string mode, Stream output, int id)
    {
        return mode switch
        {
            "missing-jsonrpc" => WriteEnvelopeAsync(output, id, InitializeResult(), includeJsonRpc: false),
            "wrong-jsonrpc" => WriteEnvelopeAsync(output, id, InitializeResult(), jsonRpc: "1.0"),
            "wrong-id" => WriteEnvelopeAsync(output, id + 1, InitializeResult()),
            "result-and-error" => WriteEnvelopeAsync(output, id, InitializeResult(), new { code = -1 }),
            "neither-result-nor-error" => WriteEnvelopeAsync(output, id),
            "initialize-scalar" => WriteEnvelopeAsync(output, id, "invalid"),
            "initialize-version-mismatch" => WriteEnvelopeAsync(
                output,
                id,
                new { protocolVersion = "1900-01-01", capabilities = new { } }),
            "initialize-capabilities-missing" => WriteEnvelopeAsync(
                output,
                id,
                new { protocolVersion = McpStdioClient.ProtocolVersion }),
            "initialize-capabilities-scalar" => WriteEnvelopeAsync(
                output,
                id,
                new { protocolVersion = McpStdioClient.ProtocolVersion, capabilities = 1 }),
            _ => WriteEnvelopeAsync(output, id, InitializeResult()),
        };
    }

    private static object InitializeResult() => new
    {
        protocolVersion = McpStdioClient.ProtocolVersion,
        capabilities = new { },
    };

    private static object TextResult(object payload) => new
    {
        content = new[]
        {
            new { type = "text", text = JsonSerializer.Serialize(payload) },
        },
    };

    private static object ImageResult()
    {
        var data = PngEncoder.EncodeRgb24(new uint[256 * 240], 256, 240);
        return new
        {
            content = new[]
            {
                new { type = "image", mimeType = "image/png", data = Convert.ToBase64String(data) },
            },
        };
    }

    private static async Task WriteEnvelopeAsync(
        Stream output,
        int id,
        object? result = null,
        object? error = null,
        bool includeJsonRpc = true,
        string jsonRpc = "2.0")
    {
        using var message = new MemoryStream();
        using (var writer = new Utf8JsonWriter(message))
        {
            writer.WriteStartObject();
            if (includeJsonRpc)
            {
                writer.WriteString("jsonrpc", jsonRpc);
            }

            writer.WriteNumber("id", id);
            if (result is not null)
            {
                writer.WritePropertyName("result");
                JsonSerializer.Serialize(writer, result, result.GetType());
            }

            if (error is not null)
            {
                writer.WritePropertyName("error");
                JsonSerializer.Serialize(writer, error, error.GetType());
            }

            writer.WriteEndObject();
        }

        message.WriteByte((byte)'\n');
        await output.WriteAsync(message.ToArray()).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<TestChildRequest?> ReadRequestAsync(Stream input)
    {
        using var retained = new MemoryStream();
        var buffer = new byte[1024];
        while (retained.Length <= MaximumRequestBytes)
        {
            var read = await input.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                try
                {
                    return JsonSerializer.Deserialize<TestChildRequest>(retained.ToArray());
                }
                catch (JsonException)
                {
                    return null;
                }
            }

            retained.Write(buffer, 0, read);
        }

        return null;
    }

    private static async Task WriteRepeatedAsync(Stream stream, byte value, int count)
    {
        if (count is < 0 or > 4 * 1024 * 1024)
        {
            return;
        }

        var buffer = new byte[Math.Min(count, 4096)];
        buffer.AsSpan().Fill(value);
        var remaining = count;
        while (remaining > 0)
        {
            var length = Math.Min(remaining, buffer.Length);
            await stream.WriteAsync(buffer.AsMemory(0, length)).ConfigureAwait(false);
            remaining -= length;
        }

        await stream.FlushAsync().ConfigureAwait(false);
    }
}
