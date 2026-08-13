using System.Text.Json;

namespace Nes.Corpus.Qualification;

internal sealed record SmokeResult(
    bool Passed,
    FailureCategory? FailureCategory,
    string? BackendVersion,
    string? ServerVersion)
{
    public static SmokeResult Failure(FailureCategory category) => new(false, category, null, null);

    public static SmokeResult Success(string backendVersion, string serverVersion) =>
        new(true, null, backendVersion, serverVersion);
}

internal static class McpQualificationSmoke
{
    private const int AdnesMaximumInstructionsPerFrame = 1_000_000;

    private static readonly string[] PpuRegisters =
    [
        "PPUCTRL",
        "PPUMASK",
        "PPUSTATUS",
        "OAMADDR",
        "OAMDATA",
        "PPUSCROLL",
        "PPUADDR",
        "PPUDATA",
    ];

    public static async Task<SmokeResult> RunAsync(
        string serverAssembly,
        string romPath,
        string statePath,
        int headerMapper,
        QualificationLaunchMode launchMode,
        QualificationBounds bounds,
        CancellationToken cancellationToken)
    {
        await using var client = McpStdioClient.Start(serverAssembly, launchMode);
        if (client is null)
        {
            return SmokeResult.Failure(FailureCategory.WorkerCrash);
        }

        var expectedBackend = launchMode switch
        {
            QualificationLaunchMode.PrimaryDefault => QualificationBackend.AprNes,
            QualificationLaunchMode.Adnes => QualificationBackend.Adnes,
            _ => throw new ArgumentOutOfRangeException(nameof(launchMode)),
        };
        var result = await RunCoreAsync(
            client,
            romPath,
            statePath,
            headerMapper,
            expectedBackend,
            bounds,
            cancellationToken).ConfigureAwait(false);

        var stopped = await client.StopAsync(cancellationToken).ConfigureAwait(false);
        var cleanup = TryDelete(statePath);
        if (!cleanup)
        {
            return SmokeResult.Failure(FailureCategory.TempCleanup);
        }

        return !result.Passed || stopped ? result : SmokeResult.Failure(FailureCategory.McpShutdown);
    }

    private static async Task<SmokeResult> RunCoreAsync(
        McpStdioClient client,
        string romPath,
        string statePath,
        int headerMapper,
        QualificationBackend backend,
        QualificationBounds bounds,
        CancellationToken cancellationToken)
    {
        if (!await client.InitializeAsync(cancellationToken).ConfigureAwait(false))
        {
            return SmokeResult.Failure(FailureCategory.ProtocolViolation);
        }

        var load = await client.CallJsonAsync("load_rom", new { path = romPath }, cancellationToken).ConfigureAwait(false);
        if (!load.IsSuccess || !IsTrue(load.Payload, "loaded") || !IsInt32(load.Payload, "mapper", headerMapper))
        {
            return SmokeResult.Failure(FailureCategory.Load);
        }

        var identity = await ReadIdentityAsync(client, headerMapper, backend, cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            return SmokeResult.Failure(FailureCategory.Identity);
        }

        var reset = await client.CallJsonAsync("reset", new { }, cancellationToken).ConfigureAwait(false);
        if (!reset.IsSuccess || !IsTrue(reset.Payload, "reset"))
        {
            return SmokeResult.Failure(FailureCategory.Reset);
        }

        BehaviorSnapshot? adnesBefore = null;
        if (backend == QualificationBackend.Adnes)
        {
            adnesBefore = await CaptureBehaviorSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
            if (adnesBefore is null)
            {
                return SmokeResult.Failure(FailureCategory.IndependentSmoke);
            }
        }

        var stepCount = Math.Min(1, bounds.MaxInstructions);
        var step = await client.CallJsonAsync("step_instruction", new { count = stepCount }, cancellationToken).ConfigureAwait(false);
        if (!step.IsSuccess || !TryGetInt32(step.Payload, "instructionsRun", out var instructionsRun) ||
            instructionsRun is < 1 || instructionsRun > stepCount)
        {
            return SmokeResult.Failure(FailureCategory.InstructionExecution);
        }

        BehaviorSnapshot? adnesAfterStep = null;
        if (backend == QualificationBackend.Adnes)
        {
            adnesAfterStep = await CaptureBehaviorSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
            if (adnesAfterStep is null ||
                !HasBoundedInstructionProgress(adnesBefore!, adnesAfterStep, (ulong)stepCount))
            {
                return SmokeResult.Failure(FailureCategory.InstructionExecution);
            }
        }

        var frameCount = Math.Min(1, bounds.MaxFrames);
        var frame = await client.CallJsonAsync("run_frame", new { count = frameCount }, cancellationToken).ConfigureAwait(false);
        if (!frame.IsSuccess || !IsInt32(frame.Payload, "framesRun", frameCount) ||
            backend == QualificationBackend.Adnes &&
            (!TryReadTimeline(frame.Payload, out var adnesFrameTimeline) ||
             !HasBoundedFrameProgress(adnesAfterStep!.Timeline, adnesFrameTimeline, frameCount)))
        {
            return SmokeResult.Failure(FailureCategory.FrameExecution);
        }

        if (backend == QualificationBackend.Adnes)
        {
            var held = await client.CallJsonAsync(
                "set_controller",
                new { buttons = new[] { "a" } },
                cancellationToken).ConfigureAwait(false);
            if (!held.IsSuccess || !IsControllerState(held.Payload, aPressed: true))
            {
                return SmokeResult.Failure(FailureCategory.ControllerInput);
            }
        }

        var input = await client.CallJsonAsync(
            "press_buttons",
            new { buttons = new[] { "a" }, frameCount },
            cancellationToken).ConfigureAwait(false);
        if (!input.IsSuccess || !IsInt32(input.Payload, "framesRun", frameCount) ||
            backend == QualificationBackend.Adnes &&
            (!input.Payload.TryGetProperty("released", out var released) ||
             !IsControllerState(released, aPressed: false)))
        {
            return SmokeResult.Failure(FailureCategory.ControllerInput);
        }

        if (backend == QualificationBackend.AprNes)
        {
            var instructionBoundFailure = await ExerciseInstructionBoundAsync(
                client,
                bounds.MaxInstructions,
                frameCount,
                cancellationToken).ConfigureAwait(false);
            if (instructionBoundFailure.HasValue)
            {
                return SmokeResult.Failure(instructionBoundFailure.Value);
            }
        }

        var registers = await client.CallJsonAsync("read_registers", new { }, cancellationToken).ConfigureAwait(false);
        if (!registers.IsSuccess || !IsRegisterPayload(registers.Payload))
        {
            return SmokeResult.Failure(FailureCategory.CpuInspection);
        }

        var ppu = await client.CallJsonAsync("read_ppu_state", new { }, cancellationToken).ConfigureAwait(false);
        if (!ppu.IsSuccess || !IsPpuPayload(ppu.Payload))
        {
            return SmokeResult.Failure(FailureCategory.PpuInspection);
        }

        var capture = await client.CallImageAsync("capture_screen", new { }, cancellationToken).ConfigureAwait(false);
        if (!capture.IsSuccess)
        {
            return SmokeResult.Failure(FailureCategory.ScreenCapture);
        }

        if (backend == QualificationBackend.Adnes)
        {
            var finalState = await client.CallJsonAsync("get_state", new { }, cancellationToken).ConfigureAwait(false);
            if (!finalState.IsSuccess ||
                !TryReadTimeline(finalState.Payload, out var finalTimeline) ||
                !TryReadTimeline(frame.Payload, out var frameTimeline) ||
                !HasBoundedFrameProgress(frameTimeline, finalTimeline, frameCount) ||
                !TryGetUInt64(registers.Payload, "cycles", out var finalCpuCycles) ||
                !TryGetUInt64(ppu.Payload, "ppuCycles", out var finalPpuCycles) ||
                finalCpuCycles <= adnesAfterStep!.CpuCycles ||
                finalPpuCycles <= adnesAfterStep.PpuCycles)
            {
                return SmokeResult.Failure(FailureCategory.IndependentSmoke);
            }
        }

        if (backend == QualificationBackend.AprNes)
        {
            var traceFailure = await ExerciseTraceAsync(client, frameCount, bounds.MaxTraceEvents, cancellationToken).ConfigureAwait(false);
            if (traceFailure.HasValue)
            {
                return SmokeResult.Failure(traceFailure.Value);
            }
        }

        if (backend == QualificationBackend.AprNes)
        {
            var replayFailure = await ExerciseReplayAsync(client, statePath, frameCount, cancellationToken).ConfigureAwait(false);
            if (replayFailure.HasValue)
            {
                return SmokeResult.Failure(replayFailure.Value);
            }
        }

        return SmokeResult.Success(identity.BackendVersion, identity.ServerVersion);
    }

    private static async Task<IdentitySnapshot?> ReadIdentityAsync(
        McpStdioClient client,
        int headerMapper,
        QualificationBackend backend,
        CancellationToken cancellationToken)
    {
        var state = await client.CallJsonAsync("get_state", new { }, cancellationToken).ConfigureAwait(false);
        if (!state.IsSuccess ||
            !IsTrue(state.Payload, "romLoaded") ||
            !IsInt32(state.Payload, "mapper", headerMapper) ||
            !TryGetString(state.Payload, "backend", out var backendName) ||
            backendName != (backend == QualificationBackend.AprNes ? "AprNes" : "ADNES") ||
            !TryGetString(state.Payload, "backendVersion", out var backendVersion) ||
            !TryGetString(state.Payload, "serverVersion", out var serverVersion) ||
            !WorkerProtocol.IsSafeVersion(backendVersion) ||
            !WorkerProtocol.IsSafeVersion(serverVersion) ||
            !TryReadTimeline(state.Payload, out _))
        {
            return null;
        }

        int? debugCycleLimit = null;
        if (backend == QualificationBackend.AprNes)
        {
            if (!TryGetInt32(state.Payload, "debugCycleLimit", out var limit) || limit < 1)
            {
                return null;
            }

            debugCycleLimit = limit;
        }

        // Deliberately do not read or clone title or any other identity field.
        return new IdentitySnapshot(backendVersion, serverVersion, debugCycleLimit);
    }

    private static async Task<FailureCategory?> ExerciseInstructionBoundAsync(
        McpStdioClient client,
        int maximumInstructions,
        int maximumFrames,
        CancellationToken cancellationToken)
    {
        var before = await client.CallJsonAsync("get_state", new { }, cancellationToken).ConfigureAwait(false);
        if (!before.IsSuccess || !TryReadTimeline(before.Payload, out var beforeTimeline))
        {
            return FailureCategory.InstructionExecution;
        }

        var bounded = await client.CallJsonAsync(
            "run_until_condition",
            new
            {
                condition = "A == 0x100",
                maxInstructions = maximumInstructions,
                maxFrames = maximumFrames,
            },
            cancellationToken).ConfigureAwait(false);
        if (!bounded.IsSuccess ||
            !TryGetUInt32(bounded.Payload, "instructionsRun", out var instructionsRun) ||
            instructionsRun > maximumInstructions ||
            !TryGetUInt64(bounded.Payload, "framesRun", out var framesRun) ||
            framesRun > (ulong)maximumFrames ||
            !TryGetString(bounded.Payload, "reason", out var reason) ||
            reason is not ("maxInstructions" or "maxFrames") ||
            !TryReadTimeline(bounded.Payload, out var afterTimeline) ||
            afterTimeline.Instructions < beforeTimeline.Instructions ||
            afterTimeline.Instructions - beforeTimeline.Instructions != instructionsRun ||
            afterTimeline.Instructions - beforeTimeline.Instructions > (ulong)maximumInstructions)
        {
            return FailureCategory.InstructionExecution;
        }

        return null;
    }

    private static async Task<FailureCategory?> ExerciseTraceAsync(
        McpStdioClient client,
        int frameCount,
        int maximumEvents,
        CancellationToken cancellationToken)
    {
        var trace = await client.CallJsonAsync(
            "trace_ppu_register_writes",
            new
            {
                frameCount,
                maxEvents = maximumEvents,
                registers = PpuRegisters,
                buttons = Array.Empty<string>(),
            },
            cancellationToken).ConfigureAwait(false);
        if (!trace.IsSuccess)
        {
            return trace.Failure switch
            {
                McpCallFailure.ToolError => FailureCategory.TraceBackendError,
                McpCallFailure.ResponseOverflow => FailureCategory.TraceResponseBounds,
                McpCallFailure.InvalidContent => FailureCategory.McpResponseContent,
                McpCallFailure.RequestWrite => FailureCategory.McpRequestWrite,
                McpCallFailure.ServerCrash => FailureCategory.McpServerCrash,
                McpCallFailure.ResponseEof => FailureCategory.McpResponseEof,
                McpCallFailure.InvalidJson => FailureCategory.McpResponseInvalidJson,
                McpCallFailure.UnexpectedId => FailureCategory.McpResponseUnexpectedId,
                _ => FailureCategory.Trace,
            };
        }

        if (!IsInt32(trace.Payload, "framesRequested", frameCount) ||
            !IsInt32(trace.Payload, "framesRun", frameCount) ||
            !TryGetString(trace.Payload, "stopReason", out var stopReason) ||
            stopReason != "framesComplete")
        {
            return FailureCategory.TraceStop;
        }

        if (!TryGetInt32(trace.Payload, "eventCount", out var eventCount) ||
            !TryGetInt32(trace.Payload, "eventsObserved", out var eventsObserved) ||
            !TryGetBoolean(trace.Payload, "truncated", out var truncated) ||
            eventCount is < 0 || eventCount > maximumEvents ||
            eventsObserved < eventCount ||
            !trace.Payload.TryGetProperty("events", out var events) ||
            events.ValueKind != JsonValueKind.Array ||
            events.GetArrayLength() != eventCount ||
            truncated && (eventCount != maximumEvents || eventsObserved < eventCount) ||
            !truncated && eventsObserved != eventCount)
        {
            return FailureCategory.TraceEventBounds;
        }

        if (!trace.Payload.TryGetProperty("initialPpuState", out var initialPpuState) ||
            initialPpuState.ValueKind != JsonValueKind.Object ||
            !trace.Payload.TryGetProperty("timeline", out var traceTimeline) ||
            traceTimeline.ValueKind != JsonValueKind.Object ||
            !TryReadTimeline(initialPpuState, out var initialTimeline) ||
            !TryReadTimelineValue(traceTimeline, out var finalTimeline) ||
            finalTimeline.Instructions < initialTimeline.Instructions ||
            finalTimeline.Instructions - initialTimeline.Instructions >
                (ulong)frameCount * PpuRegisterTracingMaxInstructionsPerFrame)
        {
            return FailureCategory.TraceInstructionBounds;
        }

        foreach (var item in events.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryGetInt32(item, "frameOffset", out var frameOffset) ||
                frameOffset < 0 || frameOffset > frameCount ||
                !item.TryGetProperty("cpuCycle", out var cpuCycle) || cpuCycle.ValueKind != JsonValueKind.Number ||
                !item.TryGetProperty("instructionCounter", out var instructionCounter) || instructionCounter.ValueKind != JsonValueKind.Number ||
                !TryGetString(item, "address", out _))
            {
                return FailureCategory.TraceEventBounds;
            }
        }

        return null;
    }

    private const int PpuRegisterTracingMaxInstructionsPerFrame = 100_000;

    private static async Task<FailureCategory?> ExerciseReplayAsync(
        McpStdioClient client,
        string statePath,
        int frameCount,
        CancellationToken cancellationToken)
    {
        var save = await client.CallJsonAsync("save_state", new { path = statePath }, cancellationToken).ConfigureAwait(false);
        if (!save.IsSuccess || !IsTrue(save.Payload, "saved"))
        {
            return FailureCategory.SaveStateReplay;
        }

        var saved = await CaptureSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (saved is null)
        {
            return FailureCategory.SaveStateReplay;
        }

        if (!await RunReplayFrameAsync(client, frameCount, cancellationToken).ConfigureAwait(false))
        {
            return FailureCategory.SaveStateReplay;
        }

        var firstFuture = await CaptureSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (firstFuture is null)
        {
            return FailureCategory.SaveStateReplay;
        }

        var load = await client.CallJsonAsync("load_state", new { path = statePath }, cancellationToken).ConfigureAwait(false);
        if (!load.IsSuccess || !IsTrue(load.Payload, "loaded"))
        {
            return FailureCategory.SaveStateReplay;
        }

        var restored = await CaptureSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (restored is null || !saved.SemanticallyEquals(restored))
        {
            return FailureCategory.SaveStateReplay;
        }

        if (!await RunReplayFrameAsync(client, frameCount, cancellationToken).ConfigureAwait(false))
        {
            return FailureCategory.SaveStateReplay;
        }

        var replayedFuture = await CaptureSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        return replayedFuture is not null && firstFuture.SemanticallyEquals(replayedFuture)
            ? null
            : FailureCategory.SaveStateReplay;
    }

    private static async Task<bool> RunReplayFrameAsync(
        McpStdioClient client,
        int frameCount,
        CancellationToken cancellationToken)
    {
        var frame = await client.CallJsonAsync("run_frame", new { count = frameCount }, cancellationToken).ConfigureAwait(false);
        return frame.IsSuccess && IsInt32(frame.Payload, "framesRun", frameCount);
    }

    private static async Task<SemanticSnapshot?> CaptureSnapshotAsync(
        McpStdioClient client,
        CancellationToken cancellationToken)
    {
        var registers = await client.CallJsonAsync("read_registers", new { }, cancellationToken).ConfigureAwait(false);
        var ppu = await client.CallJsonAsync("read_ppu_state", new { }, cancellationToken).ConfigureAwait(false);
        var state = await client.CallJsonAsync("get_state", new { }, cancellationToken).ConfigureAwait(false);
        var capture = await client.CallImageAsync("capture_screen", new { }, cancellationToken).ConfigureAwait(false);
        if (!registers.IsSuccess || !IsRegisterPayload(registers.Payload) ||
            !ppu.IsSuccess || !IsPpuPayload(ppu.Payload) ||
            !state.IsSuccess || !TryReadTimeline(state.Payload, out var timeline) ||
            !capture.IsSuccess)
        {
            return null;
        }

        // Only the selected CPU/PPU payloads and timeline are retained; get_state title is never read.
        return new SemanticSnapshot(
            registers.Payload.GetRawText(),
            ppu.Payload.GetRawText(),
            timeline,
            capture.Data);
    }

    private static async Task<BehaviorSnapshot?> CaptureBehaviorSnapshotAsync(
        McpStdioClient client,
        CancellationToken cancellationToken)
    {
        var state = await client.CallJsonAsync("get_state", new { }, cancellationToken).ConfigureAwait(false);
        var registers = await client.CallJsonAsync("read_registers", new { }, cancellationToken).ConfigureAwait(false);
        var ppu = await client.CallJsonAsync("read_ppu_state", new { }, cancellationToken).ConfigureAwait(false);
        if (!state.IsSuccess || !TryReadTimeline(state.Payload, out var timeline) ||
            !registers.IsSuccess || !IsRegisterPayload(registers.Payload) ||
            !TryGetUInt64(registers.Payload, "cycles", out var cpuCycles) ||
            !ppu.IsSuccess || !IsPpuPayload(ppu.Payload) ||
            !TryGetUInt64(ppu.Payload, "ppuCycles", out var ppuCycles))
        {
            return null;
        }

        return new BehaviorSnapshot(timeline, cpuCycles, ppuCycles);
    }

    private static bool HasBoundedInstructionProgress(
        BehaviorSnapshot before,
        BehaviorSnapshot after,
        ulong expectedInstructions) =>
        after.Timeline.Instructions >= before.Timeline.Instructions &&
        after.Timeline.Instructions - before.Timeline.Instructions == expectedInstructions &&
        after.Timeline.Frames >= before.Timeline.Frames &&
        after.Timeline.Frames - before.Timeline.Frames <= 1 &&
        after.Timeline.Cycles > before.Timeline.Cycles &&
        after.CpuCycles > before.CpuCycles &&
        after.PpuCycles > before.PpuCycles;

    private static bool HasBoundedFrameProgress(
        TimelineSnapshot before,
        TimelineSnapshot after,
        int expectedFrames) =>
        after.Frames >= before.Frames &&
        after.Frames - before.Frames == (ulong)expectedFrames &&
        after.Cycles > before.Cycles &&
        after.Instructions > before.Instructions &&
        after.Instructions - before.Instructions <=
            (ulong)expectedFrames * AdnesMaximumInstructionsPerFrame;

    private static bool IsControllerState(JsonElement payload, bool aPressed)
    {
        if (!TryGetBoolean(payload, "a", out var a) || a != aPressed ||
            !payload.TryGetProperty("pressed", out var pressed) ||
            pressed.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var expectedPressedCount = aPressed ? 1 : 0;
        if (pressed.GetArrayLength() != expectedPressedCount ||
            aPressed && (pressed[0].ValueKind != JsonValueKind.String || pressed[0].GetString() != "a"))
        {
            return false;
        }

        foreach (var name in new[] { "b", "select", "start", "up", "down", "left", "right" })
        {
            if (!TryGetBoolean(payload, name, out var value) || value)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRegisterPayload(JsonElement payload) =>
        TryGetString(payload, "a", out _) &&
        TryGetString(payload, "x", out _) &&
        TryGetString(payload, "y", out _) &&
        TryGetString(payload, "sp", out _) &&
        TryGetString(payload, "pc", out _) &&
        TryGetString(payload, "status", out _) &&
        payload.TryGetProperty("cycles", out var cycles) && cycles.ValueKind == JsonValueKind.Number;

    private static bool IsPpuPayload(JsonElement payload) =>
        TryGetString(payload, "ppuctrl", out _) &&
        TryGetString(payload, "ppumask", out _) &&
        TryGetString(payload, "ppustatus", out _) &&
        payload.TryGetProperty("scanline", out var scanline) && scanline.ValueKind == JsonValueKind.Number &&
        payload.TryGetProperty("cycle", out var cycle) && cycle.ValueKind == JsonValueKind.Number;

    private static bool TryReadTimeline(JsonElement payload, out TimelineSnapshot timeline)
    {
        timeline = default;
        if (!payload.TryGetProperty("timeline", out var value) || value.ValueKind != JsonValueKind.Object ||
            !TryGetUInt64(value, "frames", out var frames) ||
            !TryGetUInt64(value, "cycles", out var cycles) ||
            !TryGetUInt64(value, "instructions", out var instructions))
        {
            return false;
        }

        timeline = new TimelineSnapshot(frames, cycles, instructions);
        return true;
    }

    private static bool TryReadTimelineValue(JsonElement value, out TimelineSnapshot timeline)
    {
        timeline = default;
        if (!TryGetUInt64(value, "frames", out var frames) ||
            !TryGetUInt64(value, "cycles", out var cycles) ||
            !TryGetUInt64(value, "instructions", out var instructions))
        {
            return false;
        }

        timeline = new TimelineSnapshot(frames, cycles, instructions);
        return true;
    }

    private static bool IsTrue(JsonElement payload, string propertyName) =>
        payload.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.True;

    private static bool IsInt32(JsonElement payload, string propertyName, int expected) =>
        TryGetInt32(payload, propertyName, out var actual) && actual == expected;

    private static bool TryGetInt32(JsonElement payload, string propertyName, out int value)
    {
        value = 0;
        return payload.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static bool TryGetUInt64(JsonElement payload, string propertyName, out ulong value)
    {
        value = 0;
        return payload.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetUInt64(out value);
    }

    private static bool TryGetUInt32(JsonElement payload, string propertyName, out uint value)
    {
        value = 0;
        return payload.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetUInt32(out value);
    }

    private static bool TryGetString(JsonElement payload, string propertyName, out string value)
    {
        value = "";
        return payload.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               (value = property.GetString() ?? "").Length > 0;
    }

    private static bool TryGetBoolean(JsonElement payload, string propertyName, out bool value)
    {
        value = false;
        if (!payload.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record IdentitySnapshot(string BackendVersion, string ServerVersion, int? DebugCycleLimit);

    private sealed record BehaviorSnapshot(TimelineSnapshot Timeline, ulong CpuCycles, ulong PpuCycles);

    private readonly record struct TimelineSnapshot(ulong Frames, ulong Cycles, ulong Instructions);

    private sealed record SemanticSnapshot(
        string Registers,
        string Ppu,
        TimelineSnapshot Timeline,
        byte[] Frame)
    {
        public bool SemanticallyEquals(SemanticSnapshot other) =>
            Registers == other.Registers &&
            Ppu == other.Ppu &&
            Timeline == other.Timeline &&
            Frame.AsSpan().SequenceEqual(other.Frame);
    }
}
