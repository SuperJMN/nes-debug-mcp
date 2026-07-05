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

    private static byte[] CreateMinimalMmc3()
    {
        var rom = new byte[16 + 2 * 16 * 1024 + 8 * 1024];
        rom[0] = (byte)'N';
        rom[1] = (byte)'E';
        rom[2] = (byte)'S';
        rom[3] = 0x1A;
        rom[4] = 2;
        rom[5] = 1;
        rom[6] = 0x40;

        var prg = rom.AsSpan(16, 2 * 16 * 1024);
        new byte[] { 0xA9, 0x42, 0xEA, 0x4C, 0x02, 0x80 }.CopyTo(prg);
        prg[0x7FFC] = 0x00;
        prg[0x7FFD] = 0x80;
        return rom;
    }

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
