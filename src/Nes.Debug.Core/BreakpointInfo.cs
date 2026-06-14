namespace Nes.Debug.Core;

public sealed record BreakpointInfo(string Id, string Address, ushort AddressValue, bool Enabled);
