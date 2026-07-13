namespace Nes.Debug.Core;

public static class ScreenObserver
{
    public const int MaxFrames = 600;

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

        var previous = new byte[ScreenFrameAnalyzer.PixelCount];
        var current = new byte[ScreenFrameAnalyzer.PixelCount];
        var initial = ScreenFrameAnalyzer.Capture(frameSource, previous);
        if (!initial.IsSuccess)
        {
            return DebugResult<ScreenObservationResult>.Failure(initial.Error!.Code, initial.Error.Message);
        }

        var initialHash = ScreenFrameAnalyzer.Hash(previous);
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

            var captured = ScreenFrameAnalyzer.Capture(frameSource, current);
            if (!captured.IsSuccess)
            {
                return DebugResult<ScreenObservationResult>.Failure(captured.Error!.Code, captured.Error.Message);
            }

            samples.Add(ScreenFrameAnalyzer.Compare(previous, current, frameOffset, timeline.Frames));
            (previous, current) = (current, previous);

            if (hitBreakpoint)
            {
                break;
            }
        }

        return DebugResult<ScreenObservationResult>.Success(
            new ScreenObservationResult(frameCount, framesRun, initialHash, samples, hitBreakpoint, timeline));
    }

}
