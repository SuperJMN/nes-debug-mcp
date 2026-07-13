namespace Nes.Debug.Core;

public static class PaletteIndexScreen
{
    public const string SummaryFormat = "palette_indices";
    public const string RawFormat = "palette_indices_raw";
    public const int MaxAutomaticRawPixels = 1024;

    public static bool TryParseFormat(string format, out bool forceRaw)
    {
        forceRaw = format.Equals(RawFormat, StringComparison.OrdinalIgnoreCase);
        return forceRaw || format.Equals(SummaryFormat, StringComparison.OrdinalIgnoreCase);
    }

    public static ScreenRegionResult Build(
        IReadOnlyList<byte> frame,
        int screenWidth,
        int x,
        int y,
        int width,
        int height,
        bool forceRaw)
    {
        var includeRaw = forceRaw || width * height <= MaxAutomaticRawPixels;
        var values = includeRaw ? new List<int>(width * height) : null;
        var histogram = new Dictionary<string, int>(StringComparer.Ordinal);
        var rowHashes = new List<string>(height);

        for (var row = 0; row < height; row++)
        {
            var hash = 2166136261u;
            for (var column = 0; column < width; column++)
            {
                var paletteIndex = frame[(y + row) * screenWidth + x + column] & 0x3F;
                values?.Add(paletteIndex);

                var key = paletteIndex.ToString();
                histogram[key] = histogram.TryGetValue(key, out var count) ? count + 1 : 1;
                hash ^= (byte)paletteIndex;
                hash *= 16777619u;
            }

            rowHashes.Add($"0x{hash:X8}");
        }

        return new ScreenRegionResult(
            x,
            y,
            width,
            height,
            forceRaw ? RawFormat : SummaryFormat,
            width * height,
            values,
            histogram,
            rowHashes);
    }
}
