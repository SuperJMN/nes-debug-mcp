using System;
using System.Collections.Generic;
using System.IO;
using Nes.Debug.Core;

namespace Nes.Debug.Emulator;

public sealed class AutoNesDebugSession(INesDebugSession adnes, INesDebugSession aprnes) : INesDebugSession, IDisposable
{
    private INesDebugSession? active;

    private INesDebugSession Active => active ?? adnes;

    public DebugResult<LoadRomResult> LoadRom(string path)
    {
        if (!File.Exists(path))
        {
            return DebugResult<LoadRomResult>.Failure("rom_not_found", $"ROM was not found: {path}");
        }

        var bytes = File.ReadAllBytes(path);
        var header = ParseHeader(bytes);
        if (!header.IsSuccess)
        {
            return DebugResult<LoadRomResult>.Failure(header.Error!.Code, header.Error.Message);
        }

        active = IsAdnesSupportedMapper(header.Value.Mapper) ? adnes : aprnes;
        return active.LoadRom(path);
    }

    public DebugResult<SaveStateResult> SaveState(string path) => Active.SaveState(path);

    public DebugResult<LoadStateResult> LoadState(string path) => Active.LoadState(path);

    public DebugResult<ResetResult> Reset() => Active.Reset();

    public DebugResult<StepInstructionResult> StepInstruction(int count) => Active.StepInstruction(count);

    public DebugResult<RunFrameResult> RunFrame(int count) => Active.RunFrame(count);

    public DebugResult<ControllerStateResult> SetController(IReadOnlyList<NesButton> pressedButtons) =>
        Active.SetController(pressedButtons);

    public DebugResult<PressButtonsResult> PressButtons(IReadOnlyList<NesButton> pressedButtons, int frameCount) =>
        Active.PressButtons(pressedButtons, frameCount);

    public DebugResult<ContinueResult> ContinueUntilBreak(int maxInstructions) => Active.ContinueUntilBreak(maxInstructions);

    public DebugResult<ContinueResult> StepOver(int maxInstructions) => Active.StepOver(maxInstructions);

    public DebugResult<ContinueResult> StepOut(int maxInstructions) => Active.StepOut(maxInstructions);

    public DebugResult<RunUntilConditionResult> RunUntilCondition(string condition, int maxInstructions, int maxFrames) =>
        Active.RunUntilCondition(condition, maxInstructions, maxFrames);

    public DebugResult<BreakpointSetResult> SetBreakpoint(ushort address, string? condition) =>
        Active.SetBreakpoint(address, condition);

    public DebugResult<ClearBreakpointResult> ClearBreakpoint(string breakpointId) => Active.ClearBreakpoint(breakpointId);

    public DebugResult<ListBreakpointsResult> ListBreakpoints() => Active.ListBreakpoints();

    public DebugResult<WatchpointSetResult> SetWatchpoint(ushort address, WatchpointMode mode) =>
        Active.SetWatchpoint(address, mode);

    public DebugResult<WatchpointSetResult> SetWatchpointRange(ushort address, int length, WatchpointMode mode) =>
        Active.SetWatchpointRange(address, length, mode);

    public DebugResult<ClearWatchpointResult> ClearWatchpoint(string watchpointId) => Active.ClearWatchpoint(watchpointId);

    public DebugResult<ListWatchpointsResult> ListWatchpoints() => Active.ListWatchpoints();

    public DebugResult<SessionStateResult> GetState() => Active.GetState();

    public DebugResult<NesCpuRegisters> ReadRegisters() => Active.ReadRegisters();

    public DebugResult<MemoryReadResult> ReadMemory(ushort address, int length) => Active.ReadMemory(address, length);

    public DebugResult<WriteMemoryResult> WriteMemory(ushort address, IReadOnlyList<byte> bytes) =>
        Active.WriteMemory(address, bytes);

    public DebugResult<DisassembleResult> Disassemble(ushort address, int instructionCount) =>
        Active.Disassemble(address, instructionCount);

    public DebugResult<LoadSymbolsResult> LoadSymbols(string path) => Active.LoadSymbols(path);

    public DebugResult<ResolveSymbolResult> ResolveSymbol(string name) => Active.ResolveSymbol(name);

    public DebugResult<ReadSymbolResult> ReadSymbol(string name, int? length) => Active.ReadSymbol(name, length);

    public DebugResult<OamDumpResult> ReadOam() => Active.ReadOam();

    public DebugResult<PpuStateResult> ReadPpuState() => Active.ReadPpuState();

    public DebugResult<ScreenCaptureResult> CaptureScreen() => Active.CaptureScreen();

    public DebugResult<LastWriterResult> FindLastWriter(ushort address) => Active.FindLastWriter(address);

    public DebugResult<LastWritersResult> FindLastWriters(ushort address, int length) =>
        Active.FindLastWriters(address, length);

    public DebugResult<TraceUntilWriteResult> TraceUntilWrite(ushort address, int maxInstructions) =>
        Active.TraceUntilWrite(address, maxInstructions);

    public DebugResult<TraceUntilWriteRangeResult> TraceUntilWriteRange(ushort address, int length, int maxInstructions) =>
        Active.TraceUntilWriteRange(address, length, maxInstructions);

    public DebugResult<ScreenRegionResult> ReadScreenRegion(int x, int y, int width, int height, string format) =>
        Active.ReadScreenRegion(x, y, width, height, format);

    public DebugResult<InputTimelineResult> RunInputTimeline(IReadOnlyList<InputTimelineStep> steps) =>
        Active.RunInputTimeline(steps);

    public DebugResult<TilemapDumpResult> DumpTilemap(ushort address) => Active.DumpTilemap(address);

    public DebugResult<TilesetDumpResult> DumpTileset(ushort address, int tileCount) =>
        Active.DumpTileset(address, tileCount);

    public void Dispose()
    {
        (adnes as IDisposable)?.Dispose();
        (aprnes as IDisposable)?.Dispose();
    }

    private static bool IsAdnesSupportedMapper(int mapper) => mapper is 0 or 1 or 2 or 3;

    private static DebugResult<NesRomHeader> ParseHeader(byte[] bytes)
    {
        if (bytes.Length < 16 || bytes[0] != 'N' || bytes[1] != 'E' || bytes[2] != 'S' || bytes[3] != 0x1A)
        {
            return DebugResult<NesRomHeader>.Failure("invalid_ines", "ROM does not have a valid iNES header.");
        }

        var flags6 = bytes[6];
        var flags7 = bytes[7];
        var mapper = ((flags7 & 0xF0) | (flags6 >> 4)) & 0xFF;
        return DebugResult<NesRomHeader>.Success(new NesRomHeader(mapper));
    }

    private sealed record NesRomHeader(int Mapper);
}
