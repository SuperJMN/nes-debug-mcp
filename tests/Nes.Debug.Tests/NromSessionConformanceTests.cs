using Nes.Debug.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nes.Debug.Tests;

public abstract class NromSessionConformanceTests<TSession>
    where TSession : INesDebugSession, IDisposable
{
    protected abstract TSession CreateSession();

    protected virtual bool SupportsContinuousObservation => false;

    [Fact]
    public void Load_reset_execution_memory_symbols_and_input_timeline_conform()
    {
        var fixture = NromTestRomBuilder.CreateProgram(
        [
            0xA9, 0x42,       // $8000: LDA #$42
            0x85, 0x02,       // $8002: STA $02
            0xE6, 0x02,       // $8004: INC $02
            0x4C, 0x04, 0x80, // $8006: JMP $8004
        ], prgRomBanks: 2, chrRomBanks: 1, NromMirroring.Vertical);
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var symbols = TemporaryTestFile.FromText("8000 EntryPoint\n0002 Counter\n", "sym");
        using var session = CreateSession();

        var load = session.LoadRom(rom.Path);
        var initial = session.GetState();
        var symbolLoad = session.LoadSymbols(symbols.Path);
        var resolved = session.ResolveSymbol("EntryPoint");
        var disassembly = session.Disassemble(0x8000, 2);
        var step = session.StepInstruction(2);
        var registers = session.ReadRegisters();
        var counter = session.ReadSymbol("Counter", 1);
        var write = session.WriteMemory(0x0003, [0xA5]);
        var written = session.ReadMemory(0x0002, 2);
        var controller = session.SetController([NesButton.Right, NesButton.A]);
        var pressed = session.PressButtons([NesButton.B], 1);
        var timeline = session.RunInputTimeline(
        [
            new InputTimelineStep
            {
                Frames = 1,
                Buttons = ["right"],
                ReadRegisters = true,
                MemoryAddress = "0x0002",
                MemoryLength = 2,
            },
            new InputTimelineStep
            {
                Frames = 1,
                Buttons = ["a", "right"],
                ReadPpuState = true,
            },
        ]);
        var reset = session.Reset();
        var afterReset = session.GetState();
        var resetRegisters = session.ReadRegisters();
        var resetRam = session.ReadMemory(0x0002, 2);

        AssertSuccess(load);
        Assert.Equal(0, load.Value.Mapper);
        Assert.Equal(2, load.Value.PrgRomBanks);
        Assert.Equal(1, load.Value.ChrRomBanks);
        AssertSuccess(initial);
        Assert.True(initial.Value.RomLoaded);
        Assert.Equal(0UL, initial.Value.Timeline.Frames);
        AssertSuccess(symbolLoad);
        Assert.Equal(2, symbolLoad.Value.SymbolCount);
        AssertSuccess(resolved);
        Assert.Equal("0x8000", resolved.Value.Address);
        AssertSuccess(disassembly);
        Assert.Equal("EntryPoint", disassembly.Value.Instructions[0].Symbol);
        Assert.Equal("LDA #$42", disassembly.Value.Instructions[0].Text);
        AssertSuccess(step);
        Assert.Equal("0x8004", step.Value.PcAfter);
        AssertSuccess(registers);
        Assert.Equal("0x42", registers.Value.A);
        AssertSuccess(counter);
        Assert.Equal("42", counter.Value.BytesHex);
        AssertSuccess(write);
        AssertSuccess(written);
        Assert.Equal("42 A5", written.Value.BytesHex);
        AssertSuccess(controller);
        Assert.True(controller.Value.A);
        Assert.True(controller.Value.Right);
        AssertSuccess(pressed);
        Assert.Equal(1, pressed.Value.FramesRun);
        Assert.Empty(pressed.Value.Released.Pressed);
        AssertSuccess(timeline);
        Assert.Equal(2, timeline.Value.FramesRun);
        Assert.Equal(2, timeline.Value.Steps.Count);
        Assert.NotNull(timeline.Value.Steps[0].Registers);
        Assert.NotNull(timeline.Value.Steps[0].Memory);
        Assert.NotNull(timeline.Value.Steps[1].PpuState);
        Assert.Empty(timeline.Value.Released.Pressed);
        AssertSuccess(reset);
        AssertSuccess(afterReset);
        Assert.Equal(0UL, afterReset.Value.Timeline.Frames);
        Assert.Equal(0UL, afterReset.Value.Timeline.Instructions);
        AssertSuccess(resetRegisters);
        Assert.Equal("0x8000", resetRegisters.Value.Pc);
        AssertSuccess(resetRam);
        Assert.Equal("00 00", resetRam.Value.BytesHex);
    }

    [Fact]
    public void Breakpoints_watchpoints_conditions_last_writers_and_bounded_traces_conform()
    {
        var fixture = NromTestRomBuilder.CreateProgram(
        [
            0xA9, 0x2A,       // $8000: LDA #$2A
            0x85, 0x02,       // $8002: STA $02
            0x4C, 0x04, 0x80, // $8004: JMP $8004
        ]);
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        AssertSuccess(session.LoadRom(rom.Path));

        var breakpoint = session.SetBreakpoint(0x8004, "A == 0x2A");
        AssertSuccess(breakpoint);
        var listedBreakpoints = session.ListBreakpoints();
        AssertSuccess(listedBreakpoints);
        Assert.Contains(listedBreakpoints.Value.Breakpoints, item => item.Id == breakpoint.Value.BreakpointId);
        var breakpointRun = session.ContinueUntilBreak(10);
        AssertSuccess(breakpointRun);
        Assert.Equal("breakpoint", breakpointRun.Value.Reason);
        Assert.Equal("0x8004", breakpointRun.Value.Pc);
        AssertSuccess(session.ClearBreakpoint(breakpoint.Value.BreakpointId));

        AssertSuccess(session.Reset());
        var watchpoint = session.SetWatchpointRange(0x0001, 4, WatchpointMode.Write);
        AssertSuccess(watchpoint);
        var listedWatchpoints = session.ListWatchpoints();
        AssertSuccess(listedWatchpoints);
        Assert.Contains(listedWatchpoints.Value.Watchpoints, item => item.Id == watchpoint.Value.WatchpointId);
        var watchpointRun = session.ContinueUntilBreak(10);
        AssertSuccess(watchpointRun);
        Assert.Equal("watchpoint", watchpointRun.Value.Reason);
        var lastWriter = session.FindLastWriter(0x0002);
        var lastWriters = session.FindLastWriters(0x0001, 4);
        AssertSuccess(lastWriter);
        Assert.True(lastWriter.Value.Found);
        Assert.Equal("0x8002", lastWriter.Value.Pc);
        Assert.Equal("0x2A", lastWriter.Value.Value);
        AssertSuccess(lastWriters);
        Assert.Single(lastWriters.Value.Writers, item => item.Found);
        AssertSuccess(session.ClearWatchpoint(watchpoint.Value.WatchpointId));

        AssertSuccess(session.Reset());
        var condition = session.RunUntilCondition("[0x0002] == 0x2A", 10, 1);
        AssertSuccess(condition);
        Assert.Equal("condition", condition.Value.Reason);
        Assert.InRange(condition.Value.InstructionsRun, 1U, 10U);

        AssertSuccess(session.Reset());
        var trace = session.TraceUntilWrite(0x0002, 10);
        AssertSuccess(trace);
        Assert.Equal("write", trace.Value.Reason);
        Assert.Equal("0x2A", trace.Value.Value);
        Assert.InRange(trace.Value.InstructionsRun, 1U, 10U);

        AssertSuccess(session.Reset());
        var rangeTrace = session.TraceUntilWriteRange(0x0001, 4, 10);
        AssertSuccess(rangeTrace);
        Assert.Equal("write", rangeTrace.Value.Reason);
        Assert.Equal("0x0002", rangeTrace.Value.HitAddress);
        Assert.Equal("0x2A", rangeTrace.Value.Value);
        Assert.InRange(rangeTrace.Value.InstructionsRun, 1U, 10U);
        Assert.NotEmpty(rangeTrace.Value.Disassembly.Instructions);

        AssertSuccess(session.Reset());
        var boundedMiss = session.TraceUntilWrite(0x0003, 5);
        AssertSuccess(boundedMiss);
        Assert.Equal("maxInstructions", boundedMiss.Value.Reason);
        Assert.Equal(5U, boundedMiss.Value.InstructionsRun);
        Assert.Null(boundedMiss.Value.Pc);
        Assert.Null(boundedMiss.Value.Value);
    }

    [Fact]
    public void Ppu_oam_screen_and_correlated_observation_conform()
    {
        var program = new List<byte>();
        program.AddRange(NromTestRomBuilder.PpuWrite(0x0000, 0xFF));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F00, 0x0F));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F01, 0x30));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2000, 0x11));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x23C0, 0xAA));
        program.AddRange(
        [
            0xA9, 0x00,       // LDA #$00
            0x8D, 0x03, 0x20, // STA $2003
            0xA9, 0x20,       // sprite 0 Y
            0x8D, 0x04, 0x20,
            0xA9, 0x01,       // sprite 0 tile
            0x8D, 0x04, 0x20,
            0xA9, 0x02,       // sprite 0 attributes
            0x8D, 0x04, 0x20,
            0xA9, 0x30,       // sprite 0 X
            0x8D, 0x04, 0x20,
            0xA9, 0x0A,       // background rendering including left edge
            0x8D, 0x01, 0x20,
        ]);
        var traceLoop = (ushort)(0x8000 + program.Count);
        program.AddRange(
        [
            0xA9, 0x01,
            0x8D, 0x00, 0x20, // STA $2000
            0xE6, 0x00,       // INC $00
            0x4C, (byte)traceLoop, (byte)(traceLoop >> 8),
        ]);

        var fixture = NromTestRomBuilder.CreateProgram(
            program.ToArray(),
            prgRomBanks: 1,
            chrRomBanks: 0,
            NromMirroring.Vertical);
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        AssertSuccess(session.LoadRom(rom.Path));
        var initialize = session.StepInstruction(43);
        AssertSuccess(initialize);

        var ppu = session.ReadPpuState();
        var oam = session.ReadOam();
        var region = session.ReadScreenRegion(0, 0, 2, 2, "palette_indices");
        var capture = session.CaptureScreen();
        var tilemap = session.DumpTilemap(0x2000);
        var nametables = session.DumpNametables(includeDetails: true);
        var tileset = session.DumpTileset(0x0000, 1);

        AssertSuccess(ppu);
        Assert.Equal("0x0A", ppu.Value.PpuMask);
        Assert.True(ppu.Value.BackgroundEnabled);
        AssertSuccess(oam);
        Assert.Equal(64, oam.Value.Sprites.Count);
        Assert.Equal(0x20, oam.Value.Sprites[0].Y);
        Assert.Equal("0x01", oam.Value.Sprites[0].Tile);
        Assert.Equal("0x02", oam.Value.Sprites[0].Attributes);
        Assert.Equal(0x30, oam.Value.Sprites[0].X);
        AssertSuccess(region);
        Assert.Equal(4, region.Value.Values?.Count);
        AssertSuccess(capture);
        Assert.Equal(256, capture.Value.Width);
        Assert.Equal(240, capture.Value.Height);
        Assert.Equal("image/png", capture.Value.MimeType);
        Assert.NotEmpty(capture.Value.Data);
        AssertSuccess(tilemap);
        Assert.Equal("11", FirstByte(tilemap.Value.Rows[0]));
        Assert.Equal("AA", FirstByte(tilemap.Value.AttributeRows[0]));
        AssertSuccess(nametables);
        Assert.True(nametables.Value.DetailsIncluded);
        Assert.Equal(4, nametables.Value.Nametables.Count);
        Assert.All(nametables.Value.Nametables, table => Assert.NotNull(table.Detail));
        AssertSuccess(tileset);
        Assert.StartsWith("FF", tileset.Value.Tiles[0].BytesHex, StringComparison.Ordinal);

        var screen = session.ObserveScreen(1);
        AssertSuccess(screen);
        Assert.Equal(1, screen.Value.FramesRun);
        var sample = Assert.Single(screen.Value.Samples);
        Assert.StartsWith("sha256:", sample.Hash, StringComparison.Ordinal);

        var ppuTrace = session.TracePpuRegisterWrites(new PpuRegisterTraceRequest(
            FrameCount: 1,
            MaxEvents: 3,
            Registers: new HashSet<ushort> { 0x2000 },
            Buttons: []));

        if (!SupportsContinuousObservation)
        {
            Assert.False(ppuTrace.IsSuccess);
            Assert.Equal("ppu_register_trace_not_supported", ppuTrace.Error?.Code);
            var unsupportedExecution = session.ObserveExecution(new ExecutionObservationRequest(
                FrameCount: 1,
                Buttons: [],
                MemoryProbes: [],
                IncludePpuState: true,
                TracePpuWrites: false,
                MaxPpuEvents: 0,
                PpuRegisters: new HashSet<ushort>()));
            Assert.False(unsupportedExecution.IsSuccess);
            Assert.Equal("execution_observation_not_supported", unsupportedExecution.Error?.Code);
            return;
        }

        AssertSuccess(ppuTrace);
        Assert.Equal(1, ppuTrace.Value.FramesRun);
        Assert.Equal(3, ppuTrace.Value.EventCount);
        Assert.True(ppuTrace.Value.EventsObserved > ppuTrace.Value.EventCount);
        Assert.True(ppuTrace.Value.Truncated);
        Assert.Equal(
            Enumerable.Repeat("0x2000", ppuTrace.Value.EventCount),
            ppuTrace.Value.Events.Select(item => item.Address));
        Assert.Equal(
            Enumerable.Repeat("0x01", ppuTrace.Value.EventCount),
            ppuTrace.Value.Events.Select(item => item.Value));
        Assert.Equal(
            Enumerable.Repeat($"0x{traceLoop + 2:X4}", ppuTrace.Value.EventCount),
            ppuTrace.Value.Events.Select(item => item.Pc));
        AssertStrictlyOrdered(ppuTrace.Value.Events.Select(item => item.CpuCycle));
        AssertStrictlyOrdered(ppuTrace.Value.Events.Select(item => item.InstructionCounter));
        Assert.All(ppuTrace.Value.Events, item => Assert.NotEqual(item.Before, item.After));

        var memoryBeforeExecution = session.ReadMemory(0x0000, 1);
        AssertSuccess(memoryBeforeExecution);
        var execution = session.ObserveExecution(new ExecutionObservationRequest(
            FrameCount: 1,
            Buttons: [NesButton.Right],
            MemoryProbes: [new MemoryProbe(0x0000, 1)],
            IncludePpuState: true,
            TracePpuWrites: true,
            MaxPpuEvents: 2,
            PpuRegisters: new HashSet<ushort> { 0x2000 }));
        AssertSuccess(execution);
        Assert.Equal(1, execution.Value.FramesRun);
        Assert.Equal(["right"], execution.Value.HeldButtons);
        var frame = Assert.Single(execution.Value.Frames);
        Assert.StartsWith("sha256:", execution.Value.InitialFramebufferHash, StringComparison.Ordinal);
        Assert.StartsWith("sha256:", frame.Screen.Hash, StringComparison.Ordinal);
        Assert.NotNull(frame.PpuState);
        Assert.Single(frame.Memory);
        Assert.Equal("0x0000", frame.Memory[0].Address);
        Assert.NotEqual(memoryBeforeExecution.Value.BytesHex, frame.Memory[0].BytesHex);
        Assert.Equal(2, execution.Value.PpuEventCount);
        Assert.True(execution.Value.PpuEventsObserved > execution.Value.PpuEventCount);
        Assert.True(execution.Value.PpuTraceTruncated);
        Assert.True(execution.Value.Truncated);
        Assert.Equal(
            Enumerable.Repeat("0x2000", execution.Value.PpuEventCount),
            execution.Value.PpuEvents.Select(item => item.Address));
        AssertStrictlyOrdered(execution.Value.PpuEvents.Select(item => item.CpuCycle));
        AssertStrictlyOrdered(execution.Value.PpuEvents.Select(item => item.InstructionCounter));
        Assert.All(execution.Value.PpuEvents, item => Assert.NotEqual(item.Before, item.After));
        Assert.Equal(4, execution.Value.InitialNametables.Nametables.Count);
        Assert.Equal(4, execution.Value.FinalNametables.Nametables.Count);
        Assert.All(
            execution.Value.InitialNametables.Nametables.Concat(execution.Value.FinalNametables.Nametables),
            table =>
            {
                Assert.False(string.IsNullOrWhiteSpace(table.Hash));
                Assert.False(string.IsNullOrWhiteSpace(table.TileHash));
                Assert.False(string.IsNullOrWhiteSpace(table.AttributeHash));
            });
        Assert.Empty(execution.Value.Released.Pressed);
    }

    [Fact]
    public void Savestate_restores_complete_observable_state_and_replays_deterministically()
    {
        var program = new List<byte>();
        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F00, 0x0F));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F01, 0x30));
        program.AddRange(
        [
            0xA9, 0x0A,
            0x8D, 0x01, 0x20, // enable background rendering
        ]);
        var loop = (ushort)(0x8000 + program.Count);
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

        var fixture = NromTestRomBuilder.CreateProgram(
            program.ToArray(),
            prgRomBanks: 2,
            chrRomBanks: 0,
            NromMirroring.Horizontal);
        fixture.Bytes[16 + 0x3FF0] = 0x6D;
        fixture.Bytes[16 + 0x7FF0] = 0xD6;
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var state = TemporaryTestFile.Empty("nesstate");
        using var session = CreateSession();

        AssertSuccess(session.LoadRom(rom.Path));
        AssertSuccess(session.RunFrame(1));
        var savedSnapshot = CaptureObservableSnapshot(session);
        AssertSuccess(session.SaveState(state.Path));

        AssertSuccess(session.RunFrame(1));
        var firstFuture = CaptureObservableSnapshot(session);
        Assert.NotEqual(savedSnapshot.Registers, firstFuture.Registers);
        Assert.NotEqual(savedSnapshot.CpuRamHash, firstFuture.CpuRamHash);
        Assert.NotEqual(savedSnapshot.PpuStateHash, firstFuture.PpuStateHash);
        Assert.NotEqual(savedSnapshot.OamHash, firstFuture.OamHash);
        Assert.NotEqual(savedSnapshot.NametablesHash, firstFuture.NametablesHash);
        Assert.NotEqual(savedSnapshot.ChrRamHash, firstFuture.ChrRamHash);
        Assert.NotEqual(savedSnapshot.FramebufferHash, firstFuture.FramebufferHash);
        Assert.NotEqual(savedSnapshot.Timeline, firstFuture.Timeline);
        Assert.Equal("6D D6", savedSnapshot.PrgSentinels);
        Assert.Equal(savedSnapshot.PrgSentinels, firstFuture.PrgSentinels);

        AssertSuccess(session.WriteMemory(0x0000, [0xEE]));
        AssertSuccess(session.LoadState(state.Path));
        var restoredSnapshot = CaptureObservableSnapshot(session);
        Assert.Equal(savedSnapshot, restoredSnapshot);

        AssertSuccess(session.RunFrame(1));
        var replayedFuture = CaptureObservableSnapshot(session);
        Assert.Equal(firstFuture, replayedFuture);
    }

    [Theory]
    [InlineData(1, 0, NromMirroring.Horizontal)]
    [InlineData(1, 0, NromMirroring.Vertical)]
    [InlineData(1, 1, NromMirroring.Horizontal)]
    [InlineData(1, 1, NromMirroring.Vertical)]
    [InlineData(2, 0, NromMirroring.Horizontal)]
    [InlineData(2, 0, NromMirroring.Vertical)]
    [InlineData(2, 1, NromMirroring.Horizontal)]
    [InlineData(2, 1, NromMirroring.Vertical)]
    public void Nrom_matrix_exposes_prg_chr_and_mirroring_sentinels(
        int prgRomBanks,
        int chrRomBanks,
        NromMirroring mirroring)
    {
        var fixture = NromTestRomBuilder.CreateMatrixFixture(prgRomBanks, chrRomBanks, mirroring);
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        var load = session.LoadRom(rom.Path);
        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.Equal(0, load.Value.Mapper);
        Assert.Equal(prgRomBanks, load.Value.PrgRomBanks);
        Assert.Equal(chrRomBanks, load.Value.ChrRomBanks);

        var initialize = session.StepInstruction(fixture.InitializationInstructionCount);
        var lowerPrg = session.ReadMemory(0xBFF0, 1);
        var upperPrg = session.ReadMemory(0xFFF0, 1);
        var chr = session.DumpTileset(0x0000, 1);
        var nametables = new[] { 0x2000, 0x2400, 0x2800, 0x2C00 }
            .Select(address => session.DumpTilemap((ushort)address))
            .ToArray();

        Assert.True(initialize.IsSuccess, initialize.Error?.Message);
        AssertSuccess(lowerPrg);
        AssertSuccess(upperPrg);
        AssertSuccess(chr);
        Assert.All(nametables, AssertSuccess);
        Assert.Equal(fixture.LowerPrgSentinel, lowerPrg.Value.Bytes[0]);
        Assert.Equal(fixture.UpperPrgSentinel, upperPrg.Value.Bytes[0]);
        Assert.StartsWith(fixture.ChrSentinel, chr.Value.Tiles[0].BytesHex, StringComparison.Ordinal);

        var expectedTiles = mirroring == NromMirroring.Horizontal
            ? new[] { "22", "22", "44", "44" }
            : ["33", "44", "33", "44"];
        Assert.Equal(expectedTiles, nametables.Select(result => FirstByte(result.Value.Rows[0])));
    }

    private static string FirstByte(string row) =>
        row.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

    private static ObservableSnapshot CaptureObservableSnapshot(INesDebugSession session)
    {
        var registers = session.ReadRegisters();
        var ram = session.ReadMemory(0x0000, 0x800);
        var lowerPrgSentinel = session.ReadMemory(0xBFF0, 1);
        var upperPrgSentinel = session.ReadMemory(0xFFF0, 1);
        var ppu = session.ReadPpuState();
        var oam = session.ReadOam();
        var nametables = session.DumpNametables(includeDetails: true);
        var tileset = session.DumpTileset(0x0000, 512);
        var capture = session.CaptureScreen();
        var state = session.GetState();

        AssertSuccess(registers);
        AssertSuccess(ram);
        Assert.Equal(0x800, ram.Value.Bytes.Length);
        AssertSuccess(lowerPrgSentinel);
        AssertSuccess(upperPrgSentinel);
        AssertSuccess(ppu);
        AssertSuccess(oam);
        Assert.Equal(64, oam.Value.Sprites.Count);
        AssertSuccess(nametables);
        Assert.True(nametables.Value.DetailsIncluded);
        Assert.Equal(4, nametables.Value.Nametables.Count);
        Assert.All(nametables.Value.Nametables, table =>
        {
            Assert.NotNull(table.Detail);
            Assert.Equal(30, table.Detail.Rows.Count);
            Assert.Equal(8, table.Detail.AttributeRows.Count);
        });
        AssertSuccess(tileset);
        Assert.Equal(512, tileset.Value.Tiles.Count);
        AssertSuccess(capture);
        AssertSuccess(state);

        return new ObservableSnapshot(
            registers.Value,
            HashBytes(ram.Value.Bytes),
            $"{lowerPrgSentinel.Value.BytesHex} {upperPrgSentinel.Value.BytesHex}",
            HashCanonical(ppu.Value),
            HashCanonical(oam.Value.Sprites),
            HashCanonical(nametables.Value.Nametables),
            HashCanonical(tileset.Value.Tiles),
            Convert.ToHexString(SHA256.HashData(capture.Value.Data)),
            state.Value.Timeline);
    }

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static string HashCanonical<T>(T value) =>
        HashBytes(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    private static void AssertStrictlyOrdered(IEnumerable<ulong> values)
    {
        var materialized = values.ToArray();
        Assert.All(materialized.Zip(materialized.Skip(1)), pair => Assert.True(pair.First < pair.Second));
    }

    private static void AssertSuccess<T>(DebugResult<T> result) =>
        Assert.True(result.IsSuccess, result.Error?.Message);

    private sealed record ObservableSnapshot(
        NesCpuRegisters Registers,
        string CpuRamHash,
        string PrgSentinels,
        string PpuStateHash,
        string OamHash,
        string NametablesHash,
        string ChrRamHash,
        string FramebufferHash,
        TimelineCounters Timeline);
}
