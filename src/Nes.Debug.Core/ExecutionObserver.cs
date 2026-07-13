namespace Nes.Debug.Core;

public static class ExecutionObserver
{
    public const int MaxFrames = 600;
    public const int MaxMemoryProbes = 16;
    public const int MaxMemoryProbeLength = 64;
    public const int MaxMemoryBytesPerFrame = 256;
    public const int MaxPpuEvents = 2_000;

    public static ExecutionObservationAppliedLimits AppliedLimits { get; } = new(
        MaxFrames,
        MaxMemoryProbes,
        MaxMemoryProbeLength,
        MaxMemoryBytesPerFrame,
        MaxPpuEvents);
}
