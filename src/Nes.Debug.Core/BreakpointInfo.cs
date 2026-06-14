namespace Nes.Debug.Core;

public sealed record BreakpointInfo(string Id, string Address, ushort AddressValue, string? Condition, bool Enabled)
{
    public BreakpointCondition? ParsedCondition { get; init; }
}
