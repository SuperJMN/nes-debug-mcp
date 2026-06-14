using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nes.Debug.Core;

namespace Nes.Debug.Mcp;

[McpServerToolType]
public static class NesDebugTools
{
    private const int MaxMemoryLength = 4096;
    private const int MaxInstructionCount = 10000;
    private const int MaxDisassemblyCount = 256;
    private const int MaxFrameCount = 600;
    private const int MaxContinueInstructions = 10_000_000;

    private static readonly IReadOnlyDictionary<string, NesButton> ButtonNames =
        new Dictionary<string, NesButton>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = NesButton.A,
            ["b"] = NesButton.B,
            ["select"] = NesButton.Select,
            ["start"] = NesButton.Start,
            ["up"] = NesButton.Up,
            ["down"] = NesButton.Down,
            ["left"] = NesButton.Left,
            ["right"] = NesButton.Right,
        };

    private static readonly NesButton[] CanonicalButtons =
    [
        NesButton.A,
        NesButton.B,
        NesButton.Select,
        NesButton.Start,
        NesButton.Up,
        NesButton.Down,
        NesButton.Left,
        NesButton.Right,
    ];

    [McpServerTool(Name = "load_rom", ReadOnly = false, Destructive = false)]
    [Description("Loads an iNES NES ROM into the active debug session.")]
    public static object LoadRom(INesDebugSession session, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Error("invalid_path", "ROM path is required.");
        }

        return ToToolResult(session.LoadRom(path));
    }

    [McpServerTool(Name = "reset", ReadOnly = false, Destructive = false)]
    [Description("Resets the active emulator session.")]
    public static object Reset(INesDebugSession session) => ToToolResult(session.Reset());

    [McpServerTool(Name = "step_instruction", ReadOnly = false, Destructive = false)]
    [Description("Steps one or more CPU instructions and returns the resulting registers and local disassembly.")]
    public static object StepInstruction(INesDebugSession session, int count = 1)
    {
        if (count is < 1 or > MaxInstructionCount)
        {
            return Error("invalid_count", $"Instruction count must be between 1 and {MaxInstructionCount}.");
        }

        return ToToolResult(session.StepInstruction(count));
    }

    [McpServerTool(Name = "run_frame", ReadOnly = false, Destructive = false)]
    [Description("Runs the emulator for one or more frames.")]
    public static object RunFrame(INesDebugSession session, int count = 1)
    {
        if (count is < 1 or > MaxFrameCount)
        {
            return Error("invalid_count", $"Frame count must be between 1 and {MaxFrameCount}.");
        }

        return ToToolResult(session.RunFrame(count));
    }

    [McpServerTool(Name = "set_controller", ReadOnly = false, Destructive = false)]
    [Description("Sets the currently held controller buttons. Pass an empty array to release every button.")]
    public static object SetController(INesDebugSession session, string[] buttons)
    {
        var parsed = ParseButtons(buttons);
        return parsed.IsSuccess
            ? ToToolResult(session.SetController(parsed.Value))
            : new ToolError(parsed.Error!);
    }

    [McpServerTool(Name = "press_buttons", ReadOnly = false, Destructive = false)]
    [Description("Holds one or more controller buttons for a bounded number of frames, then releases them.")]
    public static object PressButtons(INesDebugSession session, string[] buttons, int frameCount = 1)
    {
        if (frameCount is < 1 or > MaxFrameCount)
        {
            return Error("invalid_frame_count", $"frameCount must be between 1 and {MaxFrameCount}.");
        }

        var parsed = ParseButtons(buttons);
        return parsed.IsSuccess
            ? ToToolResult(session.PressButtons(parsed.Value, frameCount))
            : new ToolError(parsed.Error!);
    }

    [McpServerTool(Name = "continue_until_break", ReadOnly = false, Destructive = false)]
    [Description("Continues execution until a breakpoint or the explicit instruction limit.")]
    public static object ContinueUntilBreak(INesDebugSession session, int maxInstructions = 1_000_000)
    {
        if (maxInstructions is < 1 or > MaxContinueInstructions)
        {
            return Error("invalid_max_instructions", $"maxInstructions must be between 1 and {MaxContinueInstructions}.");
        }

        return ToToolResult(session.ContinueUntilBreak(maxInstructions));
    }

    [McpServerTool(Name = "set_breakpoint", ReadOnly = false, Destructive = false)]
    [Description("Sets an execution breakpoint at a 16-bit CPU address.")]
    public static object SetBreakpoint(INesDebugSession session, string address)
    {
        var parsed = ParseAddress(address);
        return parsed.IsSuccess
            ? ToToolResult(session.SetBreakpoint(parsed.Value.Address))
            : new ToolError(parsed.Error!);
    }

    [McpServerTool(Name = "clear_breakpoint", ReadOnly = false, Destructive = false)]
    [Description("Clears a breakpoint by breakpoint id.")]
    public static object ClearBreakpoint(INesDebugSession session, string breakpointId)
    {
        if (string.IsNullOrWhiteSpace(breakpointId))
        {
            return Error("invalid_breakpoint_id", "breakpointId is required.");
        }

        return ToToolResult(session.ClearBreakpoint(breakpointId));
    }

    [McpServerTool(Name = "list_breakpoints", ReadOnly = true, Destructive = false)]
    [Description("Lists all breakpoints currently registered in the active session.")]
    public static object ListBreakpoints(INesDebugSession session) => ToToolResult(session.ListBreakpoints());

    [McpServerTool(Name = "get_state", ReadOnly = true, Destructive = false)]
    [Description("Returns ROM load status, ROM metadata, current PC, and frame count.")]
    public static object GetState(INesDebugSession session) => ToToolResult(session.GetState());

    [McpServerTool(Name = "read_registers", ReadOnly = true, Destructive = false)]
    [Description("Reads 6502 CPU registers from the active session.")]
    public static object ReadRegisters(INesDebugSession session) => ToToolResult(session.ReadRegisters());

    [McpServerTool(Name = "read_memory", ReadOnly = true, Destructive = false)]
    [Description("Reads a bounded range of CPU address-space memory.")]
    public static object ReadMemory(INesDebugSession session, string address, int length)
    {
        if (length is < 1 or > MaxMemoryLength)
        {
            return Error("invalid_length", $"Memory length must be between 1 and {MaxMemoryLength} bytes.");
        }

        var parsed = ParseAddress(address);
        return parsed.IsSuccess
            ? ToToolResult(session.ReadMemory(parsed.Value.Address, length))
            : new ToolError(parsed.Error!);
    }

    [McpServerTool(Name = "write_memory", ReadOnly = false, Destructive = true)]
    [Description("Writes a bounded byte array to CPU address-space memory.")]
    public static object WriteMemory(INesDebugSession session, string address, int[] bytes)
    {
        if (bytes.Length is < 1 or > MaxMemoryLength)
        {
            return Error("invalid_length", $"Write length must be between 1 and {MaxMemoryLength} bytes.");
        }

        if (bytes.Any(value => value is < 0 or > 0xFF))
        {
            return Error("invalid_bytes", "Every byte must be in the inclusive range 0..255.");
        }

        var parsed = ParseAddress(address);
        return parsed.IsSuccess
            ? ToToolResult(session.WriteMemory(parsed.Value.Address, bytes.Select(value => (byte)value).ToArray()))
            : new ToolError(parsed.Error!);
    }

    [McpServerTool(Name = "disassemble", ReadOnly = true, Destructive = false)]
    [Description("Disassembles a bounded number of instructions around a CPU address.")]
    public static object Disassemble(INesDebugSession session, string address, int instructionCount = 16)
    {
        if (instructionCount is < 1 or > MaxDisassemblyCount)
        {
            return Error("invalid_instruction_count", $"instructionCount must be between 1 and {MaxDisassemblyCount}.");
        }

        var parsed = ParseAddress(address);
        return parsed.IsSuccess
            ? ToToolResult(session.Disassemble(parsed.Value.Address, instructionCount))
            : new ToolError(parsed.Error!);
    }

    [McpServerTool(Name = "capture_screen", ReadOnly = true, Destructive = false)]
    [Description("Captures the current 256x240 screen image as inline PNG image content.")]
    public static object CaptureScreen(INesDebugSession session)
    {
        var result = session.CaptureScreen();
        return result.IsSuccess
            ? ImageContentBlock.FromBytes(result.Value.Data, result.Value.MimeType)
            : new ToolError(result.Error!);
    }

    private static DebugResult<NesAddress> ParseAddress(string address) => NesAddress.Parse(address);

    private static DebugResult<IReadOnlyList<NesButton>> ParseButtons(IReadOnlyList<string>? buttons)
    {
        if (buttons is null)
        {
            return DebugResult<IReadOnlyList<NesButton>>.Failure(
                "invalid_buttons",
                "buttons is required. Use an empty array to release every button.");
        }

        var selected = new HashSet<NesButton>();
        foreach (var rawButton in buttons)
        {
            var button = rawButton?.Trim();
            if (string.IsNullOrEmpty(button))
            {
                return DebugResult<IReadOnlyList<NesButton>>.Failure("invalid_button", "Button names must not be empty.");
            }

            if (!ButtonNames.TryGetValue(button, out var parsed))
            {
                return DebugResult<IReadOnlyList<NesButton>>.Failure(
                    "invalid_button",
                    $"Unknown button '{button}'. Valid buttons: {string.Join(", ", ButtonNames.Keys)}.");
            }

            selected.Add(parsed);
        }

        return DebugResult<IReadOnlyList<NesButton>>.Success(CanonicalButtons.Where(selected.Contains).ToArray());
    }

    private static object ToToolResult<T>(DebugResult<T> result) => result.IsSuccess ? result.Value! : new ToolError(result.Error!);

    private static ToolError Error(string code, string message) => new(new DebugError(code, message));
}
