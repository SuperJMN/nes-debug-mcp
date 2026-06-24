namespace Nes.Debug.Core;

public sealed record WatchpointInfo(string Id, string Address, ushort AddressValue, WatchpointMode Mode, bool Enabled, int Length)
{
    public WatchpointInfo(string id, string address, ushort addressValue, WatchpointMode mode, bool enabled)
        : this(id, address, addressValue, mode, enabled, 1)
    {
    }
}
