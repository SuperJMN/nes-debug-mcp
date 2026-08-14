namespace Nes.Debug.Tests;

internal sealed record Mmc1BankedTestRom(
    byte[] Bytes,
    byte[] PrgSentinels,
    byte[] Chr4KSentinels);

/// <summary>
/// Builds deterministic mapper-1 fixtures independently of both emulator implementations.
/// </summary>
internal static class Mmc1TestRomBuilder
{
    private const int HeaderSize = 16;
    private const int PrgBankSize = 16 * 1024;
    private const int ChrBankSize = 8 * 1024;
    private const int Chr4KBankSize = 4 * 1024;

    public static DebugContractRom CreateContractProgram(
        byte[] program,
        int prgRomBanks,
        int chrRomBanks,
        NromMirroring mirroring)
    {
        var bytes = CreateRom(
            program,
            prgRomBanks,
            chrRomBanks,
            mirroring,
            programBank: 0,
            resetAddress: 0x8000);
        return new DebugContractRom(bytes, 1, prgRomBanks, chrRomBanks);
    }

    public static Mmc1BankedTestRom CreateBankedFixture(
        byte[]? program = null,
        NromMirroring mirroring = NromMirroring.Vertical)
    {
        program ??=
        [
            0xE6, 0x00,       // $C000: INC $00
            0x4C, 0x00, 0xC0, // $C002: JMP $C000
        ];

        const int prgRomBanks = 4;
        const int chrRomBanks = 2;
        var bytes = CreateRom(
            program,
            prgRomBanks,
            chrRomBanks,
            mirroring,
            programBank: prgRomBanks - 1,
            resetAddress: 0xC000);

        byte[] prgSentinels = [0xA0, 0xB1, 0xC2, 0xD3];
        var prg = bytes.AsSpan(HeaderSize, prgRomBanks * PrgBankSize);
        for (var bank = 0; bank < prgSentinels.Length; bank++)
        {
            prg[bank * PrgBankSize + 0x3FF0] = prgSentinels[bank];
        }

        byte[] chr4KSentinels = [0x40, 0x51, 0x62, 0x73];
        var chr = bytes.AsSpan(HeaderSize + prgRomBanks * PrgBankSize);
        for (var bank = 0; bank < chr4KSentinels.Length; bank++)
        {
            chr.Slice(bank * Chr4KBankSize, 16).Fill(chr4KSentinels[bank]);
        }

        return new Mmc1BankedTestRom(bytes, prgSentinels, chr4KSentinels);
    }

    private static byte[] CreateRom(
        byte[] program,
        int prgRomBanks,
        int chrRomBanks,
        NromMirroring mirroring,
        int programBank,
        ushort resetAddress)
    {
        if (prgRomBanks is not (2 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(prgRomBanks));
        }

        if (chrRomBanks is not (0 or 1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(chrRomBanks));
        }

        if (programBank < 0 || programBank >= prgRomBanks)
        {
            throw new ArgumentOutOfRangeException(nameof(programBank));
        }

        var maximumProgramLength = programBank == prgRomBanks - 1
            ? PrgBankSize - 6
            : PrgBankSize;
        if (program.Length > maximumProgramLength)
        {
            throw new ArgumentException("Program does not fit in one MMC1 PRG bank.", nameof(program));
        }

        var prgLength = prgRomBanks * PrgBankSize;
        var bytes = new byte[HeaderSize + prgLength + chrRomBanks * ChrBankSize];
        bytes[0] = (byte)'N';
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'S';
        bytes[3] = 0x1A;
        bytes[4] = (byte)prgRomBanks;
        bytes[5] = (byte)chrRomBanks;
        bytes[6] = (byte)(0x10 | (mirroring == NromMirroring.Vertical ? 0x01 : 0x00));

        var prg = bytes.AsSpan(HeaderSize, prgLength);
        program.CopyTo(prg.Slice(programBank * PrgBankSize));
        var vectorOffset = prgLength - 6;
        for (var vector = 0; vector < 3; vector++)
        {
            prg[vectorOffset + vector * 2] = (byte)resetAddress;
            prg[vectorOffset + vector * 2 + 1] = (byte)(resetAddress >> 8);
        }

        return bytes;
    }
}
