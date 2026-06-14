using System.Globalization;

namespace Nes.Debug.Core;

public sealed record NesAddress(ushort Address)
{
    public static DebugResult<NesAddress> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Invalid(text);
        }

        var normalized = text.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }
        else if (normalized.StartsWith('$'))
        {
            normalized = normalized[1..];
        }

        return normalized.Length > 0
            && int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            && value <= 0xFFFF
            ? DebugResult<NesAddress>.Success(new NesAddress((ushort)value))
            : Invalid(text);
    }

    public override string ToString() => Hex.FormatWord(Address);

    private static DebugResult<NesAddress> Invalid(string? text) =>
        DebugResult<NesAddress>.Failure("invalid_address", $"'{text}' is not a valid NES CPU address.");
}
