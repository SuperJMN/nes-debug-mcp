using Nes.Debug.Core;
using Nes.Debug.Emulator;

namespace Nes.Debug.Tests;

public sealed class AprNesDebugSessionTests
{
    [Fact]
    public void Load_rom_accepts_mapper4_and_steps_instruction()
    {
        using var temp = new TempRom(CreateMinimalMmc3());
        using var session = new AprNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var before = session.ReadRegisters();
        var step = session.StepInstruction(1);
        var memory = session.ReadMemory(0x8000, 2);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(before.IsSuccess, before.Error?.Message);
        Assert.True(step.IsSuccess, step.Error?.Message);
        Assert.True(memory.IsSuccess, memory.Error?.Message);
        Assert.Equal(4, load.Value.Mapper);
        Assert.Equal("0x8000", before.Value.Pc);
        Assert.Equal("0x42", step.Value.Registers.A);
        Assert.Equal("0x8002", step.Value.Registers.Pc);
        Assert.Equal("A9 42", memory.Value.BytesHex);
    }

    [Fact]
    public void Auto_backend_uses_aprnes_for_mapper4()
    {
        using var temp = new TempRom(CreateMinimalMmc3());
        using var session = new AutoNesDebugSession(new ManagedNesDebugSession(), new AprNesDebugSession());

        var load = session.LoadRom(temp.Path);
        var step = session.StepInstruction(1);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(step.IsSuccess, step.Error?.Message);
        Assert.Equal(4, load.Value.Mapper);
        Assert.Equal("0x42", step.Value.Registers.A);
    }

    [Fact]
    public void Save_state_and_load_state_restore_cpu_ram_and_registers()
    {
        using var temp = new TempRom(CreateMinimalMmc3());
        using var state = new TempPath("nesstate");
        using var session = new AprNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var step = session.StepInstruction(1);
        var write = session.WriteMemory(0x0002, [0x11]);
        var save = session.SaveState(state.Path);
        var mutateStep = session.StepInstruction(1);
        var mutateMemory = session.WriteMemory(0x0002, [0x22]);
        var loadState = session.LoadState(state.Path);
        var memory = session.ReadMemory(0x0002, 1);
        var registers = session.ReadRegisters();

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(step.IsSuccess, step.Error?.Message);
        Assert.True(write.IsSuccess, write.Error?.Message);
        Assert.True(save.IsSuccess, save.Error?.Message);
        Assert.True(mutateStep.IsSuccess, mutateStep.Error?.Message);
        Assert.True(mutateMemory.IsSuccess, mutateMemory.Error?.Message);
        Assert.True(loadState.IsSuccess, loadState.Error?.Message);
        Assert.True(memory.IsSuccess, memory.Error?.Message);
        Assert.True(registers.IsSuccess, registers.Error?.Message);
        Assert.Equal("11", memory.Value.BytesHex);
        Assert.Equal("0x42", registers.Value.A);
        Assert.Equal("0x8002", registers.Value.Pc);
    }

    [Fact]
    public void Save_state_and_load_state_restore_mapper_bank_registers()
    {
        using var temp = new TempRom(CreateBankedMmc3());
        using var state = new TempPath("nesstate");
        using var session = new AprNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var selectPrg0 = session.WriteMemory(0x8000, [0x06]);
        var selectBank1 = session.WriteMemory(0x8001, [0x01]);
        var bankBeforeSave = session.ReadMemory(0x8003, 1);
        var save = session.SaveState(state.Path);
        var selectBank2 = session.WriteMemory(0x8001, [0x02]);
        var bankAfterMutation = session.ReadMemory(0x8003, 1);
        var loadState = session.LoadState(state.Path);
        var bankAfterLoad = session.ReadMemory(0x8003, 1);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(selectPrg0.IsSuccess, selectPrg0.Error?.Message);
        Assert.True(selectBank1.IsSuccess, selectBank1.Error?.Message);
        Assert.True(bankBeforeSave.IsSuccess, bankBeforeSave.Error?.Message);
        Assert.True(save.IsSuccess, save.Error?.Message);
        Assert.True(selectBank2.IsSuccess, selectBank2.Error?.Message);
        Assert.True(bankAfterMutation.IsSuccess, bankAfterMutation.Error?.Message);
        Assert.True(loadState.IsSuccess, loadState.Error?.Message);
        Assert.True(bankAfterLoad.IsSuccess, bankAfterLoad.Error?.Message);
        Assert.Equal("81", bankBeforeSave.Value.BytesHex);
        Assert.Equal("82", bankAfterMutation.Value.BytesHex);
        Assert.Equal("81", bankAfterLoad.Value.BytesHex);
    }

    [Fact]
    public void Continue_until_break_honors_conditional_breakpoints()
    {
        using var temp = new TempRom(CreateProgramMmc3(
        [
            0xA9, 0x01, // LDA #$01
            0xEA,       // NOP
            0xA9, 0x02, // LDA #$02
            0xEA,       // NOP
            0x4C, 0x05, 0x80, // JMP $8005
        ]));
        using var session = new AprNesDebugSession();

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
        using var temp = new TempRom(CreateProgramMmc3(
        [
            0xA9, 0x7B, // LDA #$7B
            0x85, 0x02, // STA $02
            0x4C, 0x04, 0x80, // JMP $8004
        ]));
        using var session = new AprNesDebugSession();

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
        using var temp = new TempRom(CreateProgramMmc3(
        [
            0xA9, 0x2A, // LDA #$2A
            0x85, 0x02, // STA $02
            0x4C, 0x04, 0x80, // JMP $8004
        ]));
        using var session = new AprNesDebugSession();

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
        using var temp = new TempRom(CreateProgramMmc3(
        [
            0xA9, 0x7B, // LDA #$7B
            0x85, 0x02, // STA $02
            0x4C, 0x04, 0x80, // JMP $8004
        ]));
        using var session = new AprNesDebugSession();

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
    public void Step_over_runs_jsr_and_stops_at_return_address()
    {
        using var temp = new TempRom(CreateProgramMmc3(
        [
            0x20, 0x06, 0x80, // JSR $8006
            0xA9, 0x55,       // LDA #$55
            0xEA,             // NOP
            0xA9, 0x42,       // LDA #$42
            0x60,             // RTS
        ]));
        using var session = new AprNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var step = session.StepOver(10);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(step.IsSuccess, step.Error?.Message);
        Assert.Equal("step_over", step.Value.Reason);
        Assert.Equal("0x8003", step.Value.Pc);
        Assert.Equal("0x42", step.Value.Registers.A);
    }

    [Fact]
    public void Step_out_runs_until_current_subroutine_returns()
    {
        using var temp = new TempRom(CreateProgramMmc3(
        [
            0x20, 0x06, 0x80, // JSR $8006
            0xA9, 0x55,       // LDA #$55
            0xEA,             // NOP
            0xA9, 0x42,       // LDA #$42
            0x60,             // RTS
        ]));
        using var session = new AprNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var enterSubroutine = session.StepInstruction(1);
        var step = session.StepOut(10);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(enterSubroutine.IsSuccess, enterSubroutine.Error?.Message);
        Assert.True(step.IsSuccess, step.Error?.Message);
        Assert.Equal("step_out", step.Value.Reason);
        Assert.Equal("0x8003", step.Value.Pc);
        Assert.Equal("0x42", step.Value.Registers.A);
    }

    [Fact]
    public void Observe_screen_runs_against_a_real_aprnes_session()
    {
        using var temp = new TempRom(CreateMinimalMmc3());
        using var session = new AprNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var observation = session.ObserveScreen(2);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(observation.IsSuccess, observation.Error?.Message);
        Assert.Equal(2, observation.Value.FramesRun);
        Assert.Equal(2, observation.Value.Samples.Count);
        Assert.All(observation.Value.Samples, sample => Assert.StartsWith("sha256:", sample.Hash, StringComparison.Ordinal));
    }

    [Fact]
    public void Dump_nametables_atomically_captures_four_distinct_physical_tables_with_attributes()
    {
        using var temp = new TempRom(CreateProgramMmc3(
        [
            ..WritePpuByte(0x2000, 0x11),
            ..WritePpuByte(0x23C0, 0xA1),
            ..WritePpuByte(0x2400, 0x22),
            ..WritePpuByte(0x27C0, 0xA2),
            ..WritePpuByte(0x2800, 0x33),
            ..WritePpuByte(0x2BC0, 0xA3),
            ..WritePpuByte(0x2C00, 0x44),
            ..WritePpuByte(0x2FC0, 0xA4),
            0x4C, 0x78, 0x80, // JMP $8078
        ], flags6: 0x48));
        using var session = new AprNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var step = session.StepInstruction(50);
        var dump = session.DumpNametables(includeDetails: true);

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(step.IsSuccess, step.Error?.Message);
        Assert.True(dump.IsSuccess, dump.Error?.Message);
        Assert.True(dump.Value.DetailsIncluded);
        Assert.Equal(4, dump.Value.Nametables.Count);
        Assert.Equal(["11", "22", "33", "44"], dump.Value.Nametables.Select(table => FirstByte(table.Detail!.Rows[0])));
        Assert.Equal(["A1", "A2", "A3", "A4"], dump.Value.Nametables.Select(table => FirstByte(table.Detail!.AttributeRows[0])));
        Assert.Equal(4, dump.Value.Nametables.Select(table => table.Hash).Distinct().Count());
    }

    [Fact]
    public void Read_ppu_state_reports_complete_ppuctrl_ppumask_status_and_timeline()
    {
        using var temp = new TempRom(CreateProgramMmc3(
        [
            0xA9, 0xFF,       // LDA #$FF
            0x8D, 0x00, 0x20, // STA $2000
            0xA9, 0xFF,       // LDA #$FF
            0x8D, 0x01, 0x20, // STA $2001
            0xA9, 0x7E,       // LDA #$7E
            0x8D, 0x03, 0x20, // STA $2003
            0x4C, 0x0F, 0x80, // JMP $800F
        ]));
        using var session = new AprNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var step = session.StepInstruction(7);
        var ppu = session.ReadPpuState();

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(step.IsSuccess, step.Error?.Message);
        Assert.True(ppu.IsSuccess, ppu.Error?.Message);
        Assert.Equal("0xFF", ppu.Value.PpuCtrl);
        Assert.Equal("0xFF", ppu.Value.PpuMask);
        Assert.Equal("0x84", ppu.Value.OamAddr);
        Assert.Null(ppu.Value.PpuScroll);
        Assert.Equal(3, ppu.Value.Control.NametableSelect);
        Assert.Equal("0x2C00", ppu.Value.Control.NametableAddress);
        Assert.Equal(32, ppu.Value.Control.VramIncrement);
        Assert.Equal("0x1000", ppu.Value.Control.SpritePatternTableAddress);
        Assert.Equal("0x1000", ppu.Value.Control.BackgroundPatternTableAddress);
        Assert.Equal("8x16", ppu.Value.Control.SpriteSize);
        Assert.True(ppu.Value.Control.NmiEnabled);
        Assert.True(ppu.Value.Mask.Greyscale);
        Assert.True(ppu.Value.Mask.BackgroundLeftEdgeEnabled);
        Assert.True(ppu.Value.Mask.SpriteLeftEdgeEnabled);
        Assert.True(ppu.Value.Mask.BackgroundEnabled);
        Assert.True(ppu.Value.Mask.SpritesEnabled);
        Assert.True(ppu.Value.Mask.EmphasizeRed);
        Assert.True(ppu.Value.Mask.EmphasizeGreen);
        Assert.True(ppu.Value.Mask.EmphasizeBlue);
        Assert.Equal(ppu.Value.Status.VBlank, ppu.Value.VBlank);
        Assert.Equal(step.Value.Timeline, ppu.Value.Timeline);
        Assert.Equal(ppu.Value.PpuAddr, ppu.Value.V);
    }

    [Fact]
    public void Read_ppu_state_exposes_authoritative_v_t_x_w_during_scroll_sequence()
    {
        using var temp = new TempRom(CreateProgramMmc3(
        [
            0xA9, 0x01,       // LDA #$01
            0x8D, 0x00, 0x20, // STA $2000: t nametable = 1
            0xA9, 0x2D,       // LDA #$2D
            0x8D, 0x05, 0x20, // STA $2005: coarse X = 5, fine X = 5, w = 1
            0xA9, 0x9A,       // LDA #$9A
            0x8D, 0x05, 0x20, // STA $2005: fine Y = 2, coarse Y = 19, w = 0
            0x4C, 0x0F, 0x80, // JMP $800F
        ]));
        using var session = new AprNesDebugSession();

        var load = session.LoadRom(temp.Path);
        var firstScroll = session.StepInstruction(4);
        var afterFirst = session.ReadPpuState();
        var secondScroll = session.StepInstruction(2);
        var afterSecond = session.ReadPpuState();

        Assert.True(load.IsSuccess, load.Error?.Message);
        Assert.True(firstScroll.IsSuccess, firstScroll.Error?.Message);
        Assert.True(afterFirst.IsSuccess, afterFirst.Error?.Message);
        Assert.Equal("0x0000", afterFirst.Value.V);
        Assert.Equal("0x0405", afterFirst.Value.T);
        Assert.Equal(5, afterFirst.Value.X);
        Assert.True(afterFirst.Value.W);
        Assert.True(secondScroll.IsSuccess, secondScroll.Error?.Message);
        Assert.True(afterSecond.IsSuccess, afterSecond.Error?.Message);
        Assert.Equal("0x2665", afterSecond.Value.T);
        Assert.Equal(5, afterSecond.Value.X);
        Assert.False(afterSecond.Value.W);
    }

    private static byte[] CreateMinimalMmc3()
    {
        return CreateProgramMmc3([0xA9, 0x42, 0xEA, 0x4C, 0x02, 0x80]);
    }

    private static byte[] CreateProgramMmc3(byte[] program, byte flags6 = 0x40)
    {
        var rom = new byte[16 + 2 * 16 * 1024 + 8 * 1024];
        rom[0] = (byte)'N';
        rom[1] = (byte)'E';
        rom[2] = (byte)'S';
        rom[3] = 0x1A;
        rom[4] = 2;
        rom[5] = 1;
        rom[6] = flags6;

        var prg = rom.AsSpan(16, 2 * 16 * 1024);
        program.CopyTo(prg);
        prg[0x7FFC] = 0x00;
        prg[0x7FFD] = 0x80;
        return rom;
    }

    private static byte[] WritePpuByte(ushort address, byte value) =>
    [
        0xA9, (byte)(address >> 8), // LDA #high
        0x8D, 0x06, 0x20,         // STA $2006
        0xA9, (byte)address,       // LDA #low
        0x8D, 0x06, 0x20,         // STA $2006
        0xA9, value,               // LDA #value
        0x8D, 0x07, 0x20,         // STA $2007
    ];

    private static string FirstByte(string row) => row.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

    private static byte[] CreateBankedMmc3()
    {
        const int prgBanks16K = 4;
        var rom = new byte[16 + prgBanks16K * 16 * 1024 + 8 * 1024];
        rom[0] = (byte)'N';
        rom[1] = (byte)'E';
        rom[2] = (byte)'S';
        rom[3] = 0x1A;
        rom[4] = prgBanks16K;
        rom[5] = 1;
        rom[6] = 0x40;

        var prg = rom.AsSpan(16, prgBanks16K * 16 * 1024);
        for (var bank = 0; bank < prg.Length / 0x2000; bank++)
        {
            prg[bank * 0x2000] = 0xEA;
            prg[bank * 0x2000 + 3] = (byte)(0x80 + bank);
        }

        prg[^4] = 0x00;
        prg[^3] = 0x80;
        return rom;
    }

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
