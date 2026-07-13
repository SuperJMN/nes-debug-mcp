namespace Nes.Debug.Core;

public sealed class SynchronizedNesDebugSession(INesDebugSession inner) : INesDebugSession, IDisposable
{
    private readonly object gate = new();

    public DebugResult<LoadRomResult> LoadRom(string path)
    {
        lock (gate) { return inner.LoadRom(path); }
    }

    public DebugResult<SaveStateResult> SaveState(string path)
    {
        lock (gate) { return inner.SaveState(path); }
    }

    public DebugResult<LoadStateResult> LoadState(string path)
    {
        lock (gate) { return inner.LoadState(path); }
    }

    public DebugResult<ResetResult> Reset()
    {
        lock (gate) { return inner.Reset(); }
    }

    public DebugResult<StepInstructionResult> StepInstruction(int count)
    {
        lock (gate) { return inner.StepInstruction(count); }
    }

    public DebugResult<RunFrameResult> RunFrame(int count)
    {
        lock (gate) { return inner.RunFrame(count); }
    }

    public DebugResult<ControllerStateResult> SetController(IReadOnlyList<NesButton> pressedButtons)
    {
        lock (gate) { return inner.SetController(pressedButtons); }
    }

    public DebugResult<PressButtonsResult> PressButtons(IReadOnlyList<NesButton> pressedButtons, int frameCount)
    {
        lock (gate) { return inner.PressButtons(pressedButtons, frameCount); }
    }

    public DebugResult<ContinueResult> ContinueUntilBreak(int maxInstructions)
    {
        lock (gate) { return inner.ContinueUntilBreak(maxInstructions); }
    }

    public DebugResult<ContinueResult> StepOver(int maxInstructions)
    {
        lock (gate) { return inner.StepOver(maxInstructions); }
    }

    public DebugResult<ContinueResult> StepOut(int maxInstructions)
    {
        lock (gate) { return inner.StepOut(maxInstructions); }
    }

    public DebugResult<RunUntilConditionResult> RunUntilCondition(string condition, int maxInstructions, int maxFrames)
    {
        lock (gate) { return inner.RunUntilCondition(condition, maxInstructions, maxFrames); }
    }

    public DebugResult<BreakpointSetResult> SetBreakpoint(ushort address, string? condition)
    {
        lock (gate) { return inner.SetBreakpoint(address, condition); }
    }

    public DebugResult<ClearBreakpointResult> ClearBreakpoint(string breakpointId)
    {
        lock (gate) { return inner.ClearBreakpoint(breakpointId); }
    }

    public DebugResult<ListBreakpointsResult> ListBreakpoints()
    {
        lock (gate) { return inner.ListBreakpoints(); }
    }

    public DebugResult<WatchpointSetResult> SetWatchpoint(ushort address, WatchpointMode mode)
    {
        lock (gate) { return inner.SetWatchpoint(address, mode); }
    }

    public DebugResult<WatchpointSetResult> SetWatchpointRange(ushort address, int length, WatchpointMode mode)
    {
        lock (gate) { return inner.SetWatchpointRange(address, length, mode); }
    }

    public DebugResult<ClearWatchpointResult> ClearWatchpoint(string watchpointId)
    {
        lock (gate) { return inner.ClearWatchpoint(watchpointId); }
    }

    public DebugResult<ListWatchpointsResult> ListWatchpoints()
    {
        lock (gate) { return inner.ListWatchpoints(); }
    }

    public DebugResult<SessionStateResult> GetState()
    {
        lock (gate) { return inner.GetState(); }
    }

    public DebugResult<NesCpuRegisters> ReadRegisters()
    {
        lock (gate) { return inner.ReadRegisters(); }
    }

    public DebugResult<MemoryReadResult> ReadMemory(ushort address, int length)
    {
        lock (gate) { return inner.ReadMemory(address, length); }
    }

    public DebugResult<WriteMemoryResult> WriteMemory(ushort address, IReadOnlyList<byte> bytes)
    {
        lock (gate) { return inner.WriteMemory(address, bytes); }
    }

    public DebugResult<DisassembleResult> Disassemble(ushort address, int instructionCount)
    {
        lock (gate) { return inner.Disassemble(address, instructionCount); }
    }

    public DebugResult<LoadSymbolsResult> LoadSymbols(string path)
    {
        lock (gate) { return inner.LoadSymbols(path); }
    }

    public DebugResult<ResolveSymbolResult> ResolveSymbol(string name)
    {
        lock (gate) { return inner.ResolveSymbol(name); }
    }

    public DebugResult<ReadSymbolResult> ReadSymbol(string name, int? length)
    {
        lock (gate) { return inner.ReadSymbol(name, length); }
    }

    public DebugResult<OamDumpResult> ReadOam()
    {
        lock (gate) { return inner.ReadOam(); }
    }

    public DebugResult<PpuStateResult> ReadPpuState()
    {
        lock (gate) { return inner.ReadPpuState(); }
    }

    public DebugResult<ScreenCaptureResult> CaptureScreen()
    {
        lock (gate) { return inner.CaptureScreen(); }
    }

    public DebugResult<LastWriterResult> FindLastWriter(ushort address)
    {
        lock (gate) { return inner.FindLastWriter(address); }
    }

    public DebugResult<LastWritersResult> FindLastWriters(ushort address, int length)
    {
        lock (gate) { return inner.FindLastWriters(address, length); }
    }

    public DebugResult<TraceUntilWriteResult> TraceUntilWrite(ushort address, int maxInstructions)
    {
        lock (gate) { return inner.TraceUntilWrite(address, maxInstructions); }
    }

    public DebugResult<TraceUntilWriteRangeResult> TraceUntilWriteRange(ushort address, int length, int maxInstructions)
    {
        lock (gate) { return inner.TraceUntilWriteRange(address, length, maxInstructions); }
    }

    public DebugResult<PpuRegisterTraceResult> TracePpuRegisterWrites(PpuRegisterTraceRequest request)
    {
        lock (gate) { return inner.TracePpuRegisterWrites(request); }
    }

    public DebugResult<ScreenRegionResult> ReadScreenRegion(int x, int y, int width, int height, string format)
    {
        lock (gate) { return inner.ReadScreenRegion(x, y, width, height, format); }
    }

    public DebugResult<ScreenObservationResult> ObserveScreen(int frameCount)
    {
        lock (gate) { return inner.ObserveScreen(frameCount); }
    }

    public DebugResult<ExecutionObservationResult> ObserveExecution(ExecutionObservationRequest request)
    {
        lock (gate) { return inner.ObserveExecution(request); }
    }

    public DebugResult<InputTimelineResult> RunInputTimeline(IReadOnlyList<InputTimelineStep> steps)
    {
        lock (gate) { return inner.RunInputTimeline(steps); }
    }

    public DebugResult<NametableDumpResult> DumpNametables(bool includeDetails)
    {
        lock (gate) { return inner.DumpNametables(includeDetails); }
    }

    public DebugResult<TilemapDumpResult> DumpTilemap(ushort address)
    {
        lock (gate) { return inner.DumpTilemap(address); }
    }

    public DebugResult<TilesetDumpResult> DumpTileset(ushort address, int tileCount)
    {
        lock (gate) { return inner.DumpTileset(address, tileCount); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            (inner as IDisposable)?.Dispose();
        }
    }
}
