namespace Nes.Debug.Core;

public sealed class BreakpointCollection
{
    private readonly Dictionary<string, BreakpointInfo> byId = [];
    private int nextId = 1;

    public IReadOnlyCollection<BreakpointInfo> All => byId.Values.ToArray();

    public BreakpointInfo Set(ushort address, string? condition, BreakpointCondition? parsedCondition = null)
    {
        if (parsedCondition is null)
        {
            _ = BreakpointCondition.TryParse(condition, out parsedCondition, out _);
        }

        var info = new BreakpointInfo($"bp-{nextId++}", Hex.FormatWord(address), address, condition, true)
        {
            ParsedCondition = parsedCondition,
        };
        byId.Add(info.Id, info);
        return info;
    }

    public bool Clear(string id) => byId.Remove(id);

    public void ClearAll() => byId.Clear();

    public bool Contains(ushort address) =>
        byId.Values.Any(breakpoint => breakpoint.Enabled && breakpoint.AddressValue == address);

    public bool HasBreakpointAt(ushort address) => Contains(address);

    public IReadOnlyCollection<BreakpointInfo> FindAll(ushort address) =>
        byId.Values.Where(breakpoint => breakpoint.Enabled && breakpoint.AddressValue == address).ToArray();
}
