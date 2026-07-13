using System.Numerics;
using System.Security.Cryptography;

namespace Nes.Debug.Core;

public static class ScreenObserver
{
    public const int MaxFrames = 600;

    private const int ScreenWidth = 256;
    private const int ScreenHeight = 240;
    private const int TileSize = 8;

    public static DebugResult<ScreenObservationResult> Observe(INesDebugSession session, int frameCount)
    {
        if (frameCount is < 1 or > MaxFrames)
        {
            return DebugResult<ScreenObservationResult>.Failure(
                "invalid_frame_count",
                $"frameCount must be between 1 and {MaxFrames}.");
        }

        if (session is not IPaletteIndexFrameSource frameSource)
        {
            return DebugResult<ScreenObservationResult>.Failure(
                "screen_observation_not_supported",
                "The active backend does not expose direct palette-index frame capture.");
        }

        var previous = new byte[ScreenWidth * ScreenHeight];
        var current = new byte[ScreenWidth * ScreenHeight];
        var initial = ReadFrame(frameSource, previous);
        if (!initial.IsSuccess)
        {
            return DebugResult<ScreenObservationResult>.Failure(initial.Error!.Code, initial.Error.Message);
        }

        var initialHash = Hash(previous);
        var samples = new List<ScreenFrameObservation>(frameCount);
        var framesRun = 0;
        var hitBreakpoint = false;
        var timeline = new TimelineCounters(0, 0);

        for (var frameOffset = 1; frameOffset <= frameCount; frameOffset++)
        {
            var run = session.RunFrame(1);
            if (!run.IsSuccess)
            {
                return DebugResult<ScreenObservationResult>.Failure(run.Error!.Code, run.Error.Message);
            }

            framesRun += run.Value.FramesRun;
            hitBreakpoint = run.Value.HitBreakpoint;
            timeline = run.Value.Timeline;
            if (run.Value.FramesRun == 0)
            {
                break;
            }

            var captured = ReadFrame(frameSource, current);
            if (!captured.IsSuccess)
            {
                return DebugResult<ScreenObservationResult>.Failure(captured.Error!.Code, captured.Error.Message);
            }

            samples.Add(Compare(previous, current, frameOffset, timeline.Frames));
            (previous, current) = (current, previous);

            if (hitBreakpoint)
            {
                break;
            }
        }

        return DebugResult<ScreenObservationResult>.Success(
            new ScreenObservationResult(frameCount, framesRun, initialHash, samples, hitBreakpoint, timeline));
    }

    private static DebugResult<int> ReadFrame(IPaletteIndexFrameSource source, Memory<byte> destination)
    {
        var capture = source.CopyPaletteIndexFrame(destination);
        if (!capture.IsSuccess)
        {
            return DebugResult<int>.Failure(capture.Error!.Code, capture.Error.Message);
        }

        if (capture.Value != ScreenWidth * ScreenHeight)
        {
            return DebugResult<int>.Failure(
                "invalid_screen_frame",
                $"Expected {ScreenWidth * ScreenHeight} palette indices from the active backend.");
        }

        return capture;
    }

    private static ScreenFrameObservation Compare(
        ReadOnlySpan<byte> previous,
        ReadOnlySpan<byte> current,
        int frameOffset,
        ulong totalFrame)
    {
        var changedPixels = 0;
        var minX = ScreenWidth;
        var minY = ScreenHeight;
        var maxX = -1;
        var maxY = -1;
        var tileRows = new uint[ScreenHeight / TileSize];

        for (var index = 0; index < current.Length; index++)
        {
            if (previous[index] == current[index])
            {
                continue;
            }

            changedPixels++;
            var x = index % ScreenWidth;
            var y = index / ScreenWidth;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            tileRows[y / TileSize] |= 1u << (x / TileSize);
        }

        var changedTileRows = tileRows
            .Select((mask, row) => (mask, row))
            .Where(item => item.mask != 0)
            .Select(item => new ScreenChangedTileRow(item.row, $"0x{item.mask:X8}"))
            .ToArray();
        var changedTiles = tileRows.Sum(mask => BitOperations.PopCount(mask));
        var bounds = changedPixels == 0
            ? null
            : new ScreenChangeBounds(minX, minY, maxX - minX + 1, maxY - minY + 1);

        return new ScreenFrameObservation(
            frameOffset,
            totalFrame,
            Hash(current),
            changedPixels,
            changedTiles,
            bounds,
            changedTileRows);
    }

    private static string Hash(ReadOnlySpan<byte> values) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(values)).ToLowerInvariant()}";
}
