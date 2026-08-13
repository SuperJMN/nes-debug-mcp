namespace Nes.Debug.Tests;

public enum NromMirroring
{
    Horizontal,
    Vertical,
}

internal sealed record NromTestRom(
    byte[] Bytes,
    int PrgRomBanks,
    int ChrRomBanks,
    NromMirroring Mirroring,
    int InitializationInstructionCount,
    byte LowerPrgSentinel,
    byte UpperPrgSentinel,
    string ChrSentinel);

/// <summary>
/// Builds deterministic, source-versioned mapper-0 fixtures without relying on an assembler
/// or on either emulator implementation.
/// </summary>
internal static class NromTestRomBuilder
{
    public const int FixtureVersion = 1;

    private const int HeaderSize = 16;
    private const int PrgBankSize = 16 * 1024;
    private const int ChrBankSize = 8 * 1024;

    public static NromTestRom CreateMatrixFixture(
        int prgRomBanks,
        int chrRomBanks,
        NromMirroring mirroring)
    {
        if (prgRomBanks is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(prgRomBanks));
        }

        if (chrRomBanks is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(chrRomBanks));
        }

        var program = new List<byte>();
        AppendPpuWrite(program, 0x2000, 0x11);
        AppendPpuWrite(program, 0x2400, 0x22);
        AppendPpuWrite(program, 0x2800, 0x33);
        AppendPpuWrite(program, 0x2C00, 0x44);

        var instructionCount = 25;
        if (chrRomBanks == 0)
        {
            AppendPpuWrite(program, 0x0000, 0x5A);
            AppendPpuWrite(program, 0x0001, 0xA5);
            instructionCount += 12;
        }

        var loopAddress = (ushort)(0x8000 + program.Count);
        program.AddRange([0x4C, (byte)loopAddress, (byte)(loopAddress >> 8)]);

        const byte lowerPrgSentinel = 0x6D;
        var upperPrgSentinel = prgRomBanks == 1 ? lowerPrgSentinel : (byte)0xD6;
        var rom = CreateRom(program.ToArray(), prgRomBanks, chrRomBanks, mirroring);
        var prg = rom.AsSpan(HeaderSize, prgRomBanks * PrgBankSize);
        prg[0x3FF0] = lowerPrgSentinel;
        if (prgRomBanks == 2)
        {
            prg[0x7FF0] = upperPrgSentinel;
        }

        const string chrRomSentinel = "DE AD BE EF 12 34 56 78 87 65 43 21 FE ED FA CE";
        if (chrRomBanks == 1)
        {
            byte[] chrBytes = [0xDE, 0xAD, 0xBE, 0xEF, 0x12, 0x34, 0x56, 0x78, 0x87, 0x65, 0x43, 0x21, 0xFE, 0xED, 0xFA, 0xCE];
            chrBytes.CopyTo(rom.AsSpan(HeaderSize + prgRomBanks * PrgBankSize));
        }

        return new NromTestRom(
            rom,
            prgRomBanks,
            chrRomBanks,
            mirroring,
            instructionCount,
            lowerPrgSentinel,
            upperPrgSentinel,
            chrRomBanks == 0 ? "5A A5" : chrRomSentinel);
    }

    public static NromTestRom CreateProgram(
        byte[] program,
        int prgRomBanks = 1,
        int chrRomBanks = 1,
        NromMirroring mirroring = NromMirroring.Horizontal)
    {
        var rom = CreateRom(program, prgRomBanks, chrRomBanks, mirroring);
        return new NromTestRom(rom, prgRomBanks, chrRomBanks, mirroring, 0, 0, 0, "");
    }

    public static byte[] PpuWrite(ushort address, byte value) =>
    [
        0xA9, (byte)(address >> 8), // LDA #high
        0x8D, 0x06, 0x20,         // STA $2006
        0xA9, (byte)address,       // LDA #low
        0x8D, 0x06, 0x20,         // STA $2006
        0xA9, value,               // LDA #value
        0x8D, 0x07, 0x20,         // STA $2007
    ];

    private static byte[] CreateRom(
        byte[] program,
        int prgRomBanks,
        int chrRomBanks,
        NromMirroring mirroring)
    {
        if (prgRomBanks is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(prgRomBanks));
        }

        if (chrRomBanks is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(chrRomBanks));
        }

        var prgLength = prgRomBanks * PrgBankSize;
        if (program.Length > prgLength - 6)
        {
            throw new ArgumentException("Program does not fit before the NROM vectors.", nameof(program));
        }

        var rom = new byte[HeaderSize + prgLength + chrRomBanks * ChrBankSize];
        rom[0] = (byte)'N';
        rom[1] = (byte)'E';
        rom[2] = (byte)'S';
        rom[3] = 0x1A;
        rom[4] = (byte)prgRomBanks;
        rom[5] = (byte)chrRomBanks;
        rom[6] = mirroring == NromMirroring.Vertical ? (byte)0x01 : (byte)0x00;

        var prg = rom.AsSpan(HeaderSize, prgLength);
        program.CopyTo(prg);
        var vectorOffset = prgLength - 6;
        prg[vectorOffset] = 0x00;     // NMI -> $8000
        prg[vectorOffset + 1] = 0x80;
        prg[vectorOffset + 2] = 0x00; // RESET -> $8000
        prg[vectorOffset + 3] = 0x80;
        prg[vectorOffset + 4] = 0x00; // IRQ -> $8000
        prg[vectorOffset + 5] = 0x80;
        return rom;
    }

    private static void AppendPpuWrite(List<byte> program, ushort address, byte value) =>
        program.AddRange(PpuWrite(address, value));
}

internal sealed class TemporaryTestFile : IDisposable
{
    private TemporaryTestFile(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TemporaryTestFile FromBytes(byte[] bytes, string extension = "nes")
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"nes-mcp-fixture-v{NromTestRomBuilder.FixtureVersion}-{Guid.NewGuid():N}.{extension.TrimStart('.')}");
        File.WriteAllBytes(path, bytes);
        return new TemporaryTestFile(path);
    }

    public static TemporaryTestFile FromText(string text, string extension)
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"nes-mcp-fixture-v{NromTestRomBuilder.FixtureVersion}-{Guid.NewGuid():N}.{extension.TrimStart('.')}");
        File.WriteAllText(path, text);
        return new TemporaryTestFile(path);
    }

    public static TemporaryTestFile Empty(string extension) =>
        new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"nes-mcp-fixture-v{NromTestRomBuilder.FixtureVersion}-{Guid.NewGuid():N}.{extension.TrimStart('.')}"));

    public void Dispose()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
