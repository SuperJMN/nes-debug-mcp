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

    private static byte[] CreateMinimalNrom()
    {
        var rom = new byte[16 + 16 * 1024 + 8 * 1024];
        rom[0] = (byte)'N';
        rom[1] = (byte)'E';
        rom[2] = (byte)'S';
        rom[3] = 0x1A;
        rom[4] = 1;
        rom[5] = 1;

        var prg = rom.AsSpan(16, 16 * 1024);
        prg[0x0000] = 0xA9; // LDA #$42
        prg[0x0001] = 0x42;
        prg[0x0002] = 0xEA; // NOP
        prg[0x0003] = 0x4C; // JMP $8002
        prg[0x0004] = 0x02;
        prg[0x0005] = 0x80;
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
}
