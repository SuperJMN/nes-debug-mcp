using Nes.Debug.Core;
using Nes.Debug.Emulator;
using System.Security.Cryptography;
using System.Text.Json;

namespace Nes.Debug.Tests;

[Collection(NesDebugSessionCollection.Name)]
public sealed class AprNesMmc1SessionConformanceTests : NesDebugSessionConformanceTests<AprNesDebugSession>
{
    protected override AprNesDebugSession CreateSession() => new();

    protected override bool SupportsContinuousObservation => true;

    protected override DebugContractRom CreateContractRom(
        byte[] program,
        int prgRomBanks = 2,
        int chrRomBanks = 1,
        NromMirroring mirroring = NromMirroring.Horizontal) =>
        Mmc1TestRomBuilder.CreateContractProgram(program, prgRomBanks, chrRomBanks, mirroring);

    [Theory]
    [InlineData(0, 1, 0, 1)]
    [InlineData(0, 3, 2, 3)]
    [InlineData(1, 2, 2, 3)]
    [InlineData(2, 1, 0, 1)]
    [InlineData(2, 2, 0, 2)]
    [InlineData(2, 3, 0, 3)]
    [InlineData(3, 0, 0, 3)]
    [InlineData(3, 1, 1, 3)]
    [InlineData(3, 2, 2, 3)]
    public void Cpu_reads_after_serial_writes_expose_each_prg_bank_in_every_supported_mode(
        int prgMode,
        int selectedBank,
        int expectedLowerBank,
        int expectedUpperBank)
    {
        var fixture = Mmc1TestRomBuilder.CreateBankedFixture();
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        var load = session.LoadRom(rom.Path);
        AssertSuccess(load);
        Assert.Equal(1, load.Value.Mapper);
        Assert.Equal(4, load.Value.PrgRomBanks);
        Assert.Equal(2, load.Value.ChrRomBanks);

        WriteSerial(session, 0x8000, (byte)(0x02 | prgMode << 2));
        WriteSerial(session, 0xE000, (byte)selectedBank);

        var lower = session.ReadMemory(0xBFF0, 1);
        var upper = session.ReadMemory(0xFFF0, 1);
        AssertSuccess(lower);
        AssertSuccess(upper);
        Assert.Equal(fixture.PrgSentinels[expectedLowerBank], lower.Value.Bytes[0]);
        Assert.Equal(fixture.PrgSentinels[expectedUpperBank], upper.Value.Bytes[0]);
    }

    [Fact]
    public void Prg_register_commits_only_after_the_fifth_serial_write()
    {
        var fixture = Mmc1TestRomBuilder.CreateBankedFixture();
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        AssertSuccess(session.LoadRom(rom.Path));
        WriteSerial(session, 0x8000, 0x0E);

        WriteSerialPrefix(session, 0xE000, 0x01, bitCount: 4);
        var beforeCommit = session.ReadMemory(0xBFF0, 1);
        AssertSuccess(beforeCommit);
        Assert.Equal(fixture.PrgSentinels[2], beforeCommit.Value.Bytes[0]);

        AssertSuccess(session.WriteMemory(0xE000, [0x00]));
        var afterCommit = session.ReadMemory(0xBFF0, 1);
        AssertSuccess(afterCommit);
        Assert.Equal(fixture.PrgSentinels[1], afterCommit.Value.Bytes[0]);
    }

    [Fact]
    public void Ppu_reads_follow_8k_and_independent_4k_chr_banks()
    {
        var fixture = Mmc1TestRomBuilder.CreateBankedFixture();
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        AssertSuccess(session.LoadRom(rom.Path));

        AssertChrBanks(session, fixture, expectedLower4KBank: 0, expectedUpper4KBank: 1);

        WriteSerial(session, 0x8000, 0x0E); // 8 KiB CHR, PRG mode 3, vertical.
        WriteSerial(session, 0xA000, 0x03); // Low bit is ignored: select 8 KiB bank 1.
        AssertChrBanks(session, fixture, expectedLower4KBank: 2, expectedUpper4KBank: 3);

        WriteSerial(session, 0x8000, 0x1E); // Independent 4 KiB CHR banks.
        WriteSerial(session, 0xA000, 0x01);
        WriteSerial(session, 0xC000, 0x02);
        AssertChrBanks(session, fixture, expectedLower4KBank: 1, expectedUpper4KBank: 2);
    }

    [Fact]
    public void Prg_ram_remains_readable_and_writable_across_bank_changes()
    {
        var fixture = Mmc1TestRomBuilder.CreateBankedFixture();
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        AssertSuccess(session.LoadRom(rom.Path));
        AssertSuccess(session.WriteMemory(0x6000, [0x5A, 0xA5]));
        WriteSerial(session, 0x8000, 0x1E);
        WriteSerial(session, 0xA000, 0x01);
        WriteSerial(session, 0xC000, 0x02);
        WriteSerial(session, 0xE000, 0x01);

        var ram = session.ReadMemory(0x6000, 2);
        AssertSuccess(ram);
        Assert.Equal("5A A5", ram.Value.BytesHex);
    }

    [Theory]
    [InlineData(0, "33", "33", "33", "33")]
    [InlineData(1, "44", "44", "44", "44")]
    [InlineData(2, "33", "44", "33", "44")]
    [InlineData(3, "33", "33", "44", "44")]
    public void Mapper_control_selects_each_mirroring_mode(
        int mirroring,
        string first,
        string second,
        string third,
        string fourth)
    {
        var program = new List<byte>();
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2000, 0x11));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2400, 0x22));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2800, 0x33));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2C00, 0x44));
        var loop = (ushort)(0xC000 + program.Count);
        program.AddRange([0x4C, (byte)loop, (byte)(loop >> 8)]);
        var fixture = Mmc1TestRomBuilder.CreateBankedFixture(program.ToArray());
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        AssertSuccess(session.LoadRom(rom.Path));
        AssertSuccess(session.StepInstruction(25));
        WriteSerial(session, 0x8000, (byte)(0x0C | mirroring));

        Assert.Equal(
            new[] { first, second, third, fourth },
            ReadNametableFirstBytes(session));
    }

    [Fact]
    public void Savestate_restores_partial_shift_banks_mirroring_prg_ram_cpu_ppu_and_timeline()
    {
        var fixture = Mmc1TestRomBuilder.CreateBankedFixture(CreateObservableLoop());
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var state = TemporaryTestFile.Empty("nesstate");
        using var session = CreateSession();

        AssertSuccess(session.LoadRom(rom.Path));
        WriteSerial(session, 0x8000, 0x1E);
        WriteSerial(session, 0xA000, 0x01);
        WriteSerial(session, 0xC000, 0x02);
        WriteSerial(session, 0xE000, 0x01);
        AssertSuccess(session.WriteMemory(0x6000, [0x5A]));
        AssertSuccess(session.RunFrame(1));
        WriteSerialPrefix(session, 0xE000, 0x02, bitCount: 3);

        var saved = CaptureSnapshot(session);
        AssertSuccess(session.SaveState(state.Path));

        WriteSerialSuffix(session, 0xE000, 0x02, startBit: 3);
        AssertSuccess(session.RunFrame(1));
        var firstFuture = CaptureSnapshot(session);
        Assert.NotEqual(saved.Timeline, firstFuture.Timeline);
        Assert.NotEqual(saved, firstFuture);
        Assert.Equal($"{fixture.PrgSentinels[2]:X2} {fixture.PrgSentinels[3]:X2}", firstFuture.PrgBanks);

        WriteSerial(session, 0x8000, 0x1F);
        WriteSerial(session, 0xA000, 0x03);
        WriteSerial(session, 0xC000, 0x00);
        WriteSerial(session, 0xE000, 0x00);
        AssertSuccess(session.WriteMemory(0x6000, [0xEE]));
        AssertSuccess(session.RunFrame(1));
        var mutated = CaptureSnapshot(session);
        Assert.NotEqual(saved.PrgRam, mutated.PrgRam);
        Assert.NotEqual(saved.PrgBanks, mutated.PrgBanks);
        Assert.NotEqual(saved.ChrBanks, mutated.ChrBanks);
        Assert.NotEqual(saved.NametablesHash, mutated.NametablesHash);

        AssertSuccess(session.LoadState(state.Path));
        var restored = CaptureSnapshot(session);
        Assert.Equal(saved.NametablesHash, restored.NametablesHash);
        Assert.Equal(saved, restored);

        WriteSerialSuffix(session, 0xE000, 0x02, startBit: 3);
        AssertSuccess(session.RunFrame(1));
        Assert.Equal(firstFuture, CaptureSnapshot(session));
    }

    [Fact]
    public void Repeated_bounded_frames_complete_with_fresh_mapper_state()
    {
        var fixture = Mmc1TestRomBuilder.CreateBankedFixture(CreateNametableInitializationProgram());
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        AssertSuccess(session.LoadRom(rom.Path));
        AssertSuccess(session.StepInstruction(25));
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var prgBank = iteration % 3;
            var lowerChrBank = iteration % 4;
            var upperChrBank = (iteration + 1) % 4;
            WriteSerial(session, 0x8000, (byte)(0x1C | iteration % 4));
            WriteSerial(session, 0xA000, (byte)lowerChrBank);
            WriteSerial(session, 0xC000, (byte)upperChrBank);
            WriteSerial(session, 0xE000, (byte)prgBank);

            var frame = session.RunFrame(1);
            AssertSuccess(frame);
            Assert.Equal(1, frame.Value.FramesRun);

            var lowerPrg = session.ReadMemory(0xBFF0, 1);
            AssertSuccess(lowerPrg);
            Assert.Equal(fixture.PrgSentinels[prgBank], lowerPrg.Value.Bytes[0]);
            AssertChrBanks(session, fixture, lowerChrBank, upperChrBank);
            Assert.Equal(ExpectedNametables(iteration % 4), ReadNametableFirstBytes(session));

            var current = session.GetState();
            AssertSuccess(current);
            Assert.Equal((ulong)iteration + 1, current.Value.Timeline.Frames);
        }
    }

    private static byte[] CreateNametableInitializationProgram()
    {
        var program = new List<byte>();
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2000, 0x11));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2400, 0x22));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2800, 0x33));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2C00, 0x44));
        var loop = (ushort)(0xC000 + program.Count);
        program.AddRange([0x4C, (byte)loop, (byte)(loop >> 8)]);
        return program.ToArray();
    }

    private static string[] ExpectedNametables(int mirroring) =>
        mirroring switch
        {
            0 => ["33", "33", "33", "33"],
            1 => ["44", "44", "44", "44"],
            2 => ["33", "44", "33", "44"],
            3 => ["33", "33", "44", "44"],
            _ => throw new ArgumentOutOfRangeException(nameof(mirroring)),
        };

    private static byte[] CreateObservableLoop()
    {
        var program = new List<byte>
        {
            0xE6, 0x00,       // INC $00
            0xA9, 0x00,
            0x8D, 0x03, 0x20, // OAMADDR = 0
            0xA5, 0x00,
            0x8D, 0x04, 0x20, // OAM[0] = counter
            0xA9, 0x20,
            0x8D, 0x06, 0x20,
            0xA9, 0x00,
            0x8D, 0x06, 0x20,
            0xA5, 0x00,
            0x8D, 0x07, 0x20, // nametable[0] = counter
            0x4C, 0x00, 0xC0,
        };
        return program.ToArray();
    }

    private static Mmc1Snapshot CaptureSnapshot(INesDebugSession session)
    {
        var registers = session.ReadRegisters();
        var ppu = session.ReadPpuState();
        var oam = session.ReadOam();
        var nametables = session.DumpNametables(includeDetails: true);
        var screen = session.CaptureScreen();
        var prgRam = session.ReadMemory(0x6000, 1);
        var lowerPrg = session.ReadMemory(0xBFF0, 1);
        var upperPrg = session.ReadMemory(0xFFF0, 1);
        var lowerChr = session.DumpTileset(0x0000, 1);
        var upperChr = session.DumpTileset(0x1000, 1);
        var state = session.GetState();

        AssertSuccess(registers);
        AssertSuccess(ppu);
        AssertSuccess(oam);
        AssertSuccess(nametables);
        AssertSuccess(screen);
        AssertSuccess(prgRam);
        AssertSuccess(lowerPrg);
        AssertSuccess(upperPrg);
        AssertSuccess(lowerChr);
        AssertSuccess(upperChr);
        AssertSuccess(state);

        return new Mmc1Snapshot(
            JsonSerializer.Serialize(registers.Value),
            JsonSerializer.Serialize(ppu.Value),
            HashCanonical(oam.Value.Sprites),
            HashCanonical(nametables.Value.Nametables),
            Convert.ToHexString(SHA256.HashData(screen.Value.Data)),
            prgRam.Value.BytesHex,
            $"{lowerPrg.Value.BytesHex} {upperPrg.Value.BytesHex}",
            $"{lowerChr.Value.Tiles[0].BytesHex} {upperChr.Value.Tiles[0].BytesHex}",
            state.Value.Timeline);
    }

    private static string HashCanonical<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private static string[] ReadNametableFirstBytes(INesDebugSession session) =>
        new[] { 0x2000, 0x2400, 0x2800, 0x2C00 }
            .Select(address => session.DumpTilemap((ushort)address))
            .Select(result =>
            {
                AssertSuccess(result);
                return FirstByteFromRow(result.Value.Rows[0]);
            })
            .ToArray();

    private static void AssertChrBanks(
        INesDebugSession session,
        Mmc1BankedTestRom fixture,
        int expectedLower4KBank,
        int expectedUpper4KBank)
    {
        var lower = session.DumpTileset(0x0000, 1);
        var upper = session.DumpTileset(0x1000, 1);
        AssertSuccess(lower);
        AssertSuccess(upper);
        Assert.Equal(
            RepeatedHex(fixture.Chr4KSentinels[expectedLower4KBank], 16),
            lower.Value.Tiles[0].BytesHex);
        Assert.Equal(
            RepeatedHex(fixture.Chr4KSentinels[expectedUpper4KBank], 16),
            upper.Value.Tiles[0].BytesHex);
    }

    private static string RepeatedHex(byte value, int count) =>
        string.Join(' ', Enumerable.Repeat($"{value:X2}", count));

    private static void WriteSerial(INesDebugSession session, ushort address, byte value) =>
        WriteSerialPrefix(session, address, value, bitCount: 5);

    private static void WriteSerialPrefix(
        INesDebugSession session,
        ushort address,
        byte value,
        int bitCount)
    {
        for (var bit = 0; bit < bitCount; bit++)
        {
            AssertSuccess(session.WriteMemory(address, [(byte)(value >> bit & 0x01)]));
        }
    }

    private static void WriteSerialSuffix(
        INesDebugSession session,
        ushort address,
        byte value,
        int startBit)
    {
        for (var bit = startBit; bit < 5; bit++)
        {
            AssertSuccess(session.WriteMemory(address, [(byte)(value >> bit & 0x01)]));
        }
    }

    private sealed record Mmc1Snapshot(
        string Registers,
        string Ppu,
        string OamHash,
        string NametablesHash,
        string ScreenHash,
        string PrgRam,
        string PrgBanks,
        string ChrBanks,
        TimelineCounters Timeline);
}
