namespace Nes.Corpus.Qualification;

internal enum NesImageFormat
{
    INes,
    Nes20,
}

internal sealed record RomImageHeader(
    int HeaderMapper,
    long RequiredBytes,
    bool HasTrainer,
    NesImageFormat Format);

internal static class RomImageHeaderParser
{
    private const int HeaderSize = 16;
    private const int TrainerSize = 512;
    private const int PrgBankSize = 16 * 1024;
    private const int ChrBankSize = 8 * 1024;

    public static bool TryParse(
        ReadOnlySpan<byte> header,
        long availableBytes,
        int maximumBytes,
        out RomImageHeader? result,
        out SkippedCategory failure)
    {
        result = null;
        failure = SkippedCategory.InvalidImage;
        if (availableBytes > maximumBytes)
        {
            failure = SkippedCategory.OversizeImage;
            return false;
        }

        if (header.Length < HeaderSize ||
            header[0] != (byte)'N' ||
            header[1] != (byte)'E' ||
            header[2] != (byte)'S' ||
            header[3] != 0x1A)
        {
            return false;
        }

        var nes20 = (header[7] & 0x0C) == 0x08;
        var mapper = (header[6] >> 4) | (header[7] & 0xF0);
        if (nes20)
        {
            mapper |= (header[8] & 0x0F) << 8;
        }

        if (!TryGetRomSize(header[4], nes20 ? header[9] & 0x0F : 0, PrgBankSize, nes20, out var prgBytes) ||
            !TryGetRomSize(header[5], nes20 ? header[9] >> 4 : 0, ChrBankSize, nes20, out var chrBytes))
        {
            failure = SkippedCategory.OversizeImage;
            return false;
        }

        var hasTrainer = (header[6] & 0x04) != 0;
        long requiredBytes;
        try
        {
            requiredBytes = checked(HeaderSize + (hasTrainer ? TrainerSize : 0) + prgBytes + chrBytes);
        }
        catch (OverflowException)
        {
            failure = SkippedCategory.OversizeImage;
            return false;
        }

        if (requiredBytes > maximumBytes)
        {
            failure = SkippedCategory.OversizeImage;
            return false;
        }

        if (availableBytes < requiredBytes)
        {
            return false;
        }

        result = new RomImageHeader(mapper, requiredBytes, hasTrainer, nes20 ? NesImageFormat.Nes20 : NesImageFormat.INes);
        return true;
    }

    private static bool TryGetRomSize(byte lsb, int msb, int bankSize, bool nes20, out long size)
    {
        size = 0;
        if (!nes20 || msb != 0x0F)
        {
            try
            {
                size = checked(((long)lsb | ((long)msb << 8)) * bankSize);
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        var exponent = lsb >> 2;
        var multiplier = (lsb & 0x03) * 2 + 1;
        if (exponent >= 63)
        {
            return false;
        }

        try
        {
            size = checked((1L << exponent) * multiplier);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
