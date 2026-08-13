using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Nes.Corpus.Qualification;

namespace Nes.Debug.Tests;

public sealed class McpStdioClientTests
{
    [Fact]
    public async Task Bounded_line_reader_preserves_multiple_json_lines()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":1}\n{\"id\":2}\r\n"));
        var reader = new BoundedLineReader(stream, 64);

        var first = await reader.ReadLineAsync(CancellationToken.None);
        var second = await reader.ReadLineAsync(CancellationToken.None);
        var end = await reader.ReadLineAsync(CancellationToken.None);

        Assert.Equal("{\"id\":1}", Encoding.UTF8.GetString(first!));
        Assert.Equal("{\"id\":2}", Encoding.UTF8.GetString(second!));
        Assert.Null(end);
        Assert.False(reader.Overflow);
    }

    [Fact]
    public async Task Bounded_line_reader_rejects_an_oversize_response_without_retaining_it()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 65) + "\n"));
        var reader = new BoundedLineReader(stream, 64);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync(CancellationToken.None));
        Assert.True(reader.Overflow);
    }

    [Fact]
    public async Task Client_drives_full_stdio_protocol_with_forced_backend_and_discards_hostile_stderr()
    {
        var startInfo = StartPython(FakeServerScript);
        await using var client = McpStdioClient.Start(startInfo, QualificationBackend.AprNes);
        Assert.NotNull(client);

        Assert.True(await client.InitializeAsync(CancellationToken.None));
        var backend = await client.CallJsonAsync("backend", new { }, CancellationToken.None);
        var json = await client.CallJsonAsync("json", new { }, CancellationToken.None);
        var error = await client.CallJsonAsync("error", new { }, CancellationToken.None);
        var image = await client.CallImageAsync("image", new { }, CancellationToken.None);

        Assert.True(backend.IsSuccess);
        Assert.Equal("aprnes", backend.Payload.GetProperty("backend").GetString());
        Assert.True(json.IsSuccess);
        Assert.Equal(7, json.Payload.GetProperty("value").GetInt32());
        Assert.False(error.IsSuccess);
        Assert.True(image.IsSuccess);
        Assert.Equal("image/png", image.MimeType);
        Assert.True(PngValidator.IsNesFrame(image.Data));
        Assert.True(await client.StopAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Client_rejects_oversize_server_stdout_as_a_protocol_violation()
    {
        var startInfo = StartPython(
            "import sys\n" +
            "sys.stdin.readline()\n" +
            "sys.stdout.write('x' * (2 * 1024 * 1024 + 1) + '\\n')\n" +
            "sys.stdout.flush()\n" +
            "sys.stdin.read()\n");
        await using var client = McpStdioClient.Start(startInfo, QualificationBackend.AprNes);
        Assert.NotNull(client);

        Assert.False(await client.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Client_distinguishes_a_server_crash_from_clean_end_of_output()
    {
        var startInfo = StartPython(
            "import json, sys\n" +
            "message = json.loads(sys.stdin.readline())\n" +
            "sys.stdout.write(json.dumps({'jsonrpc':'2.0','id':message['id'],'result':{'protocolVersion':'2025-06-18','capabilities':{}}}) + '\\n')\n" +
            "sys.stdout.flush()\n" +
            "sys.stdin.readline()\n" +
            "sys.stdin.readline()\n" +
            "sys.exit(23)\n");
        await using var client = McpStdioClient.Start(startInfo, QualificationBackend.AprNes);
        Assert.NotNull(client);

        Assert.True(await client.InitializeAsync(CancellationToken.None));
        var call = await client.CallJsonAsync("crash", new { }, CancellationToken.None);

        Assert.False(call.IsSuccess);
        Assert.Equal(McpCallFailure.ServerCrash, call.Failure);
    }

    [Fact]
    public async Task Closing_server_input_is_best_effort()
    {
        await McpStdioClient.CloseInputAsync(new ThrowingDisposeStream());
    }

    [Fact]
    public async Task Stop_kills_a_server_that_stays_alive_after_input_closes()
    {
        var startInfo = StartPython(HangingAfterInputScript);
        await using var client = McpStdioClient.Start(startInfo, QualificationBackend.AprNes);
        Assert.NotNull(client);
        Assert.True(await client.InitializeAsync(CancellationToken.None));
        var response = await client.CallJsonAsync("pid", new { }, CancellationToken.None);
        Assert.True(response.IsSuccess);
        var processId = response.Payload.GetProperty("pid").GetInt32();

        Assert.False(await client.StopAsync(CancellationToken.None));
        AssertProcessExited(processId);
    }

    [Theory]
    [InlineData(256, 240, true)]
    [InlineData(255, 240, false)]
    [InlineData(256, 239, false)]
    public void Png_validation_requires_nes_dimensions(int width, int height, bool expected)
    {
        var bytes = CreatePngHeader(width, height);

        Assert.Equal(expected, PngValidator.IsNesFrame(bytes));
    }

    private static ProcessStartInfo StartPython(string script)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/python3");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        return startInfo;
    }

    private static byte[] CreatePngHeader(int width, int height)
    {
        var bytes = new byte[33];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        bytes[11] = 13;
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);
        bytes[24] = 8;
        bytes[25] = 2;
        return bytes;
    }

    private static void WriteUInt32BigEndian(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private static void AssertProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.True(process.HasExited);
        }
        catch (ArgumentException)
        {
        }
    }

    private sealed class ThrowingDisposeStream : MemoryStream
    {
        public override ValueTask DisposeAsync() => ValueTask.FromException(new IOException("synthetic"));
    }

    private const string HangingAfterInputScript = """
import json
import os
import sys
import time

for line in sys.stdin:
    message = json.loads(line)
    method = message['method']
    if method == 'notifications/initialized':
        continue
    request_id = message['id']
    if method == 'initialize':
        result = {'protocolVersion': '2025-06-18', 'capabilities': {}}
    else:
        result = {'content': [{'type': 'text', 'text': json.dumps({'pid': os.getpid()})}]}
    sys.stdout.write(json.dumps({'jsonrpc': '2.0', 'id': request_id, 'result': result}) + '\n')
    sys.stdout.flush()

time.sleep(30)
""";

    private const string FakeServerScript = """
import base64
import json
import os
import struct
import sys

sys.stderr.write('hostile-private-stderr\n')
sys.stderr.flush()
expected_id = 1
initialized = False
png = bytes([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a])
png += struct.pack('>I', 13) + b'IHDR' + struct.pack('>II', 256, 240)
png += bytes([8, 2, 0, 0, 0]) + bytes(4)

for line in sys.stdin:
    message = json.loads(line)
    method = message['method']
    if method == 'notifications/initialized':
        if not initialized:
            raise RuntimeError('notification before initialize')
        continue

    request_id = message['id']
    if request_id != expected_id:
        raise RuntimeError('unexpected request id')
    expected_id += 1

    if method == 'initialize':
        initialized = True
        result = {'protocolVersion': '2025-06-18', 'capabilities': {}}
    elif method == 'tools/call':
        if not initialized:
            raise RuntimeError('tool before initialize')
        name = message['params']['name']
        if name == 'backend':
            content = {'type': 'text', 'text': json.dumps({'backend': os.environ.get('NES_MCP_EMULATOR_BACKEND')})}
            result = {'content': [content]}
        elif name == 'json':
            result = {'content': [{'type': 'text', 'text': json.dumps({'value': 7})}]}
        elif name == 'error':
            result = {'isError': True, 'content': [{'type': 'text', 'text': json.dumps({'error': {'code': 'hostile'}})}]}
        elif name == 'image':
            result = {'content': [{'type': 'image', 'mimeType': 'image/png', 'data': base64.b64encode(png).decode()}]}
        else:
            raise RuntimeError('unknown tool')
    else:
        raise RuntimeError('unknown method')

    sys.stdout.write(json.dumps({'jsonrpc': '2.0', 'id': request_id, 'result': result}) + '\n')
    sys.stdout.flush()
""";
}
