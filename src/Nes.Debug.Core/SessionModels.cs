using System.Text.Json.Serialization;

namespace Nes.Debug.Core;

public sealed record LoadRomResult(
    [property: JsonPropertyName("loaded")] bool Loaded,
    [property: JsonPropertyName("romTitle")] string RomTitle,
    [property: JsonPropertyName("mapper")] int Mapper,
    [property: JsonPropertyName("prgRomBanks")] int PrgRomBanks,
    [property: JsonPropertyName("chrRomBanks")] int ChrRomBanks);

public sealed record SaveStateResult(
    [property: JsonPropertyName("saved")] bool Saved,
    [property: JsonPropertyName("path")] string Path);

public sealed record LoadStateResult(
    [property: JsonPropertyName("loaded")] bool Loaded,
    [property: JsonPropertyName("path")] string Path);

public sealed record ResetResult([property: JsonPropertyName("reset")] bool Reset);

public sealed record TimelineCounters(
    [property: JsonPropertyName("frames")] ulong Frames,
    [property: JsonPropertyName("cycles")] ulong Cycles,
    [property: JsonPropertyName("instructions")] ulong Instructions = 0);

public sealed record StepInstructionResult(
    [property: JsonPropertyName("pcBefore")] string PcBefore,
    [property: JsonPropertyName("pcAfter")] string PcAfter,
    [property: JsonPropertyName("registers")] NesCpuRegisters Registers,
    [property: JsonPropertyName("disassembly")] string Disassembly,
    [property: JsonPropertyName("instructionsRun")] int InstructionsRun,
    [property: JsonPropertyName("timeline")] TimelineCounters Timeline)
{
    public StepInstructionResult(string pcBefore, string pcAfter, NesCpuRegisters registers, string disassembly)
        : this(pcBefore, pcAfter, registers, disassembly, 1, new TimelineCounters(0, 0))
    {
    }
}

public sealed record RunFrameResult(
    [property: JsonPropertyName("framesRun")] int FramesRun,
    [property: JsonPropertyName("totalFrames")] long TotalFrames,
    [property: JsonPropertyName("registers")] NesCpuRegisters Registers,
    [property: JsonPropertyName("hitBreakpoint")] bool HitBreakpoint,
    [property: JsonPropertyName("timeline")] TimelineCounters Timeline)
{
    public RunFrameResult(int framesRun, long totalFrames, NesCpuRegisters registers, bool hitBreakpoint)
        : this(framesRun, totalFrames, registers, hitBreakpoint, new TimelineCounters((ulong)Math.Max(0, totalFrames), 0))
    {
    }
}

public enum NesButton
{
    A = 0,
    B = 1,
    Select = 2,
    Start = 3,
    Up = 4,
    Down = 5,
    Left = 6,
    Right = 7,
}

public sealed record ControllerStateResult(
    [property: JsonPropertyName("a")] bool A,
    [property: JsonPropertyName("b")] bool B,
    [property: JsonPropertyName("select")] bool Select,
    [property: JsonPropertyName("start")] bool Start,
    [property: JsonPropertyName("up")] bool Up,
    [property: JsonPropertyName("down")] bool Down,
    [property: JsonPropertyName("left")] bool Left,
    [property: JsonPropertyName("right")] bool Right,
    [property: JsonPropertyName("pressed")] IReadOnlyList<string> Pressed);

public sealed record PressButtonsResult(
    [property: JsonPropertyName("framesRun")] int FramesRun,
    [property: JsonPropertyName("released")] ControllerStateResult Released,
    [property: JsonPropertyName("registers")] NesCpuRegisters Registers);

public sealed record ContinueResult(
    [property: JsonPropertyName("stopped")] bool Stopped,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("pc")] string Pc,
    [property: JsonPropertyName("registers")] NesCpuRegisters Registers,
    [property: JsonPropertyName("timeline")] TimelineCounters Timeline,
    [property: JsonPropertyName("instructionsRun")] ulong InstructionsRun)
{
    public ContinueResult(bool stopped, string reason, string pc, NesCpuRegisters registers)
        : this(stopped, reason, pc, registers, new TimelineCounters(0, 0), 0)
    {
    }
}

public sealed record BreakpointSetResult(
    [property: JsonPropertyName("breakpointId")] string BreakpointId,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("enabled")] bool Enabled);

public sealed record ClearBreakpointResult([property: JsonPropertyName("cleared")] bool Cleared);

public sealed record ListBreakpointsResult(
    [property: JsonPropertyName("breakpoints")] IReadOnlyList<BreakpointEntry> Breakpoints);

public sealed record BreakpointEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("condition")] string? Condition);

public enum WatchpointMode
{
    Read,
    Write,
    Access,
}

public sealed record WatchpointSetResult(
    [property: JsonPropertyName("watchpointId")] string WatchpointId,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("length")] int Length)
{
    public WatchpointSetResult(string watchpointId, string address, string mode, bool enabled)
        : this(watchpointId, address, mode, enabled, 1)
    {
    }
}

public sealed record ClearWatchpointResult([property: JsonPropertyName("cleared")] bool Cleared);

public sealed record ListWatchpointsResult(
    [property: JsonPropertyName("watchpoints")] IReadOnlyList<WatchpointEntry> Watchpoints);

public sealed record WatchpointEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("length")] int Length)
{
    public WatchpointEntry(string id, string address, string mode, bool enabled)
        : this(id, address, mode, enabled, 1)
    {
    }
}

public sealed record SessionStateResult(
    [property: JsonPropertyName("romLoaded")] bool RomLoaded,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("mapper")] int? Mapper,
    [property: JsonPropertyName("pc")] string? Pc,
    [property: JsonPropertyName("totalFrames")] long TotalFrames,
    [property: JsonPropertyName("timeline")] TimelineCounters Timeline)
{
    public SessionStateResult(bool romLoaded, string? title, int? mapper, string? pc, long totalFrames)
        : this(romLoaded, title, mapper, pc, totalFrames, new TimelineCounters((ulong)Math.Max(0, totalFrames), 0))
    {
    }
}

public sealed record NesCpuRegisters(
    [property: JsonPropertyName("a")] string A,
    [property: JsonPropertyName("x")] string X,
    [property: JsonPropertyName("y")] string Y,
    [property: JsonPropertyName("sp")] string Sp,
    [property: JsonPropertyName("pc")] string Pc,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("carry")] bool Carry,
    [property: JsonPropertyName("zero")] bool Zero,
    [property: JsonPropertyName("interruptDisable")] bool InterruptDisable,
    [property: JsonPropertyName("decimalMode")] bool DecimalMode,
    [property: JsonPropertyName("overflow")] bool Overflow,
    [property: JsonPropertyName("negative")] bool Negative,
    [property: JsonPropertyName("cycles")] long Cycles);

public sealed record MemoryReadResult(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("bytesHex")] string BytesHex,
    [property: JsonPropertyName("bytes")] byte[] Bytes,
    [property: JsonPropertyName("ascii")] string Ascii);

public sealed record WriteMemoryResult(
    [property: JsonPropertyName("written")] bool Written,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("length")] int Length);

public sealed record DisassembleResult(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("instructions")] IReadOnlyList<DisassembledInstruction> Instructions);

public sealed record DisassembledInstruction(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("bytes")] string Bytes,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("symbol")] string? Symbol);

public sealed record OamDumpResult([property: JsonPropertyName("sprites")] IReadOnlyList<OamSprite> Sprites);

public sealed record OamSprite(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("tile")] string Tile,
    [property: JsonPropertyName("attributes")] string Attributes,
    [property: JsonPropertyName("visible")] bool Visible);

public sealed record PpuStateResult(
    [property: JsonPropertyName("ppuctrl")] string PpuCtrl,
    [property: JsonPropertyName("ppumask")] string PpuMask,
    [property: JsonPropertyName("ppustatus")] string PpuStatus,
    [property: JsonPropertyName("oamaddr")] string OamAddr,
    [property: JsonPropertyName("ppuaddr")] string PpuAddr,
    [property: JsonPropertyName("ppuscroll")] string? PpuScroll,
    [property: JsonPropertyName("scanline")] int Scanline,
    [property: JsonPropertyName("cycle")] int Cycle,
    [property: JsonPropertyName("nmi")] bool Nmi,
    [property: JsonPropertyName("renderingEnabled")] bool RenderingEnabled,
    [property: JsonPropertyName("spritesEnabled")] bool SpritesEnabled,
    [property: JsonPropertyName("backgroundEnabled")] bool BackgroundEnabled,
    [property: JsonPropertyName("ppuCycles")] long PpuCycles)
{
    [JsonPropertyName("v")]
    public string V { get; init; } = "0x0000";

    [JsonPropertyName("t")]
    public string T { get; init; } = "0x0000";

    [JsonPropertyName("x")]
    public int X { get; init; }

    [JsonPropertyName("w")]
    public bool W { get; init; }

    [JsonPropertyName("vblank")]
    public bool VBlank { get; init; }

    [JsonPropertyName("renderingActive")]
    public bool RenderingActive { get; init; }

    [JsonPropertyName("control")]
    public PpuControlState Control { get; init; } = new(0, "0x2000", 1, "0x0000", "0x0000", "8x8", false);

    [JsonPropertyName("mask")]
    public PpuMaskState Mask { get; init; } = new(false, false, false, false, false, false, false, false);

    [JsonPropertyName("status")]
    public PpuStatusState Status { get; init; } = new(false, false, false);

    [JsonPropertyName("timeline")]
    public TimelineCounters Timeline { get; init; } = new(0, 0);
}

public sealed record PpuControlState(
    [property: JsonPropertyName("nametableSelect")] int NametableSelect,
    [property: JsonPropertyName("nametableAddress")] string NametableAddress,
    [property: JsonPropertyName("vramIncrement")] int VramIncrement,
    [property: JsonPropertyName("spritePatternTableAddress")] string SpritePatternTableAddress,
    [property: JsonPropertyName("backgroundPatternTableAddress")] string BackgroundPatternTableAddress,
    [property: JsonPropertyName("spriteSize")] string SpriteSize,
    [property: JsonPropertyName("nmiEnabled")] bool NmiEnabled);

public sealed record PpuMaskState(
    [property: JsonPropertyName("greyscale")] bool Greyscale,
    [property: JsonPropertyName("backgroundLeftEdgeEnabled")] bool BackgroundLeftEdgeEnabled,
    [property: JsonPropertyName("spriteLeftEdgeEnabled")] bool SpriteLeftEdgeEnabled,
    [property: JsonPropertyName("backgroundEnabled")] bool BackgroundEnabled,
    [property: JsonPropertyName("spritesEnabled")] bool SpritesEnabled,
    [property: JsonPropertyName("emphasizeRed")] bool EmphasizeRed,
    [property: JsonPropertyName("emphasizeGreen")] bool EmphasizeGreen,
    [property: JsonPropertyName("emphasizeBlue")] bool EmphasizeBlue);

public sealed record PpuStatusState(
    [property: JsonPropertyName("spriteOverflow")] bool SpriteOverflow,
    [property: JsonPropertyName("spriteZeroHit")] bool SpriteZeroHit,
    [property: JsonPropertyName("vblank")] bool VBlank);

public sealed record LastWriterResult(
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("pc")] string? Pc,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("writeCount")] ulong WriteCount);

public sealed record TraceUntilWriteResult(
    [property: JsonPropertyName("stopped")] bool Stopped,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("pc")] string? Pc,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("instructionsRun")] uint InstructionsRun,
    [property: JsonPropertyName("registers")] NesCpuRegisters Registers,
    [property: JsonPropertyName("timeline")] TimelineCounters Timeline)
{
    public TraceUntilWriteResult(bool stopped, string reason, string address, string? pc, string? value, uint instructionsRun, NesCpuRegisters registers)
        : this(stopped, reason, address, pc, value, instructionsRun, registers, new TimelineCounters(0, 0))
    {
    }
}

public sealed record LastWritersResult(
    [property: JsonPropertyName("writers")] IReadOnlyList<LastWriterResult> Writers);

public sealed record TraceUntilWriteRangeResult(
    [property: JsonPropertyName("stopped")] bool Stopped,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("hitAddress")] string? HitAddress,
    [property: JsonPropertyName("pc")] string? Pc,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("instructionsRun")] uint InstructionsRun,
    [property: JsonPropertyName("registers")] NesCpuRegisters Registers,
    [property: JsonPropertyName("ppuState")] PpuStateResult PpuState,
    [property: JsonPropertyName("disassembly")] DisassembleResult Disassembly,
    [property: JsonPropertyName("timeline")] TimelineCounters Timeline);

public sealed record PpuRegisterTraceRequest(
    int FrameCount,
    int MaxEvents,
    IReadOnlySet<ushort> Registers,
    IReadOnlyList<NesButton> Buttons);

public sealed record PpuRegisterTraceResult(
    [property: JsonPropertyName("framesRequested")] int FramesRequested,
    [property: JsonPropertyName("framesRun")] int FramesRun,
    [property: JsonPropertyName("initialPpuState")] PpuStateResult InitialPpuState,
    [property: JsonPropertyName("finalPpuState")] PpuStateResult FinalPpuState,
    [property: JsonPropertyName("events")] IReadOnlyList<PpuRegisterWriteEvent> Events,
    [property: JsonPropertyName("eventCount")] int EventCount,
    [property: JsonPropertyName("eventsObserved")] int EventsObserved,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("hitBreakpoint")] bool HitBreakpoint,
    [property: JsonPropertyName("stopReason")] string StopReason,
    [property: JsonPropertyName("timeline")] TimelineCounters Timeline);

public sealed record PpuRegisterWriteEvent(
    [property: JsonPropertyName("frameOffset")] int FrameOffset,
    [property: JsonPropertyName("frame")] ulong Frame,
    [property: JsonPropertyName("cpuCycle")] ulong CpuCycle,
    [property: JsonPropertyName("instructionCounter")] ulong InstructionCounter,
    [property: JsonPropertyName("pc")] string Pc,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("register")] string Register,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("before")] PpuRegisterSnapshot Before,
    [property: JsonPropertyName("after")] PpuRegisterSnapshot After);

public sealed record PpuRegisterSnapshot(
    [property: JsonPropertyName("scanline")] int Scanline,
    [property: JsonPropertyName("dot")] int Dot,
    [property: JsonPropertyName("vblank")] bool VBlank,
    [property: JsonPropertyName("renderingActive")] bool RenderingActive,
    [property: JsonPropertyName("v")] string V,
    [property: JsonPropertyName("t")] string T,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("w")] bool W);

public sealed record MemoryProbe(ushort Address, int Length);

public sealed class ExecutionMemoryProbeInput
{
    [JsonPropertyName("address")]
    public string Address { get; init; } = "";

    [JsonPropertyName("length")]
    public int Length { get; init; }
}

public sealed record ExecutionObservationRequest(
    int FrameCount,
    IReadOnlyList<NesButton> Buttons,
    IReadOnlyList<MemoryProbe> MemoryProbes,
    bool IncludePpuState,
    bool TracePpuWrites,
    int MaxPpuEvents,
    IReadOnlySet<ushort> PpuRegisters);

public sealed record ExecutionObservationResult(
    [property: JsonPropertyName("framesRequested")] int FramesRequested,
    [property: JsonPropertyName("framesRun")] int FramesRun,
    [property: JsonPropertyName("heldButtons")] IReadOnlyList<string> HeldButtons,
    [property: JsonPropertyName("initialFramebufferHash")] string InitialFramebufferHash,
    [property: JsonPropertyName("frames")] IReadOnlyList<ExecutionFrameObservation> Frames,
    [property: JsonPropertyName("ppuEvents")] IReadOnlyList<PpuRegisterWriteEvent> PpuEvents,
    [property: JsonPropertyName("ppuEventCount")] int PpuEventCount,
    [property: JsonPropertyName("ppuEventsObserved")] int PpuEventsObserved,
    [property: JsonPropertyName("ppuTraceTruncated")] bool PpuTraceTruncated,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("initialNametables")] NametableDumpResult InitialNametables,
    [property: JsonPropertyName("finalNametables")] NametableDumpResult FinalNametables,
    [property: JsonPropertyName("hitBreakpoint")] bool HitBreakpoint,
    [property: JsonPropertyName("stopReason")] string StopReason,
    [property: JsonPropertyName("released")] ControllerStateResult Released,
    [property: JsonPropertyName("limits")] ExecutionObservationAppliedLimits Limits,
    [property: JsonPropertyName("timeline")] TimelineCounters Timeline);

public sealed record ExecutionFrameObservation(
    [property: JsonPropertyName("screen")] ScreenFrameObservation Screen,
    [property: JsonPropertyName("memory")] IReadOnlyList<MemoryProbeObservation> Memory,
    [property: JsonPropertyName("ppuState")] PpuStateResult? PpuState);

public sealed record MemoryProbeObservation(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("bytesHex")] string BytesHex);

public sealed record ExecutionObservationAppliedLimits(
    [property: JsonPropertyName("maxFrames")] int MaxFrames,
    [property: JsonPropertyName("maxMemoryProbes")] int MaxMemoryProbes,
    [property: JsonPropertyName("maxMemoryProbeLength")] int MaxMemoryProbeLength,
    [property: JsonPropertyName("maxMemoryBytesPerFrame")] int MaxMemoryBytesPerFrame,
    [property: JsonPropertyName("maxPpuEvents")] int MaxPpuEvents);

public sealed record RunUntilConditionResult(
    [property: JsonPropertyName("stopped")] bool Stopped,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("pc")] string Pc,
    [property: JsonPropertyName("instructionsRun")] uint InstructionsRun,
    [property: JsonPropertyName("framesRun")] ulong FramesRun,
    [property: JsonPropertyName("registers")] NesCpuRegisters Registers,
    [property: JsonPropertyName("ppuState")] PpuStateResult PpuState,
    [property: JsonPropertyName("timeline")] TimelineCounters Timeline);

public sealed class InputTimelineStep
{
    [JsonPropertyName("frames")]
    public int Frames { get; init; }

    [JsonPropertyName("buttons")]
    public IReadOnlyList<string> Buttons { get; init; } = [];

    [JsonPropertyName("readRegisters")]
    public bool ReadRegisters { get; init; }

    [JsonPropertyName("readPpuState")]
    public bool ReadPpuState { get; init; }

    [JsonPropertyName("dumpOam")]
    public bool DumpOam { get; init; }

    [JsonPropertyName("capture")]
    public bool Capture { get; init; }

    [JsonPropertyName("dumpTilemap")]
    public bool DumpTilemap { get; init; }

    [JsonPropertyName("tilemapAddress")]
    public string? TilemapAddress { get; init; }

    [JsonPropertyName("memoryAddress")]
    public string? MemoryAddress { get; init; }

    [JsonPropertyName("memoryLength")]
    public int? MemoryLength { get; init; }
}

public sealed record InputTimelineStepResult(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("framesRun")] int FramesRun,
    [property: JsonPropertyName("totalFrames")] ulong TotalFrames,
    [property: JsonPropertyName("buttons")] IReadOnlyList<string> Buttons,
    [property: JsonPropertyName("registers")] NesCpuRegisters? Registers,
    [property: JsonPropertyName("ppuState")] PpuStateResult? PpuState,
    [property: JsonPropertyName("oam")] OamDumpResult? Oam,
    [property: JsonPropertyName("screenCapture")] ScreenCaptureResult? ScreenCapture,
    [property: JsonPropertyName("tilemap")] TilemapDumpResult? Tilemap,
    [property: JsonPropertyName("memory")] MemoryReadResult? Memory,
    [property: JsonPropertyName("timeline")] TimelineCounters? Timeline);

public sealed record InputTimelineResult(
    [property: JsonPropertyName("framesRun")] int FramesRun,
    [property: JsonPropertyName("released")] ControllerStateResult Released,
    [property: JsonPropertyName("steps")] IReadOnlyList<InputTimelineStepResult> Steps,
    [property: JsonPropertyName("timeline")] TimelineCounters Timeline);

public sealed record ScreenRegionResult(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("pixelCount")] int PixelCount,
    [property: JsonPropertyName("values")] IReadOnlyList<int>? Values,
    [property: JsonPropertyName("histogram")] IReadOnlyDictionary<string, int> Histogram,
    [property: JsonPropertyName("rowHashes")] IReadOnlyList<string> RowHashes);

public sealed record ScreenObservationResult(
    [property: JsonPropertyName("framesRequested")] int FramesRequested,
    [property: JsonPropertyName("framesRun")] int FramesRun,
    [property: JsonPropertyName("initialHash")] string InitialHash,
    [property: JsonPropertyName("samples")] IReadOnlyList<ScreenFrameObservation> Samples,
    [property: JsonPropertyName("hitBreakpoint")] bool HitBreakpoint,
    [property: JsonPropertyName("timeline")] TimelineCounters Timeline);

public sealed record ScreenFrameObservation(
    [property: JsonPropertyName("frameOffset")] int FrameOffset,
    [property: JsonPropertyName("totalFrame")] ulong TotalFrame,
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("changedPixels")] int ChangedPixels,
    [property: JsonPropertyName("changedTiles")] int ChangedTiles,
    [property: JsonPropertyName("changedBounds")] ScreenChangeBounds? ChangedBounds,
    [property: JsonPropertyName("changedTileRows")] IReadOnlyList<ScreenChangedTileRow> ChangedTileRows);

public sealed record ScreenChangeBounds(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

public sealed record ScreenChangedTileRow(
    [property: JsonPropertyName("row")] int Row,
    [property: JsonPropertyName("mask")] string Mask);

public sealed record TilemapDumpResult(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("rows")] IReadOnlyList<string> Rows,
    [property: JsonPropertyName("attributeAddress")] string AttributeAddress,
    [property: JsonPropertyName("attributeRows")] IReadOnlyList<string> AttributeRows);

public sealed record NametableDumpResult(
    [property: JsonPropertyName("detailsIncluded")] bool DetailsIncluded,
    [property: JsonPropertyName("nametables")] IReadOnlyList<NametableSnapshot> Nametables,
    [property: JsonPropertyName("timeline")] TimelineCounters Timeline);

public sealed record NametableSnapshot(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("tileHash")] string TileHash,
    [property: JsonPropertyName("attributeHash")] string AttributeHash,
    [property: JsonPropertyName("detail")] TilemapDumpResult? Detail);

public sealed record TilesetDumpResult(
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("tileCount")] int TileCount,
    [property: JsonPropertyName("tiles")] IReadOnlyList<TileDump> Tiles);

public sealed record TileDump(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("bytesHex")] string BytesHex);

public sealed record LoadSymbolsResult(
    [property: JsonPropertyName("loaded")] bool Loaded,
    [property: JsonPropertyName("symbolCount")] int SymbolCount);

public sealed record ResolveSymbolResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("bank")] int? Bank);

public sealed record ReadSymbolResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("bytes")] byte[] Bytes,
    [property: JsonPropertyName("bytesHex")] string BytesHex);

public sealed record ScreenCaptureResult(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("data")] byte[] Data);

public sealed record ScreenCaptureMetadata(
    [property: JsonPropertyName("timeline")] TimelineCounters Timeline,
    [property: JsonPropertyName("registers")] NesCpuRegisters? Registers,
    [property: JsonPropertyName("ppuState")] PpuStateResult? PpuState,
    [property: JsonPropertyName("romTitle")] string? RomTitle,
    [property: JsonPropertyName("mapper")] int? Mapper);

public sealed record ScreenCaptureArtifactResult(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("saved")] bool Saved,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("metadata")] ScreenCaptureMetadata? Metadata);
