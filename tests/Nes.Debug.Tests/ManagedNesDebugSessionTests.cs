using Nes.Debug.Core;
using Nes.Debug.Emulator;
using System.Text.Json;

namespace Nes.Debug.Tests;

public sealed class ManagedNesDebugSessionTests
{
    [Fact]
    public void Load_rom_resets_to_ines_reset_vector_and_steps_instruction()
    {
        using var temp = new TempRom(CreateMinimalNrom());
        using var session = new ManagedNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var before = session.ReadRegisters();
        var step = session.StepInstruction(1);
        var memory = session.ReadMemory(0x8000, 2);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(before.IsSuccess, before.Error?.Message);
        Assert.True(step.IsSuccess, step.Error?.Message);
        Assert.True(memory.IsSuccess, memory.Error?.Message);
        Assert.Equal("0x8000", before.Value.Pc);
        Assert.Equal("0x42", step.Value.Registers.A);
        Assert.Equal("0x8002", step.Value.Registers.Pc);
        Assert.Equal("A9 42", memory.Value.BytesHex);
    }

    [Fact]
    public void Continue_until_break_honors_conditional_breakpoints()
    {
        using var temp = new TempRom(CreateMinimalNrom(
        [
            0xA9, 0x01, // LDA #$01
            0xEA,       // NOP
            0xA9, 0x02, // LDA #$02
            0xEA,       // NOP
            0x4C, 0x05, 0x80, // JMP $8005
        ]));
        using var session = new ManagedNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var breakpoint = session.SetBreakpoint(0x8005, "A == 2");
        var run = session.ContinueUntilBreak(10);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(breakpoint.IsSuccess, breakpoint.Error?.Message);
        Assert.True(run.IsSuccess, run.Error?.Message);
        Assert.Equal("breakpoint", run.Value.Reason);
        Assert.Equal("0x8005", run.Value.Pc);
        Assert.Equal("0x02", run.Value.Registers.A);
    }

    [Fact]
    public void Continue_until_break_stops_on_watchpoint_and_records_last_writer()
    {
        using var temp = new TempRom(CreateMinimalNrom(
        [
            0xA9, 0x7B, // LDA #$7B
            0x85, 0x02, // STA $02
            0x4C, 0x04, 0x80, // JMP $8004
        ]));
        using var session = new ManagedNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var watchpoint = session.SetWatchpoint(0x0002, WatchpointMode.Write);
        var run = session.ContinueUntilBreak(10);
        var writer = session.FindLastWriter(0x0002);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(watchpoint.IsSuccess, watchpoint.Error?.Message);
        Assert.True(run.IsSuccess, run.Error?.Message);
        Assert.True(writer.IsSuccess, writer.Error?.Message);
        Assert.Equal("watchpoint", run.Value.Reason);
        Assert.Equal("0x8002", writer.Value.Pc);
        Assert.Equal("0x7B", writer.Value.Value);
    }

    [Fact]
    public void Run_until_condition_stops_on_memory_predicate_and_reports_ppu_state()
    {
        using var temp = new TempRom(CreateMinimalNrom(
        [
            0xA9, 0x2A, // LDA #$2A
            0x85, 0x02, // STA $02
            0x4C, 0x04, 0x80, // JMP $8004
        ]));
        using var session = new ManagedNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var run = session.RunUntilCondition("[0x0002] == 0x2A", maxInstructions: 10, maxFrames: 1);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(run.IsSuccess, run.Error?.Message);
        Assert.Equal("condition", run.Value.Reason);
        Assert.Equal("0x8004", run.Value.Pc);
        Assert.True(run.Value.InstructionsRun > 0);
        Assert.NotNull(run.Value.PpuState);
    }

    [Fact]
    public void Trace_until_write_range_reports_concrete_hit_address_and_last_writers()
    {
        using var temp = new TempRom(CreateMinimalNrom(
        [
            0xA9, 0x7B, // LDA #$7B
            0x85, 0x02, // STA $02
            0x4C, 0x04, 0x80, // JMP $8004
        ]));
        using var session = new ManagedNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var trace = session.TraceUntilWriteRange(0x0001, length: 4, maxInstructions: 10);
        var writers = session.FindLastWriters(0x0001, 4);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(trace.IsSuccess, trace.Error?.Message);
        Assert.True(writers.IsSuccess, writers.Error?.Message);
        Assert.Equal("write", trace.Value.Reason);
        Assert.Equal("0x0001", trace.Value.Address);
        Assert.Equal("0x0002", trace.Value.HitAddress);
        Assert.Equal("0x7B", trace.Value.Value);
        Assert.NotEmpty(trace.Value.Disassembly.Instructions);
        var writer = Assert.Single(writers.Value.Writers, item => item.Found);
        Assert.Equal("0x0002", writer.Address);
        Assert.Equal("0x7B", writer.Value);
    }

    [Fact]
    public void Watchpoint_range_stops_continue_when_write_falls_inside_range()
    {
        using var temp = new TempRom(CreateMinimalNrom(
        [
            0xA9, 0x7B, // LDA #$7B
            0x85, 0x02, // STA $02
            0x4C, 0x04, 0x80, // JMP $8004
        ]));
        using var session = new ManagedNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var watchpoint = session.SetWatchpointRange(0x0001, length: 4, WatchpointMode.Write);
        var run = session.ContinueUntilBreak(10);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(watchpoint.IsSuccess, watchpoint.Error?.Message);
        Assert.True(run.IsSuccess, run.Error?.Message);
        Assert.Equal(4, watchpoint.Value.Length);
        Assert.Equal("watchpoint", run.Value.Reason);
    }

    [Fact]
    public void Read_screen_region_returns_raw_small_region_and_summary_for_large_region()
    {
        using var temp = new TempRom(CreateMinimalNrom());
        using var session = new ManagedNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var runFrame = session.RunFrame(1);
        var small = session.ReadScreenRegion(0, 0, 2, 2, "palette_indices");
        var large = session.ReadScreenRegion(0, 0, 256, 8, "palette_indices");

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(runFrame.IsSuccess, runFrame.Error?.Message);
        Assert.True(small.IsSuccess, small.Error?.Message);
        Assert.True(large.IsSuccess, large.Error?.Message);
        Assert.Equal(4, small.Value.Values?.Count);
        Assert.NotEmpty(small.Value.Histogram);
        Assert.Null(large.Value.Values);
        Assert.Equal(8, large.Value.RowHashes.Count);
        Assert.Equal(2048, large.Value.PixelCount);
    }

    [Fact]
    public void Read_screen_region_can_return_all_palette_indices_for_a_full_frame_when_requested()
    {
        using var temp = new TempRom(CreateMinimalNrom());
        using var session = new ManagedNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var runFrame = session.RunFrame(1);
        var frame = session.ReadScreenRegion(0, 0, 256, 240, "palette_indices_raw");

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(runFrame.IsSuccess, runFrame.Error?.Message);
        Assert.True(frame.IsSuccess, frame.Error?.Message);
        Assert.Equal("palette_indices_raw", frame.Value.Format);
        Assert.Equal(61_440, frame.Value.PixelCount);
        Assert.Equal(61_440, frame.Value.Values?.Count);
        Assert.Equal(240, frame.Value.RowHashes.Count);
    }

    [Fact]
    public void Dump_tilemap_includes_the_physical_attribute_table()
    {
        using var temp = new TempRom(CreateMinimalNrom(
        [
            ..WritePpuByte(0x2000, 0x11),
            ..WritePpuByte(0x23C0, 0xAA),
            0x4C, 0x21, 0x80, // JMP $8021
        ]));
        using var session = new ManagedNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var step = session.StepInstruction(14);
        var tilemap = session.DumpTilemap(0x2000);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(step.IsSuccess, step.Error?.Message);
        Assert.True(tilemap.IsSuccess, tilemap.Error?.Message);

        var json = JsonSerializer.Serialize(tilemap.Value);
        Assert.Contains("\"attributeAddress\":\"0x23C0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"attributeRows\":[\"AA", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Four_screen_rom_keeps_all_four_nametables_distinct()
    {
        using var temp = new TempRom(CreateMinimalNrom(
            [
                ..WritePpuByte(0x2000, 0x11),
                ..WritePpuByte(0x2400, 0x22),
                ..WritePpuByte(0x2800, 0x33),
                ..WritePpuByte(0x2C00, 0x44),
                0x4C, 0x3C, 0x80, // JMP $803C
            ],
            flags6: 0x08));
        using var session = new ManagedNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var step = session.StepInstruction(25);
        var nametable0 = session.DumpTilemap(0x2000);
        var nametable1 = session.DumpTilemap(0x2400);
        var nametable2 = session.DumpTilemap(0x2800);
        var nametable3 = session.DumpTilemap(0x2C00);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(step.IsSuccess, step.Error?.Message);
        Assert.True(nametable0.IsSuccess, nametable0.Error?.Message);
        Assert.True(nametable1.IsSuccess, nametable1.Error?.Message);
        Assert.True(nametable2.IsSuccess, nametable2.Error?.Message);
        Assert.True(nametable3.IsSuccess, nametable3.Error?.Message);
        Assert.Equal("11", FirstTile(nametable0.Value));
        Assert.Equal("22", FirstTile(nametable1.Value));
        Assert.Equal("33", FirstTile(nametable2.Value));
        Assert.Equal("44", FirstTile(nametable3.Value));
    }

    [Fact]
    public void Input_timeline_runs_steps_collects_observations_and_releases_buttons()
    {
        using var temp = new TempRom(CreateMinimalNrom());
        using var session = new ManagedNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var result = session.RunInputTimeline(
        [
            new InputTimelineStep { Frames = 1, Buttons = ["right"], ReadRegisters = true },
            new InputTimelineStep { Frames = 1, Buttons = ["right", "a"], ReadPpuState = true, DumpOam = true },
        ]);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value.FramesRun);
        Assert.Empty(result.Value.Released.Pressed);
        Assert.Equal(2, result.Value.Steps.Count);
        Assert.Equal(["right"], result.Value.Steps[0].Buttons);
        Assert.NotNull(result.Value.Steps[0].Registers);
        Assert.NotNull(result.Value.Steps[1].PpuState);
        Assert.NotNull(result.Value.Steps[1].Oam);
    }

    [Fact]
    public void Timeline_counters_progress_with_frames_and_reset_with_rom()
    {
        using var temp = new TempRom(CreateMinimalNrom());
        using var session = new ManagedNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var initial = session.GetState();
        var run = session.RunFrame(1);
        var step = session.StepInstruction(1);
        var reset = session.Reset();
        var resetState = session.GetState();

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(initial.IsSuccess, initial.Error?.Message);
        Assert.Equal(0UL, initial.Value.Timeline.Frames);
        Assert.Equal(0UL, initial.Value.Timeline.Cycles);
        Assert.True(run.IsSuccess, run.Error?.Message);
        Assert.Equal(1UL, run.Value.Timeline.Frames);
        Assert.True(run.Value.Timeline.Cycles > 0);
        Assert.True(step.IsSuccess, step.Error?.Message);
        Assert.Equal(1, step.Value.InstructionsRun);
        Assert.True(step.Value.Timeline.Instructions > run.Value.Timeline.Instructions);
        Assert.True(reset.IsSuccess, reset.Error?.Message);
        Assert.Equal(0UL, resetState.Value.Timeline.Frames);
        Assert.Equal(0UL, resetState.Value.Timeline.Cycles);
    }

    [Fact]
    public void Step_over_runs_jsr_and_stops_at_return_address()
    {
        using var temp = new TempRom(CreateMinimalNrom(
        [
            0x20, 0x06, 0x80, // JSR $8006
            0xA9, 0x55,       // LDA #$55
            0xEA,             // NOP
            0xA9, 0x42,       // LDA #$42
            0x60,             // RTS
        ]));
        using var session = new ManagedNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var step = session.StepOver(10);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(step.IsSuccess, step.Error?.Message);
        Assert.Equal("step_over", step.Value.Reason);
        Assert.Equal("0x8003", step.Value.Pc);
        Assert.Equal("0x42", step.Value.Registers.A);
    }

    [Fact]
    public void Save_state_and_load_state_restore_cpu_ram_and_registers()
    {
        using var temp = new TempRom(CreateMinimalNrom());
        using var state = new TempPath("state.nesstate");
        using var session = new ManagedNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var step = session.StepInstruction(1);
        var write = session.WriteMemory(0x0002, [0x11]);
        var save = session.SaveState(state.Path);
        var mutate = session.WriteMemory(0x0002, [0x22]);
        var loadState = session.LoadState(state.Path);
        var memory = session.ReadMemory(0x0002, 1);
        var registers = session.ReadRegisters();

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(step.IsSuccess, step.Error?.Message);
        Assert.True(write.IsSuccess, write.Error?.Message);
        Assert.True(save.IsSuccess, save.Error?.Message);
        Assert.True(mutate.IsSuccess, mutate.Error?.Message);
        Assert.True(loadState.IsSuccess, loadState.Error?.Message);
        Assert.True(memory.IsSuccess, memory.Error?.Message);
        Assert.True(registers.IsSuccess, registers.Error?.Message);
        Assert.Equal("11", memory.Value.BytesHex);
        Assert.Equal("0x42", registers.Value.A);
        Assert.Equal("0x8002", registers.Value.Pc);
    }

    private static byte[] CreateMinimalNrom(byte[]? program = null, byte flags6 = 0)
    {
        var rom = new byte[16 + 16 * 1024 + 8 * 1024];
        rom[0] = (byte)'N';
        rom[1] = (byte)'E';
        rom[2] = (byte)'S';
        rom[3] = 0x1A;
        rom[4] = 1;
        rom[5] = 1;
        rom[6] = flags6;

        var prg = rom.AsSpan(16, 16 * 1024);
        (program ?? [0xA9, 0x42, 0xEA, 0x4C, 0x02, 0x80]).CopyTo(prg);
        prg[0x3FFC] = 0x00;
        prg[0x3FFD] = 0x80;
        return rom;
    }

    private static byte[] WritePpuByte(ushort address, byte value) =>
    [
        0xA9, (byte)(address >> 8), // LDA #high
        0x8D, 0x06, 0x20, // STA $2006
        0xA9, (byte)(address & 0xFF), // LDA #low
        0x8D, 0x06, 0x20, // STA $2006
        0xA9, value, // LDA #value
        0x8D, 0x07, 0x20, // STA $2007
    ];

    private static string FirstTile(TilemapDumpResult tilemap) =>
        tilemap.Rows[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

    private sealed class TempRom : IDisposable
    {
        public TempRom(byte[] bytes)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.nes");
            File.WriteAllBytes(Path, bytes);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    private sealed class TempPath : IDisposable
    {
        public TempPath(string extension)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.{extension.TrimStart('.')}");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
