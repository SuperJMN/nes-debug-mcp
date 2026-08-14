using Nes.Corpus.Qualification;

namespace Nes.Debug.Tests;

[Collection(NesDebugSessionCollection.Name)]
public sealed class McpQualificationSmokeTests
{
    [Fact]
    public async Task Generated_nrom_passes_the_real_default_stdio_smoke()
    {
        var program = new List<byte>();
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2000, 0x80));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2005, 0x01));
        program.AddRange([0xE6, 0x00, 0x4C, 0x00, 0x80]);
        var fixture = NromTestRomBuilder.CreateProgram(program.ToArray());
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await McpQualificationSmoke.RunAsync(
            FindServerAssembly(),
            rom.Path,
            rom.Path + ".state",
            headerMapper: 0,
            new QualificationBounds(30, 10, 1024 * 1024, 2, 100, 16),
            cancellation.Token);

        Assert.True(result.Passed, result.FailureCategory?.ToString());
        Assert.Null(result.FailureCategory);
        Assert.True(WorkerProtocol.IsSafeVersion(result.BackendVersion));
        Assert.True(WorkerProtocol.IsSafeVersion(result.ServerVersion));
        Assert.False(File.Exists(rom.Path + ".state"));
    }

    [Fact]
    public async Task Generated_mmc3_with_high_bank_values_passes_the_real_stdio_smoke()
    {
        using var rom = TemporaryTestFile.FromBytes(CreateHighBankMmc3());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await McpQualificationSmoke.RunAsync(
            FindServerAssembly(),
            rom.Path,
            rom.Path + ".state",
            headerMapper: 4,
            new QualificationBounds(30, 10, 1024 * 1024, 2, 100, 16),
            cancellation.Token);

        Assert.True(result.Passed, result.FailureCategory?.ToString());
        Assert.Null(result.FailureCategory);
    }

    [Fact]
    public async Task Mmc3_high_bank_values_wrap_to_the_available_prg_and_chr_pages_over_stdio()
    {
        using var rom = TemporaryTestFile.FromBytes(CreateHighBankMmc3());
        await using var client = McpStdioClient.Start(FindServerAssembly());
        Assert.NotNull(client);
        Assert.True(await client.InitializeAsync(CancellationToken.None));
        Assert.True((await client.CallJsonAsync("load_rom", new { path = rom.Path }, CancellationToken.None)).IsSuccess);

        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8000", bytes = new[] { 0x06 } }, CancellationToken.None)).IsSuccess);
        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8001", bytes = new[] { 0xFF } }, CancellationToken.None)).IsSuccess);
        var prgR6 = await client.CallJsonAsync("read_memory", new { address = "0x8100", length = 1 }, CancellationToken.None);
        Assert.True(prgR6.IsSuccess, prgR6.Failure.ToString());
        Assert.Equal("83", prgR6.Payload.GetProperty("bytesHex").GetString());

        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8000", bytes = new[] { 0x07 } }, CancellationToken.None)).IsSuccess);
        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8001", bytes = new[] { 0xFE } }, CancellationToken.None)).IsSuccess);
        var prgR7 = await client.CallJsonAsync("read_memory", new { address = "0xA100", length = 1 }, CancellationToken.None);
        Assert.True(prgR7.IsSuccess, prgR7.Failure.ToString());
        Assert.Equal("82", prgR7.Payload.GetProperty("bytesHex").GetString());

        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8000", bytes = new[] { 0x46 } }, CancellationToken.None)).IsSuccess);
        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8001", bytes = new[] { 0xFD } }, CancellationToken.None)).IsSuccess);
        var reversedPrgR6 = await client.CallJsonAsync("read_memory", new { address = "0xC100", length = 1 }, CancellationToken.None);
        Assert.True(reversedPrgR6.IsSuccess, reversedPrgR6.Failure.ToString());
        Assert.Equal("81", reversedPrgR6.Payload.GetProperty("bytesHex").GetString());

        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8000", bytes = new[] { 0x47 } }, CancellationToken.None)).IsSuccess);
        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8001", bytes = new[] { 0xFC } }, CancellationToken.None)).IsSuccess);
        var reversedPrgR7 = await client.CallJsonAsync("read_memory", new { address = "0xA100", length = 1 }, CancellationToken.None);
        Assert.True(reversedPrgR7.IsSuccess, reversedPrgR7.Failure.ToString());
        Assert.Equal("80", reversedPrgR7.Payload.GetProperty("bytesHex").GetString());

        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8000", bytes = new[] { 0x00 } }, CancellationToken.None)).IsSuccess);
        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8001", bytes = new[] { 0xFE } }, CancellationToken.None)).IsSuccess);
        var chrR0 = await client.CallJsonAsync("dump_tileset", new { address = "0x0000", tileCount = 1 }, CancellationToken.None);
        Assert.True(chrR0.IsSuccess, chrR0.Failure.ToString());
        Assert.Equal("A6 A6 A6 A6 A6 A6 A6 A6 A6 A6 A6 A6 A6 A6 A6 A6", chrR0.Payload.GetProperty("tiles")[0].GetProperty("bytesHex").GetString());

        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8000", bytes = new[] { 0x02 } }, CancellationToken.None)).IsSuccess);
        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8001", bytes = new[] { 0xFF } }, CancellationToken.None)).IsSuccess);
        var chrR2 = await client.CallJsonAsync("dump_tileset", new { address = "0x1000", tileCount = 1 }, CancellationToken.None);
        Assert.True(chrR2.IsSuccess, chrR2.Failure.ToString());
        Assert.Equal("A7 A7 A7 A7 A7 A7 A7 A7 A7 A7 A7 A7 A7 A7 A7 A7", chrR2.Payload.GetProperty("tiles")[0].GetProperty("bytesHex").GetString());

        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8000", bytes = new[] { 0x80 } }, CancellationToken.None)).IsSuccess);
        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8001", bytes = new[] { 0xFE } }, CancellationToken.None)).IsSuccess);
        var reversedChrR0 = await client.CallJsonAsync("dump_tileset", new { address = "0x1000", tileCount = 1 }, CancellationToken.None);
        Assert.True(reversedChrR0.IsSuccess, reversedChrR0.Failure.ToString());
        Assert.Equal("A6 A6 A6 A6 A6 A6 A6 A6 A6 A6 A6 A6 A6 A6 A6 A6", reversedChrR0.Payload.GetProperty("tiles")[0].GetProperty("bytesHex").GetString());

        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8000", bytes = new[] { 0x82 } }, CancellationToken.None)).IsSuccess);
        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8001", bytes = new[] { 0xFF } }, CancellationToken.None)).IsSuccess);
        var reversedChrR2 = await client.CallJsonAsync("dump_tileset", new { address = "0x0000", tileCount = 1 }, CancellationToken.None);
        Assert.True(reversedChrR2.IsSuccess, reversedChrR2.Failure.ToString());
        Assert.Equal("A7 A7 A7 A7 A7 A7 A7 A7 A7 A7 A7 A7 A7 A7 A7 A7", reversedChrR2.Payload.GetProperty("tiles")[0].GetProperty("bytesHex").GetString());

        Assert.True(await client.StopAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Mmc3_high_bank_values_mirror_across_non_power_of_two_page_counts()
    {
        using var rom = TemporaryTestFile.FromBytes(CreateHighBankMmc3(prgBankCount: 3, chrBankCount: 3));
        await using var client = McpStdioClient.Start(FindServerAssembly());
        Assert.NotNull(client);
        Assert.True(await client.InitializeAsync(CancellationToken.None));
        Assert.True((await client.CallJsonAsync("load_rom", new { path = rom.Path }, CancellationToken.None)).IsSuccess);

        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8000", bytes = new[] { 0x06 } }, CancellationToken.None)).IsSuccess);
        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8001", bytes = new[] { 0xFF } }, CancellationToken.None)).IsSuccess);
        var prg = await client.CallJsonAsync("read_memory", new { address = "0x8100", length = 1 }, CancellationToken.None);
        Assert.True(prg.IsSuccess, prg.Failure.ToString());
        Assert.Equal("83", prg.Payload.GetProperty("bytesHex").GetString());

        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8000", bytes = new[] { 0x00 } }, CancellationToken.None)).IsSuccess);
        Assert.True((await client.CallJsonAsync("write_memory", new { address = "0x8001", bytes = new[] { 0xFE } }, CancellationToken.None)).IsSuccess);
        var chr = await client.CallJsonAsync("dump_tileset", new { address = "0x0000", tileCount = 1 }, CancellationToken.None);
        Assert.True(chr.IsSuccess, chr.Failure.ToString());
        Assert.Equal("AE AE AE AE AE AE AE AE AE AE AE AE AE AE AE AE", chr.Payload.GetProperty("tiles")[0].GetProperty("bytesHex").GetString());

        Assert.True(await client.StopAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(255, 6, 0x3F, 3)]
    [InlineData(254, 24, 0xFF, 14)]
    public void Mmc3_variants_inherit_non_power_of_two_page_normalization(
        int bank,
        int pageCount,
        int mask,
        int expected)
    {
        Assert.Equal(expected, Mapper004RevAProbe.Normalize(bank, pageCount, mask));
        Assert.Equal(expected, Mapper004Mmc6Probe.Normalize(bank, pageCount, mask));
    }

    private static byte[] CreateHighBankMmc3(int prgBankCount = 2, int chrBankCount = 1)
    {
        const int headerSize = 16;
        var prgSize = prgBankCount * 16 * 1024;
        var chrSize = chrBankCount * 8 * 1024;
        var rom = new byte[headerSize + prgSize + chrSize];
        rom[0] = (byte)'N';
        rom[1] = (byte)'E';
        rom[2] = (byte)'S';
        rom[3] = 0x1A;
        rom[4] = (byte)prgBankCount;
        rom[5] = (byte)chrBankCount;
        rom[6] = 0x40;

        byte[] program =
        [
            0x78,                         // SEI
            0xA9, 0x06, 0x8D, 0x00, 0x80, // Select PRG R6
            0xA9, 0xFF, 0x8D, 0x01, 0x80, // Select its highest value
            0xA9, 0x07, 0x8D, 0x00, 0x80, // Select PRG R7
            0xA9, 0xFE, 0x8D, 0x01, 0x80, // Select another high value
            0xA9, 0x00, 0x8D, 0x00, 0x80, // Select CHR R0
            0xA9, 0xFE, 0x8D, 0x01, 0x80, // Select a high 2 KiB bank
            0xA9, 0x02, 0x8D, 0x00, 0x80, // Select CHR R2
            0xA9, 0xFF, 0x8D, 0x01, 0x80, // Select a high 1 KiB bank
            0xA9, 0x00, 0x8D, 0x00, 0x20, // Pattern table at $0000
            0xA9, 0x08, 0x8D, 0x01, 0x20, // Enable background rendering
            0x4C, 0x33, 0xE0,             // Loop in the fixed last bank
        ];
        var prg = rom.AsSpan(headerSize, prgSize);
        for (var bank = 0; bank < prgBankCount * 2; bank++)
        {
            prg.Slice(bank * 0x2000, 0x2000).Fill(0xEA);
            prg[bank * 0x2000 + 0x100] = (byte)(0x80 + bank);
        }

        program.CopyTo(prg[^0x2000..]);
        prg[^6] = 0x00;
        prg[^5] = 0xE0;
        prg[^4] = 0x00;
        prg[^3] = 0xE0;
        prg[^2] = 0x00;
        prg[^1] = 0xE0;
        var chr = rom.AsSpan(headerSize + prgSize, chrSize);
        for (var bank = 0; bank < chrBankCount * 8; bank++)
        {
            chr.Slice(bank * 0x400, 0x400).Fill((byte)(0xA0 + bank));
        }

        return rom;
    }

    private sealed class Mapper004RevAProbe : AprNes.Mapper004RevA
    {
        public static int Normalize(int bank, int pageCount, int mask) =>
            NormalizeBankPage(bank, pageCount, mask);
    }

    private sealed class Mapper004Mmc6Probe : AprNes.Mapper004MMC6
    {
        public static int Normalize(int bank, int pageCount, int mask) =>
            NormalizeBankPage(bank, pageCount, mask);
    }

    private static string FindServerAssembly()
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new DirectoryNotFoundException("Could not determine test build configuration.");
        var path = Path.Combine(
            repositoryRoot,
            "src",
            "Nes.Debug.Mcp",
            "bin",
            configuration,
            "net10.0",
            "Nes.Mcp.dll");
        return File.Exists(path) ? path : throw new FileNotFoundException("MCP server assembly is unavailable.");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "nes-debug-mcp.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
