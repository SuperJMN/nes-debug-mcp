using Nes.Debug.Core;
using Nes.Debug.Emulator;

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

    private static byte[] CreateMinimalNrom(byte[]? program = null)
    {
        var rom = new byte[16 + 16 * 1024 + 8 * 1024];
        rom[0] = (byte)'N';
        rom[1] = (byte)'E';
        rom[2] = (byte)'S';
        rom[3] = 0x1A;
        rom[4] = 1;
        rom[5] = 1;

        var prg = rom.AsSpan(16, 16 * 1024);
        (program ?? [0xA9, 0x42, 0xEA, 0x4C, 0x02, 0x80]).CopyTo(prg);
        prg[0x3FFC] = 0x00;
        prg[0x3FFD] = 0x80;
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
