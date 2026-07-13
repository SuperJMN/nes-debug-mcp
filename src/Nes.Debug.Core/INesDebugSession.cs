namespace Nes.Debug.Core;

public interface INesDebugSession
{
    DebugResult<LoadRomResult> LoadRom(string path);

    DebugResult<SaveStateResult> SaveState(string path);

    DebugResult<LoadStateResult> LoadState(string path);

    DebugResult<ResetResult> Reset();

    DebugResult<StepInstructionResult> StepInstruction(int count);

    DebugResult<RunFrameResult> RunFrame(int count);

    DebugResult<ControllerStateResult> SetController(IReadOnlyList<NesButton> pressedButtons);

    DebugResult<PressButtonsResult> PressButtons(IReadOnlyList<NesButton> pressedButtons, int frameCount);

    DebugResult<ContinueResult> ContinueUntilBreak(int maxInstructions);

    DebugResult<ContinueResult> StepOver(int maxInstructions);

    DebugResult<ContinueResult> StepOut(int maxInstructions);

    DebugResult<RunUntilConditionResult> RunUntilCondition(string condition, int maxInstructions, int maxFrames);

    DebugResult<BreakpointSetResult> SetBreakpoint(ushort address, string? condition);

    DebugResult<ClearBreakpointResult> ClearBreakpoint(string breakpointId);

    DebugResult<ListBreakpointsResult> ListBreakpoints();

    DebugResult<WatchpointSetResult> SetWatchpoint(ushort address, WatchpointMode mode);

    DebugResult<WatchpointSetResult> SetWatchpointRange(ushort address, int length, WatchpointMode mode);

    DebugResult<ClearWatchpointResult> ClearWatchpoint(string watchpointId);

    DebugResult<ListWatchpointsResult> ListWatchpoints();

    DebugResult<SessionStateResult> GetState();

    DebugResult<NesCpuRegisters> ReadRegisters();

    DebugResult<MemoryReadResult> ReadMemory(ushort address, int length);

    DebugResult<WriteMemoryResult> WriteMemory(ushort address, IReadOnlyList<byte> bytes);

    DebugResult<DisassembleResult> Disassemble(ushort address, int instructionCount);

    DebugResult<LoadSymbolsResult> LoadSymbols(string path);

    DebugResult<ResolveSymbolResult> ResolveSymbol(string name);

    DebugResult<ReadSymbolResult> ReadSymbol(string name, int? length);

    DebugResult<OamDumpResult> ReadOam();

    DebugResult<PpuStateResult> ReadPpuState();

    DebugResult<ScreenCaptureResult> CaptureScreen();

    DebugResult<LastWriterResult> FindLastWriter(ushort address);

    DebugResult<LastWritersResult> FindLastWriters(ushort address, int length);

    DebugResult<TraceUntilWriteResult> TraceUntilWrite(ushort address, int maxInstructions);

    DebugResult<TraceUntilWriteRangeResult> TraceUntilWriteRange(ushort address, int length, int maxInstructions);

    DebugResult<ScreenRegionResult> ReadScreenRegion(int x, int y, int width, int height, string format);

    DebugResult<ScreenObservationResult> ObserveScreen(int frameCount);

    DebugResult<InputTimelineResult> RunInputTimeline(IReadOnlyList<InputTimelineStep> steps);

    DebugResult<NametableDumpResult> DumpNametables(bool includeDetails);

    DebugResult<TilemapDumpResult> DumpTilemap(ushort address);

    DebugResult<TilesetDumpResult> DumpTileset(ushort address, int tileCount);
}
