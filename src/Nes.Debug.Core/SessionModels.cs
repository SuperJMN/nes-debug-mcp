using System.Text.Json.Serialization;

namespace Nes.Debug.Core;

public sealed record LoadRomResult(
    [property: JsonPropertyName("loaded")] bool Loaded,
    [property: JsonPropertyName("romTitle")] string RomTitle,
    [property: JsonPropertyName("mapper")] int Mapper,
    [property: JsonPropertyName("prgRomBanks")] int PrgRomBanks,
    [property: JsonPropertyName("chrRomBanks")] int ChrRomBanks);

public sealed record ResetResult([property: JsonPropertyName("reset")] bool Reset);

public sealed record StepInstructionResult(
    [property: JsonPropertyName("pcBefore")] string PcBefore,
    [property: JsonPropertyName("pcAfter")] string PcAfter,
    [property: JsonPropertyName("registers")] NesCpuRegisters Registers,
    [property: JsonPropertyName("disassembly")] string Disassembly);

public sealed record RunFrameResult(
    [property: JsonPropertyName("framesRun")] int FramesRun,
    [property: JsonPropertyName("totalFrames")] long TotalFrames,
    [property: JsonPropertyName("registers")] NesCpuRegisters Registers,
    [property: JsonPropertyName("hitBreakpoint")] bool HitBreakpoint);

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
    [property: JsonPropertyName("registers")] NesCpuRegisters Registers);

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
    [property: JsonPropertyName("enabled")] bool Enabled);

public sealed record SessionStateResult(
    [property: JsonPropertyName("romLoaded")] bool RomLoaded,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("mapper")] int? Mapper,
    [property: JsonPropertyName("pc")] string? Pc,
    [property: JsonPropertyName("totalFrames")] long TotalFrames);

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
    [property: JsonPropertyName("text")] string Text);

public sealed record ScreenCaptureResult(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("data")] byte[] Data);
