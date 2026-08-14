namespace Nes.Debug.Tests;

internal sealed record UxromTestRom(
    byte[] Bytes,
    IReadOnlyList<byte> SwitchableBankSentinels,
    byte FixedBankSentinel,
    ushort ResetVector);

/// <summary>
/// Builds deterministic mapper-2 fixtures with four 16 KiB PRG banks and CHR RAM.
/// The generated ROMs do not depend on an assembler or either emulator implementation.
/// </summary>
internal static class UxromTestRomBuilder
{
    public const int PrgRomBanks = 4;

    private const int HeaderSize = 16;
    private const int PrgBankSize = 16 * 1024;
    private const int VectorOffset = PrgBankSize - 6;

    private static readonly byte[] SwitchableBankSentinels = [0xA0, 0xA1, 0xA2, 0xD6];

    public static DebugContractRom CreateConformanceProgram(
        byte[] program,
        NromMirroring mirroring = NromMirroring.Horizontal)
    {
        var fixture = CreateProgramInSwitchableBank(program, mirroring);
        return new DebugContractRom(
            fixture.Bytes,
            Mapper: 2,
            PrgRomBanks,
            ChrRomBanks: 0);
    }

    public static UxromTestRom CreateBankSelectionFixture(
        NromMirroring mirroring = NromMirroring.Horizontal)
    {
        var rom = CreateEmptyRom(mirroring);
        var prg = rom.AsSpan(HeaderSize, PrgRomBanks * PrgBankSize);
        for (var bank = 0; bank < PrgRomBanks; bank++)
        {
            var marker = (byte)(0xB0 + bank);
            WriteProgram(prg.Slice(bank * PrgBankSize, PrgBankSize),
            [
                0xA9, marker, // LDA #bank marker
                0x85, 0x10,   // STA $10
                0x60,         // RTS
            ]);
        }

        const ushort resetVector = 0xC100;
        WriteProgram(prg.Slice((PrgRomBanks - 1) * PrgBankSize + 0x100, PrgBankSize - 0x100),
        [
            0xA9, 0x02,       // $C100: LDA #bank 2
            0x8D, 0x00, 0x80, // $C102: STA $8000
            0x20, 0x00, 0x80, // $C105: JSR $8000
            0x4C, 0x08, 0xC1, // $C108: JMP $C108
        ]);
        WriteVectors(prg, resetVector);
        return CreateResult(rom, resetVector);
    }

    public static UxromTestRom CreateProgramInFixedBank(
        byte[] program,
        NromMirroring mirroring = NromMirroring.Horizontal)
    {
        var rom = CreateEmptyRom(mirroring);
        var prg = rom.AsSpan(HeaderSize, PrgRomBanks * PrgBankSize);
        WriteProgram(prg.Slice((PrgRomBanks - 1) * PrgBankSize, PrgBankSize), program);
        const ushort resetVector = 0xC000;
        WriteVectors(prg, resetVector);
        return CreateResult(rom, resetVector);
    }

    private static UxromTestRom CreateProgramInSwitchableBank(
        byte[] program,
        NromMirroring mirroring)
    {
        var rom = CreateEmptyRom(mirroring);
        var prg = rom.AsSpan(HeaderSize, PrgRomBanks * PrgBankSize);
        WriteProgram(prg[..PrgBankSize], program);
        const ushort resetVector = 0x8000;
        WriteVectors(prg, resetVector);
        return CreateResult(rom, resetVector);
    }

    private static byte[] CreateEmptyRom(NromMirroring mirroring)
    {
        var rom = new byte[HeaderSize + PrgRomBanks * PrgBankSize];
        rom[0] = (byte)'N';
        rom[1] = (byte)'E';
        rom[2] = (byte)'S';
        rom[3] = 0x1A;
        rom[4] = PrgRomBanks;
        rom[5] = 0; // UxROM uses 8 KiB CHR RAM.
        rom[6] = (byte)(0x20 | (mirroring == NromMirroring.Vertical ? 0x01 : 0x00));

        var prg = rom.AsSpan(HeaderSize);
        prg.Fill(0xEA);
        for (var bank = 0; bank < PrgRomBanks; bank++)
        {
            prg[bank * PrgBankSize + 0x3FF0] = SwitchableBankSentinels[bank];
        }

        return rom;
    }

    private static void WriteProgram(Span<byte> destination, byte[] program)
    {
        if (program.Length > destination.Length - 16)
        {
            throw new ArgumentException("Program does not fit before the UxROM sentinel or vectors.", nameof(program));
        }

        program.CopyTo(destination);
    }

    private static void WriteVectors(Span<byte> prg, ushort resetVector)
    {
        var vectors = prg.Slice((PrgRomBanks - 1) * PrgBankSize + VectorOffset, 6);
        vectors[0] = (byte)resetVector;
        vectors[1] = (byte)(resetVector >> 8);
        vectors[2] = (byte)resetVector;
        vectors[3] = (byte)(resetVector >> 8);
        vectors[4] = (byte)resetVector;
        vectors[5] = (byte)(resetVector >> 8);
    }

    private static UxromTestRom CreateResult(byte[] rom, ushort resetVector) =>
        new(rom, SwitchableBankSentinels, SwitchableBankSentinels[^1], resetVector);
}
