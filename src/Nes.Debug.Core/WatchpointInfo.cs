namespace Nes.Debug.Core;

public sealed record WatchpointInfo(string Id, string Address, ushort AddressValue, WatchpointMode Mode, bool Enabled);
