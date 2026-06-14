using Nes.Debug.Core;

namespace Nes.Debug.Symbols;

public sealed record SymbolInfo(string Name, ushort Address, int? Bank)
{
    public NesAddress ToAddress() => new(Address);
}
