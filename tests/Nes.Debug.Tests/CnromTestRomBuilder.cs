namespace Nes.Debug.Tests;

internal sealed record CnromTestRom(
    byte[] Bytes,
    int InitializationInstructionCount);

/// <summary>
/// Builds deterministic, source-versioned mapper-3 fixtures without relying on an assembler
/// or on either emulator implementation.
/// </summary>
internal static class CnromTestRomBuilder
{
    public const int Mapper = 3;
    public const int PrgRomBanks = 2;
    public const int ChrRomBanks = 4;

    private const int HeaderSize = 16;
    private const int PrgBankSize = 16 * 1024;
    private const int ChrBankSize = 8 * 1024;

    public static readonly string[] BankTileHex =
    [
        "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00",
        "FF FF FF FF FF FF FF FF 00 00 00 00 00 00 00 00",
        "00 00 00 00 00 00 00 00 FF FF FF FF FF FF FF FF",
        "FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF",
    ];

    public static readonly byte[] BankPaletteIndices = [0x0F, 0x30, 0x21, 0x11];

    public static CnromTestRom CreateProgram(
        byte[] program,
        NromMirroring mirroring = NromMirroring.Horizontal,
        byte[]? bankZeroChr = null)
    {
        var chr = CreateChrRom();
        if (bankZeroChr is not null)
        {
            if (bankZeroChr.Length > ChrBankSize)
            {
                throw new ArgumentException("Bank-zero CHR data does not fit in one CNROM bank.", nameof(bankZeroChr));
            }

            bankZeroChr.CopyTo(chr, 0);
        }

        return new CnromTestRom(CreateRom(program, chr, mirroring), 0);
    }

    public static CnromTestRom CreateRenderingFixture(NromMirroring mirroring)
    {
        var program = new List<byte>();
        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F00, BankPaletteIndices[0]));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F01, BankPaletteIndices[1]));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F02, BankPaletteIndices[2]));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F03, BankPaletteIndices[3]));
        program.AddRange(
        [
            0xA9, 0x00,
            0x8D, 0x00, 0x20, // background pattern table at $0000
            0xA9, 0x0A,
            0x8D, 0x01, 0x20, // enable background rendering including the left edge
        ]);
        var loop = (ushort)(0x8000 + program.Count);
        program.AddRange(
        [
            0xE6, 0x00,
            0x4C, (byte)loop, (byte)(loop >> 8),
        ]);

        return new CnromTestRom(CreateRom(program.ToArray(), CreateChrRom(), mirroring), 28);
    }

    public static CnromTestRom CreateMirroringFixture(NromMirroring mirroring)
    {
        var program = new List<byte>();
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2000, 0x11));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2400, 0x22));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2800, 0x33));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x2C00, 0x44));
        var loop = (ushort)(0x8000 + program.Count);
        program.AddRange([0x4C, (byte)loop, (byte)(loop >> 8)]);

        return new CnromTestRom(CreateRom(program.ToArray(), CreateChrRom(), mirroring), 25);
    }

    public static CnromTestRom CreateSavestateFixture(NromMirroring mirroring)
    {
        var program = new List<byte>();
        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F00, BankPaletteIndices[0]));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F01, BankPaletteIndices[1]));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F02, BankPaletteIndices[2]));
        program.AddRange(NromTestRomBuilder.PpuWrite(0x3F03, BankPaletteIndices[3]));
        program.AddRange(
        [
            0xA9, 0x0A,
            0x8D, 0x01, 0x20, // enable background rendering
        ]);
        var loop = (ushort)(0x8000 + program.Count);
        program.AddRange(
        [
            0xE6, 0x00,       // mutate CPU RAM
            0xA9, 0x00,
            0x8D, 0x03, 0x20, // OAMADDR = 0
            0xA5, 0x00,
            0x8D, 0x04, 0x20, // mutate sprite 0 Y
            0xA9, 0x20,
            0x8D, 0x06, 0x20,
            0xA9, 0x00,
            0x8D, 0x06, 0x20,
            0xA5, 0x00,
            0x8D, 0x07, 0x20, // mutate nametable[0]
            0x4C, (byte)loop, (byte)(loop >> 8),
        ]);

        return new CnromTestRom(CreateRom(program.ToArray(), CreateChrRom(), mirroring), 0);
    }

    private static byte[] CreateRom(byte[] program, byte[] chr, NromMirroring mirroring)
    {
        var prgLength = PrgRomBanks * PrgBankSize;
        if (program.Length > prgLength - 6)
        {
            throw new ArgumentException("Program does not fit before the CNROM vectors.", nameof(program));
        }

        if (chr.Length != ChrRomBanks * ChrBankSize)
        {
            throw new ArgumentException("CNROM fixture must contain exactly four 8 KiB CHR banks.", nameof(chr));
        }

        var rom = new byte[HeaderSize + prgLength + chr.Length];
        rom[0] = (byte)'N';
        rom[1] = (byte)'E';
        rom[2] = (byte)'S';
        rom[3] = 0x1A;
        rom[4] = PrgRomBanks;
        rom[5] = ChrRomBanks;
        rom[6] = (byte)((Mapper << 4) | (mirroring == NromMirroring.Vertical ? 0x01 : 0x00));

        var prg = rom.AsSpan(HeaderSize, prgLength);
        program.CopyTo(prg);
        var vectorOffset = prgLength - 6;
        prg[vectorOffset] = 0x00;     // NMI -> $8000
        prg[vectorOffset + 1] = 0x80;
        prg[vectorOffset + 2] = 0x00; // RESET -> $8000
        prg[vectorOffset + 3] = 0x80;
        prg[vectorOffset + 4] = 0x00; // IRQ -> $8000
        prg[vectorOffset + 5] = 0x80;
        chr.CopyTo(rom, HeaderSize + prgLength);
        return rom;
    }

    private static byte[] CreateChrRom()
    {
        var chr = new byte[ChrRomBanks * ChrBankSize];
        for (var bank = 0; bank < ChrRomBanks; bank++)
        {
            var tile = BankTileHex[bank]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => Convert.ToByte(value, 16))
                .ToArray();
            tile.CopyTo(chr, bank * ChrBankSize);
        }

        return chr;
    }
}
