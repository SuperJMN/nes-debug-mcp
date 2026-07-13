namespace Nes.Debug.Core;

public static class PpuRegisterTracing
{
    public const int MaxFrames = 600;
    public const int MaxEvents = 10_000;
    public const int MaxInstructionsPerFrame = 100_000;

    public static IReadOnlySet<ushort> DefaultRegisters { get; } = new HashSet<ushort>
    {
        0x2000,
        0x2005,
        0x2006,
        0x2007,
    };

    public static string RegisterName(ushort address) => address switch
    {
        0x2000 => "PPUCTRL",
        0x2001 => "PPUMASK",
        0x2002 => "PPUSTATUS",
        0x2003 => "OAMADDR",
        0x2004 => "OAMDATA",
        0x2005 => "PPUSCROLL",
        0x2006 => "PPUADDR",
        0x2007 => "PPUDATA",
        _ => "UNKNOWN",
    };
}
