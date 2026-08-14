using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.IO.Compression;
using Nes.Corpus.Qualification;
using Nes.Debug.Core;

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
    public async Task Client_drives_full_stdio_protocol_and_discards_hostile_stderr()
    {
        var startInfo = QualificationTestChild.CreateMcpStartInfo("valid");
        await using var client = McpStdioClient.Start(startInfo);
        Assert.NotNull(client);

        Assert.True(await client.InitializeAsync(CancellationToken.None));
        var json = await client.CallJsonAsync("json", new { }, CancellationToken.None);
        var error = await client.CallJsonAsync("error", new { }, CancellationToken.None);
        var image = await client.CallImageAsync("image", new { }, CancellationToken.None);

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
        var startInfo = QualificationTestChild.CreateMcpStartInfo("oversize");
        await using var client = McpStdioClient.Start(startInfo);
        Assert.NotNull(client);

        Assert.False(await client.InitializeAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("missing-jsonrpc")]
    [InlineData("wrong-jsonrpc")]
    [InlineData("wrong-id")]
    [InlineData("result-and-error")]
    [InlineData("neither-result-nor-error")]
    [InlineData("initialize-scalar")]
    [InlineData("initialize-version-mismatch")]
    [InlineData("initialize-capabilities-missing")]
    [InlineData("initialize-capabilities-scalar")]
    public async Task Client_rejects_invalid_json_rpc_initialize_envelopes(string mode)
    {
        var startInfo = QualificationTestChild.CreateMcpStartInfo(mode);
        await using var client = McpStdioClient.Start(startInfo);
        Assert.NotNull(client);

        Assert.False(await client.InitializeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Client_distinguishes_a_server_crash_from_clean_end_of_output()
    {
        var startInfo = QualificationTestChild.CreateMcpStartInfo("crash");
        await using var client = McpStdioClient.Start(startInfo);
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
        var startInfo = QualificationTestChild.CreateMcpStartInfo("hang-after-input");
        await using var client = McpStdioClient.Start(startInfo);
        Assert.NotNull(client);
        Assert.True(await client.InitializeAsync(CancellationToken.None));
        var response = await client.CallJsonAsync("pid", new { }, CancellationToken.None);
        Assert.True(response.IsSuccess);
        var processId = response.Payload.GetProperty("pid").GetInt32();
        var stopwatch = Stopwatch.StartNew();

        Assert.False(await client.StopAsync(CancellationToken.None));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        AssertProcessExited(processId);
    }

    [Fact]
    public async Task Stop_rejects_trailing_non_mcp_stdout_even_when_server_exits_successfully()
    {
        var startInfo = QualificationTestChild.CreateMcpStartInfo("trailing-noise");
        await using var client = McpStdioClient.Start(startInfo);
        Assert.NotNull(client);
        Assert.True(await client.InitializeAsync(CancellationToken.None));

        Assert.False(await client.StopAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Stop_accepts_a_trailing_mcp_notification_before_end_of_output()
    {
        var startInfo = QualificationTestChild.CreateMcpStartInfo("trailing-notification");
        await using var client = McpStdioClient.Start(startInfo);
        Assert.NotNull(client);
        Assert.True(await client.InitializeAsync(CancellationToken.None));

        Assert.True(await client.StopAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Dispose_is_bounded_and_kills_a_server_that_ignores_closed_input()
    {
        var startInfo = QualificationTestChild.CreateMcpStartInfo("hang-after-input");
        var client = McpStdioClient.Start(startInfo);
        Assert.NotNull(client);
        Assert.True(await client.InitializeAsync(CancellationToken.None));
        var response = await client.CallJsonAsync("pid", new { }, CancellationToken.None);
        Assert.True(response.IsSuccess);
        var processId = response.Payload.GetProperty("pid").GetInt32();
        var stopwatch = Stopwatch.StartNew();

        await client.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
        AssertProcessExited(processId);
    }

    [Theory]
    [InlineData(256, 240, true)]
    [InlineData(255, 240, false)]
    [InlineData(256, 239, false)]
    public void Png_validation_requires_nes_dimensions(int width, int height, bool expected)
    {
        var bytes = PngEncoder.EncodeRgb24(new uint[width * height], width, height);

        Assert.Equal(expected, PngValidator.IsNesFrame(bytes));
    }

    [Fact]
    public void Png_validation_rejects_truncated_crc_corrupt_and_nonfinal_iend_images()
    {
        var valid = PngEncoder.EncodeRgb24(new uint[256 * 240], 256, 240);
        var crcCorrupt = (byte[])valid.Clone();
        crcCorrupt[29] ^= 0x01;
        var trailing = new byte[valid.Length + 1];
        valid.CopyTo(trailing, 0);

        Assert.True(PngValidator.IsNesFrame(valid));
        Assert.False(PngValidator.IsNesFrame(valid[..^1]));
        Assert.False(PngValidator.IsNesFrame(crcCorrupt));
        Assert.False(PngValidator.IsNesFrame(trailing));

        Assert.False(PngValidator.IsNesFrame(CreateInvalidFilterPng(valid)));
        Assert.False(PngValidator.IsNesFrame(CreateReopenedIdatPng(valid)));
    }

    private static int FindChunk(byte[] png, ReadOnlySpan<byte> type)
    {
        for (var offset = 8; offset <= png.Length - 12;)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (png.AsSpan(offset + 4, 4).SequenceEqual(type))
            {
                return offset;
            }

            offset += 12 + length;
        }

        throw new InvalidDataException();
    }

    private static byte[] CreateInvalidFilterPng(byte[] png)
    {
        var idatOffset = FindChunk(png, "IDAT"u8);
        var idatLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(idatOffset, 4));
        using var decoded = new MemoryStream();
        using (var source = new MemoryStream(png, idatOffset + 8, idatLength, writable: false))
        using (var zlib = new ZLibStream(source, CompressionMode.Decompress))
        {
            zlib.CopyTo(decoded);
        }

        decoded.GetBuffer()[0] = 5;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(decoded.GetBuffer(), 0, (int)decoded.Length);
        }

        return RebuildPng(png, [compressed.ToArray()]);
    }

    private static byte[] CreateReopenedIdatPng(byte[] png)
    {
        var idatOffset = FindChunk(png, "IDAT"u8);
        var idatLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(idatOffset, 4));
        return RebuildPng(png, [png.AsSpan(idatOffset + 8, idatLength).ToArray(), null, []]);
    }

    private static byte[] RebuildPng(byte[] original, IReadOnlyList<byte[]?> idatParts)
    {
        using var rebuilt = new MemoryStream();
        rebuilt.Write(original, 0, 8);
        var ihdrOffset = FindChunk(original, "IHDR"u8);
        var ihdrLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(original.AsSpan(ihdrOffset, 4));
        rebuilt.Write(original, ihdrOffset, ihdrLength + 12);
        foreach (var part in idatParts)
        {
            WriteChunk(rebuilt, part is null ? "tEXt"u8 : "IDAT"u8, part ?? []);
        }

        WriteChunk(rebuilt, "IEND"u8, []);
        return rebuilt.ToArray();
    }

    private static void WriteChunk(Stream target, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        target.Write(length);
        target.Write(type);
        target.Write(payload);
        Span<byte> crc = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, CalculateCrc(type, payload));
        target.Write(crc);
    }

    private static uint CalculateCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in type.ToArray().Concat(payload.ToArray()))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
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

}
