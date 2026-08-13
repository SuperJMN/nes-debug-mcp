using Nes.Debug.Core;
using Nes.Debug.Emulator;
using Nes.Debug.Mcp;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nes.Debug.Tests;

[Collection(NesDebugSessionCollection.Name)]
public sealed class AprNesUxromSessionConformanceTests : NesDebugSessionConformanceTests<AprNesDebugSession>
{
    protected override AprNesDebugSession CreateSession() => new();

    protected override DebugContractRom CreateContractRom(
        byte[] program,
        int prgRomBanks = 2,
        int chrRomBanks = 1,
        NromMirroring mirroring = NromMirroring.Horizontal)
    {
        if (prgRomBanks is < 1 or > UxromTestRomBuilder.PrgRomBanks)
        {
            throw new ArgumentOutOfRangeException(nameof(prgRomBanks));
        }

        if (chrRomBanks is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(chrRomBanks));
        }

        return UxromTestRomBuilder.CreateConformanceProgram(program, mirroring);
    }

    protected override bool SupportsContinuousObservation => true;

    [Fact]
    public void Mcp_tools_expose_selected_prg_bank_and_its_execution()
    {
        var fixture = UxromTestRomBuilder.CreateBankSelectionFixture();
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        var load = Assert.IsType<LoadRomResult>(NesDebugTools.LoadRom(session, rom.Path));
        var initial = Assert.IsType<MemoryReadResult>(NesDebugTools.ReadMemory(session, "0xBFF0", 1));
        _ = Assert.IsType<WriteMemoryResult>(NesDebugTools.WriteMemory(session, "0x8000", [2]));
        var selected = Assert.IsType<MemoryReadResult>(NesDebugTools.ReadMemory(session, "0xBFF0", 1));
        var disassembly = Assert.IsType<DisassembleResult>(NesDebugTools.Disassemble(session, "0x8000", 1));

        Assert.Equal(2, load.Mapper);
        Assert.Equal("A0", initial.BytesHex);
        Assert.Equal("A2", selected.BytesHex);
        Assert.Equal("LDA #$B2", disassembly.Instructions[0].Text);

        _ = Assert.IsType<ResetResult>(NesDebugTools.Reset(session));
        var enterBank = Assert.IsType<StepInstructionResult>(NesDebugTools.StepInstruction(session, 3));
        var executeBank = Assert.IsType<StepInstructionResult>(NesDebugTools.StepInstruction(session, 2));
        var marker = Assert.IsType<MemoryReadResult>(NesDebugTools.ReadMemory(session, "0x0010", 1));

        Assert.Equal("0x8000", enterBank.PcAfter);
        Assert.Equal("0x8004", executeBank.PcAfter);
        Assert.Equal("B2", marker.BytesHex);
    }

    [Fact]
    public void Auto_routing_keeps_mapper_2_on_adnes()
    {
        var fixture = UxromTestRomBuilder.CreateBankSelectionFixture();
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = new AutoNesDebugSession(new ManagedNesDebugSession(), new AprNesDebugSession());

        var load = session.LoadRom(rom.Path);
        var state = session.GetState();

        AssertSuccess(load);
        Assert.Equal(2, load.Value.Mapper);
        AssertSuccess(state);
        Assert.Equal("ADNES", state.Value.Backend);
    }

    [Fact]
    public void Switchable_and_fixed_prg_windows_select_reset_and_execute_expected_banks()
    {
        var fixture = UxromTestRomBuilder.CreateBankSelectionFixture(NromMirroring.Vertical);
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        var load = session.LoadRom(rom.Path);
        var initialRegisters = session.ReadRegisters();
        var fixedProgram = session.ReadMemory(0xC100, 3);
        AssertSuccess(load);
        Assert.Equal(2, load.Value.Mapper);
        Assert.Equal(UxromTestRomBuilder.PrgRomBanks, load.Value.PrgRomBanks);
        Assert.Equal(0, load.Value.ChrRomBanks);
        AssertSuccess(initialRegisters);
        Assert.Equal($"0x{fixture.ResetVector:X4}", initialRegisters.Value.Pc);
        AssertSuccess(fixedProgram);
        Assert.Equal("A9 02 8D", fixedProgram.Value.BytesHex);

        for (var bank = 0; bank < fixture.SwitchableBankSentinels.Count; bank++)
        {
            AssertSuccess(session.WriteMemory(0x8000, [(byte)bank]));
            var switchable = session.ReadMemory(0xBFF0, 1);
            var fixedBank = session.ReadMemory(0xFFF0, 1);
            var disassembly = session.Disassemble(0x8000, 1);

            AssertSuccess(switchable);
            Assert.Equal(fixture.SwitchableBankSentinels[bank], switchable.Value.Bytes[0]);
            AssertSuccess(fixedBank);
            Assert.Equal(fixture.FixedBankSentinel, fixedBank.Value.Bytes[0]);
            AssertSuccess(disassembly);
            Assert.Equal($"LDA #${0xB0 + bank:X2}", disassembly.Value.Instructions[0].Text);
        }

        AssertSuccess(session.Reset());
        var resetBank = session.ReadMemory(0xBFF0, 1);
        var resetRegisters = session.ReadRegisters();
        AssertSuccess(resetBank);
        Assert.Equal(fixture.SwitchableBankSentinels[0], resetBank.Value.Bytes[0]);
        AssertSuccess(resetRegisters);
        Assert.Equal($"0x{fixture.ResetVector:X4}", resetRegisters.Value.Pc);

        var selectBankTwo = session.StepInstruction(2);
        var selectedBank = session.ReadMemory(0xBFF0, 1);
        AssertSuccess(selectBankTwo);
        Assert.Equal("0xC105", selectBankTwo.Value.PcAfter);
        AssertSuccess(selectedBank);
        Assert.Equal(fixture.SwitchableBankSentinels[2], selectedBank.Value.Bytes[0]);

        var enterSwitchableWindow = session.StepInstruction(1);
        AssertSuccess(enterSwitchableWindow);
        Assert.Equal("0x8000", enterSwitchableWindow.Value.PcAfter);
        var executeBankTwo = session.StepInstruction(2);
        var marker = session.ReadMemory(0x0010, 1);
        var writer = session.FindLastWriter(0x0010);
        AssertSuccess(executeBankTwo);
        Assert.Equal("0x8004", executeBankTwo.Value.PcAfter);
        AssertSuccess(marker);
        Assert.Equal("B2", marker.Value.BytesHex);
        AssertSuccess(writer);
        Assert.True(writer.Value.Found);
        Assert.Equal("0x8002", writer.Value.Pc);

        var returnToFixedWindow = session.StepInstruction(1);
        var fixedProgramAfterExecution = session.ReadMemory(0xC100, 3);
        AssertSuccess(returnToFixedWindow);
        Assert.Equal("0xC108", returnToFixedWindow.Value.PcAfter);
        AssertSuccess(fixedProgramAfterExecution);
        Assert.Equal(fixedProgram.Value.BytesHex, fixedProgramAfterExecution.Value.BytesHex);
    }

    [Fact]
    public void Chr_ram_tile_bytes_are_rendered_into_the_framebuffer()
    {
        var program = new List<byte>();
        for (ushort address = 0; address < 8; address++)
        {
            program.AddRange(NromTestRomBuilder.PpuWrite(address, 0xFF));
        }

        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F00, 0x0F));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F01, 0x30));
        program.AddRange(
        [
            0xA9, 0x00,
            0x8D, 0x05, 0x20, // PPUSCROLL X = 0
            0x8D, 0x05, 0x20, // PPUSCROLL Y = 0
            0x8D, 0x06, 0x20, // PPUADDR high = 0
            0x8D, 0x06, 0x20, // PPUADDR low = 0
            0xA9, 0x0A,
            0x8D, 0x01, 0x20, // enable background including left edge
        ]);
        var loop = (ushort)(0xC000 + program.Count);
        program.AddRange([0x4C, (byte)loop, (byte)(loop >> 8)]);

        var fixture = UxromTestRomBuilder.CreateProgramInFixedBank(program.ToArray());
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        AssertSuccess(session.LoadRom(rom.Path));
        AssertSuccess(session.RunFrame(3));
        var chrRam = session.DumpTileset(0x0000, 1);
        var pixels = session.ReadScreenRegion(16, 16, 8, 8, "palette_indices_raw");

        AssertSuccess(chrRam);
        Assert.StartsWith("FF FF FF FF FF FF FF FF", chrRam.Value.Tiles[0].BytesHex, StringComparison.Ordinal);
        AssertSuccess(pixels);
        Assert.Equal(64, pixels.Value.Values?.Count);
        Assert.All(pixels.Value.Values!, value => Assert.Equal(0x30, value));
    }

    [Fact]
    public void Savestate_restores_selected_bank_chr_ram_cpu_ppu_and_deterministic_timeline()
    {
        var program = new List<byte>();
        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F00, 0x0F));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F01, 0x30));
        program.AddRange(
        [
            0xA9, 0x0A,
            0x8D, 0x01, 0x20, // enable background rendering
        ]);
        var loop = (ushort)(0xC000 + program.Count);
        program.AddRange(
        [
            0xE6, 0x00,       // INC $00
            0xA9, 0x00,
            0x8D, 0x03, 0x20, // OAMADDR = 0
            0xA5, 0x00,
            0x8D, 0x04, 0x20, // sprite 0 Y = counter
            0xA9, 0x00,
            0x8D, 0x06, 0x20,
            0xA9, 0x00,
            0x8D, 0x06, 0x20,
            0xA5, 0x00,
            0x8D, 0x07, 0x20, // CHR-RAM[0] = counter
            0xA9, 0x20,
            0x8D, 0x06, 0x20,
            0xA9, 0x00,
            0x8D, 0x06, 0x20,
            0xA5, 0x00,
            0x8D, 0x07, 0x20, // nametable[0] = counter
            0x4C, (byte)loop, (byte)(loop >> 8),
        ]);

        var fixture = UxromTestRomBuilder.CreateProgramInFixedBank(program.ToArray());
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var state = TemporaryTestFile.Empty("nesstate");
        using var session = CreateSession();

        AssertSuccess(session.LoadRom(rom.Path));
        AssertSuccess(session.WriteMemory(0x8000, [0x02]));
        AssertSuccess(session.RunFrame(1));
        var saved = CaptureSnapshot(session);
        Assert.Equal(fixture.SwitchableBankSentinels[2], saved.SelectedBankSentinel);
        AssertSuccess(session.SaveState(state.Path));

        AssertSuccess(session.WriteMemory(0x8000, [0x01]));
        AssertSuccess(session.RunFrame(1));
        var firstFuture = CaptureSnapshot(session);
        Assert.Equal(fixture.SwitchableBankSentinels[1], firstFuture.SelectedBankSentinel);
        Assert.NotEqual(saved.ChrRamHash, firstFuture.ChrRamHash);
        Assert.NotEqual(saved.Timeline, firstFuture.Timeline);

        AssertSuccess(session.LoadState(state.Path));
        var restored = CaptureSnapshot(session);
        Assert.Equal(saved, restored);

        AssertSuccess(session.WriteMemory(0x8000, [0x01]));
        AssertSuccess(session.RunFrame(1));
        var replayedFuture = CaptureSnapshot(session);
        Assert.Equal(firstFuture, replayedFuture);
    }

    [Fact]
    public void Repeated_bounded_frame_runs_keep_each_selected_bank_current()
    {
        var fixture = UxromTestRomBuilder.CreateProgramInFixedBank(
        [
            0xE6, 0x00,       // INC $00
            0x4C, 0x00, 0xC0, // JMP $C000
        ]);
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        AssertSuccess(session.LoadRom(rom.Path));
        ulong expectedFrames = 0;
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var bank = iteration % fixture.SwitchableBankSentinels.Count;
            _ = Assert.IsType<WriteMemoryResult>(NesDebugTools.WriteMemory(session, "0x8000", [bank]));
            var frames = Assert.IsType<RunFrameResult>(NesDebugTools.RunFrame(session, 2));
            var selectedBank = Assert.IsType<MemoryReadResult>(NesDebugTools.ReadMemory(session, "0xBFF0", 1));
            var state = Assert.IsType<SessionStateResult>(NesDebugTools.GetState(session));
            expectedFrames += 2;

            Assert.Equal(2, frames.FramesRun);
            Assert.Equal(expectedFrames, frames.Timeline.Frames);
            Assert.Equal(fixture.SwitchableBankSentinels[bank], selectedBank.Bytes[0]);
            Assert.Equal(expectedFrames, state.Timeline.Frames);
            Assert.Equal(2, state.Mapper);
            Assert.Equal("AprNes", state.Backend);
        }
    }

    private static UxromObservableSnapshot CaptureSnapshot(INesDebugSession session)
    {
        var registers = session.ReadRegisters();
        var ram = session.ReadMemory(0x0000, 0x800);
        var selectedBank = session.ReadMemory(0xBFF0, 1);
        var fixedBank = session.ReadMemory(0xFFF0, 1);
        var ppu = session.ReadPpuState();
        var oam = session.ReadOam();
        var nametables = session.DumpNametables(includeDetails: true);
        var tileset = session.DumpTileset(0x0000, 512);
        var screen = session.CaptureScreen();
        var state = session.GetState();

        AssertSuccess(registers);
        AssertSuccess(ram);
        AssertSuccess(selectedBank);
        AssertSuccess(fixedBank);
        AssertSuccess(ppu);
        AssertSuccess(oam);
        AssertSuccess(nametables);
        AssertSuccess(tileset);
        AssertSuccess(screen);
        AssertSuccess(state);

        return new UxromObservableSnapshot(
            registers.Value,
            HashBytes(ram.Value.Bytes),
            selectedBank.Value.Bytes[0],
            fixedBank.Value.Bytes[0],
            HashCanonical(ppu.Value),
            HashCanonical(oam.Value.Sprites),
            HashCanonical(nametables.Value.Nametables),
            HashCanonical(tileset.Value.Tiles),
            Convert.ToHexString(SHA256.HashData(screen.Value.Data)),
            state.Value.Timeline);
    }

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static string HashCanonical<T>(T value) =>
        HashBytes(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    private sealed record UxromObservableSnapshot(
        NesCpuRegisters Registers,
        string CpuRamHash,
        byte SelectedBankSentinel,
        byte FixedBankSentinel,
        string PpuStateHash,
        string OamHash,
        string NametablesHash,
        string ChrRamHash,
        string FramebufferHash,
        TimelineCounters Timeline);
}
