namespace Nes.Debug.Core;

public static class MemoryFormatter
{
    public static string ToAscii(IEnumerable<byte> bytes) =>
        new(bytes.Select(value => value is >= 0x20 and <= 0x7E ? (char)value : '.').ToArray());
}
