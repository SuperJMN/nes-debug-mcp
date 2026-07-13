using System.Security.Cryptography;

namespace Nes.Debug.Core;

public static class NametableReader
{
    private static readonly IReadOnlySet<ushort> BaseAddresses = new HashSet<ushort>
    {
        0x2000,
        0x2400,
        0x2800,
        0x2C00,
    };

    public static bool IsBaseAddress(ushort address) => BaseAddresses.Contains(address);

    public static TilemapDumpResult Read(ushort address, Func<ushort, int, byte[]> readPpuBytes)
    {
        var bytes = readPpuBytes(address, 0x400);
        return BuildDetail(address, bytes);
    }

    public static NametableDumpResult ReadAll(
        Func<ushort, int, byte[]> readPpuBytes,
        bool includeDetails,
        TimelineCounters timeline)
    {
        var allBytes = readPpuBytes(0x2000, 0x1000);
        var nametables = BaseAddresses
            .Order()
            .Select((address, index) =>
            {
                var bytes = allBytes.AsSpan(index * 0x400, 0x400);
                return new NametableSnapshot(
                    Hex.FormatWord(address),
                    Hash(bytes),
                    Hash(bytes[..(32 * 30)]),
                    Hash(bytes[(32 * 30)..]),
                    includeDetails ? BuildDetail(address, bytes) : null);
            })
            .ToArray();

        return new NametableDumpResult(includeDetails, nametables, timeline);
    }

    private static TilemapDumpResult BuildDetail(ushort address, ReadOnlySpan<byte> bytes)
    {
        var rows = new string[30];
        for (var row = 0; row < rows.Length; row++)
        {
            rows[row] = Hex.FormatBytes(bytes.Slice(row * 32, 32).ToArray());
        }

        var attributeAddress = (ushort)(address + 0x03C0);
        var attributeRows = new string[8];
        for (var row = 0; row < attributeRows.Length; row++)
        {
            attributeRows[row] = Hex.FormatBytes(bytes.Slice(0x3C0 + row * 8, 8).ToArray());
        }

        return new TilemapDumpResult(
            Hex.FormatWord(address),
            32,
            30,
            rows,
            Hex.FormatWord(attributeAddress),
            attributeRows);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
}
