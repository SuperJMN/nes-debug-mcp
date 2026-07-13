using System.Numerics;
using System.Security.Cryptography;

namespace Nes.Debug.Core;

public static class ScreenFrameAnalyzer
{
    public const int Width = 256;
    public const int Height = 240;
    public const int PixelCount = Width * Height;

    private const int TileSize = 8;

    public static DebugResult<int> Capture(IPaletteIndexFrameSource source, Memory<byte> destination)
    {
        if (destination.Length < PixelCount)
        {
            return DebugResult<int>.Failure("invalid_screen_frame_buffer", $"Frame buffer must contain at least {PixelCount} bytes.");
        }

        var capture = source.CopyPaletteIndexFrame(destination);
        if (!capture.IsSuccess)
        {
            return DebugResult<int>.Failure(capture.Error!.Code, capture.Error.Message);
        }

        return capture.Value == PixelCount
            ? capture
            : DebugResult<int>.Failure("invalid_screen_frame", $"Expected {PixelCount} palette indices from the active backend.");
    }

    public static ScreenFrameObservation Compare(
        ReadOnlySpan<byte> previous,
        ReadOnlySpan<byte> current,
        int frameOffset,
        ulong totalFrame)
    {
        var changedPixels = 0;
        var minX = Width;
        var minY = Height;
        var maxX = -1;
        var maxY = -1;
        var tileRows = new uint[Height / TileSize];

        for (var index = 0; index < current.Length; index++)
        {
            if (previous[index] == current[index])
            {
                continue;
            }

            changedPixels++;
            var x = index % Width;
            var y = index / Width;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            tileRows[y / TileSize] |= 1u << (x / TileSize);
        }

        var changedTileRows = tileRows
            .Select((mask, row) => (mask, row))
            .Where(item => item.mask != 0)
            .Select(item => new ScreenChangedTileRow(item.row, $"0x{item.mask:X8}"))
            .ToArray();
        var changedTiles = tileRows.Sum(mask => BitOperations.PopCount(mask));
        var bounds = changedPixels == 0
            ? null
            : new ScreenChangeBounds(minX, minY, maxX - minX + 1, maxY - minY + 1);

        return new ScreenFrameObservation(
            frameOffset,
            totalFrame,
            Hash(current),
            changedPixels,
            changedTiles,
            bounds,
            changedTileRows);
    }

    public static string Hash(ReadOnlySpan<byte> values) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(values)).ToLowerInvariant()}";
}
