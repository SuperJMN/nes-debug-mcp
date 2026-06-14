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

    public DebugResult<TraceUntilWriteResult> TraceUntilWrite(ushort address, int maxInstructions)
    {
        lock (gate) { return inner.TraceUntilWrite(address, maxInstructions); }
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
