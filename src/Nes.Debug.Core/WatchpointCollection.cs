namespace Nes.Debug.Core;

public sealed class WatchpointCollection
{
    private readonly Dictionary<string, WatchpointInfo> byId = [];
    private int nextId = 1;

    public IReadOnlyCollection<WatchpointInfo> All => byId.Values.ToArray();

    public bool HasEnabledReadWatchpoints =>
        byId.Values.Any(watchpoint => watchpoint.Enabled && watchpoint.Mode is WatchpointMode.Read or WatchpointMode.Access);

    public WatchpointInfo Set(ushort address, WatchpointMode mode)
    {
        var info = new WatchpointInfo($"wp-{nextId++}", Hex.FormatWord(address), address, mode, true);
        byId.Add(info.Id, info);
        return info;
    }

    public bool Clear(string id) => byId.Remove(id);

    public void ClearAll() => byId.Clear();

    public bool TryMatch(ushort address, bool isWrite, out WatchpointInfo watchpoint)
    {
        foreach (var candidate in byId.Values)
        {
            if (!candidate.Enabled || candidate.AddressValue != address)
            {
                continue;
            }

            if (isWrite
                ? candidate.Mode is WatchpointMode.Write or WatchpointMode.Access
                : candidate.Mode is WatchpointMode.Read or WatchpointMode.Access)
            {
                watchpoint = candidate;
                return true;
            }
        }

        watchpoint = null!;
        return false;
    }
}
