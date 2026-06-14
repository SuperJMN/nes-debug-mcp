namespace Nes.Debug.Core;

public static class Hex
{
    public static string FormatByte(byte value) => $"0x{value:X2}";

    public static string FormatWord(ushort value) => $"0x{value:X4}";

    public static string FormatBytes(IEnumerable<byte> bytes) => string.Join(' ', bytes.Select(value => value.ToString("X2")));
}
