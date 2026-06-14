using System.Globalization;
using Nes.Debug.Core;

namespace Nes.Debug.Symbols;

public sealed class SymbolService
{
    private readonly Dictionary<string, SymbolInfo> symbols = new(StringComparer.Ordinal);

    public DebugResult<int> Load(string path)
    {
        if (!File.Exists(path))
        {
            return DebugResult<int>.Failure("symbol_file_not_found", $"Symbol file was not found: {path}");
        }

        var loaded = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (TryParseLine(line, out var symbol))
            {
                symbols[symbol.Name] = symbol;
                loaded++;
            }
        }

        return DebugResult<int>.Success(loaded);
    }

    public DebugResult<SymbolInfo> Resolve(string name)
    {
        return symbols.TryGetValue(name, out var symbol)
            ? DebugResult<SymbolInfo>.Success(symbol)
            : DebugResult<SymbolInfo>.Failure("symbol_not_found", $"Symbol '{name}' was not loaded.");
    }

    public string? ResolveAddress(ushort address)
    {
        foreach (var symbol in symbols.Values)
        {
            if (symbol.Address == address)
            {
                return symbol.Name;
            }
        }

        return null;
    }

    private static bool TryParseLine(string line, out SymbolInfo symbol)
    {
        symbol = null!;
        var withoutComment = StripComment(line).Trim();
        if (withoutComment.Length == 0)
        {
            return false;
        }

        var parts = withoutComment.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        var addressPart = parts[0];
        var name = parts[1];
        var addressPieces = addressPart.Split(':', StringSplitOptions.TrimEntries);
        int? bank = null;
        string addressText;

        if (addressPieces.Length == 2)
        {
            if (!TryParseHex(addressPieces[0], 0xFF, out var parsedBank))
            {
                return false;
            }

            bank = parsedBank;
            addressText = addressPieces[1];
        }
        else if (addressPieces.Length == 1)
        {
            addressText = addressPieces[0];
        }
        else
        {
            return false;
        }

        if (!TryParseHex(addressText, 0xFFFF, out var address))
        {
            return false;
        }

        symbol = new SymbolInfo(name, (ushort)address, bank);
        return true;
    }

    private static string StripComment(string line)
    {
        var semicolon = line.IndexOf(';', StringComparison.Ordinal);
        var hash = line.IndexOf('#', StringComparison.Ordinal);
        var index = semicolon >= 0 && hash >= 0 ? Math.Min(semicolon, hash) : Math.Max(semicolon, hash);
        return index >= 0 ? line[..index] : line;
    }

    private static bool TryParseHex(string text, int maxValue, out int value)
    {
        value = 0;
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
            && int.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            && value <= maxValue;
    }
}
