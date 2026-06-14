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
    private const int MaxTraceInstructions = 10_000_000;
    private const int MaxTileCount = 512;

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

    [McpServerTool(Name = "save_state", ReadOnly = false, Destructive = false)]
    [Description("Saves the active emulator state to a managed NES savestate file.")]
    public static object SaveState(INesDebugSession session, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Error("invalid_path", "Save state path is required.");
        }

        return ToToolResult(session.SaveState(path));
    }

    [McpServerTool(Name = "load_state", ReadOnly = false, Destructive = true)]
    [Description("Loads a managed NES savestate file into the active emulator session.")]
    public static object LoadState(INesDebugSession session, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Error("invalid_path", "Save state path is required.");
        }

        return ToToolResult(session.LoadState(path));
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

    [McpServerTool(Name = "set_joypad", ReadOnly = false, Destructive = false)]
    [Description("Game Boy-compatible alias for set_controller. Sets the currently held controller buttons.")]
    public static object SetJoypad(INesDebugSession session, string[] buttons) => SetController(session, buttons);

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

    [McpServerTool(Name = "step_over", ReadOnly = false, Destructive = false)]
    [Description("Steps over a JSR instruction, or steps one instruction when not on JSR.")]
    public static object StepOver(INesDebugSession session, int maxInstructions = 100_000)
    {
        if (maxInstructions is < 1 or > MaxContinueInstructions)
        {
            return Error("invalid_max_instructions", $"maxInstructions must be between 1 and {MaxContinueInstructions}.");
        }

        return ToToolResult(session.StepOver(maxInstructions));
    }

    [McpServerTool(Name = "step_out", ReadOnly = false, Destructive = false)]
    [Description("Runs until the current 6502 subroutine returns, a breakpoint/watchpoint hits, or the instruction limit is reached.")]
    public static object StepOut(INesDebugSession session, int maxInstructions = 100_000)
    {
        if (maxInstructions is < 1 or > MaxContinueInstructions)
        {
            return Error("invalid_max_instructions", $"maxInstructions must be between 1 and {MaxContinueInstructions}.");
        }

        return ToToolResult(session.StepOut(maxInstructions));
    }

    [McpServerTool(Name = "set_breakpoint", ReadOnly = false, Destructive = false)]
    [Description("Sets an execution breakpoint at a 16-bit CPU address.")]
    public static object SetBreakpoint(INesDebugSession session, string address, string? condition = null)
    {
        var parsed = ParseAddress(address);
        if (!parsed.IsSuccess)
        {
            return new ToolError(parsed.Error!);
        }

        if (!BreakpointCondition.TryParse(condition, out _, out var conditionError))
        {
            return Error("invalid_breakpoint_condition", $"Invalid breakpoint condition: {conditionError}");
        }

        return ToToolResult(session.SetBreakpoint(parsed.Value.Address, condition));
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

    [McpServerTool(Name = "set_watchpoint", ReadOnly = false, Destructive = false)]
    [Description("Sets a memory watchpoint at a 16-bit CPU address. mode is read, write, or access.")]
    public static object SetWatchpoint(INesDebugSession session, string address, string mode = "write")
    {
        var parsed = ParseAddress(address);
        if (!parsed.IsSuccess)
        {
            return new ToolError(parsed.Error!);
        }

        var parsedMode = ParseWatchpointMode(mode);
        return parsedMode.IsSuccess
            ? ToToolResult(session.SetWatchpoint(parsed.Value.Address, parsedMode.Value))
            : new ToolError(parsedMode.Error!);
    }

    [McpServerTool(Name = "clear_watchpoint", ReadOnly = false, Destructive = false)]
    [Description("Clears a watchpoint by watchpoint id.")]
    public static object ClearWatchpoint(INesDebugSession session, string watchpointId)
    {
        if (string.IsNullOrWhiteSpace(watchpointId))
        {
            return Error("invalid_watchpoint_id", "watchpointId is required.");
        }

        return ToToolResult(session.ClearWatchpoint(watchpointId));
    }

    [McpServerTool(Name = "list_watchpoints", ReadOnly = true, Destructive = false)]
    [Description("Lists all watchpoints currently registered in the active session.")]
    public static object ListWatchpoints(INesDebugSession session) => ToToolResult(session.ListWatchpoints());

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

    [McpServerTool(Name = "load_symbols", ReadOnly = false, Destructive = false)]
    [Description("Loads a simple symbol file into the active session.")]
    public static object LoadSymbols(INesDebugSession session, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Error("invalid_path", "Symbol file path is required.");
        }

        return ToToolResult(session.LoadSymbols(path));
    }

    [McpServerTool(Name = "resolve_symbol", ReadOnly = true, Destructive = false)]
    [Description("Resolves a loaded symbol name to an address.")]
    public static object ResolveSymbol(INesDebugSession session, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Error("invalid_symbol", "Symbol name is required.");
        }

        return ToToolResult(session.ResolveSymbol(name));
    }

    [McpServerTool(Name = "read_symbol", ReadOnly = true, Destructive = false)]
    [Description("Reads memory by loaded symbol name, with an optional bounded byte length.")]
    public static object ReadSymbol(INesDebugSession session, string name, int? length = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Error("invalid_symbol", "Symbol name is required.");
        }

        if (length is < 1 or > MaxMemoryLength)
        {
            return Error("invalid_length", $"Symbol read length must be between 1 and {MaxMemoryLength} bytes.");
        }

        return ToToolResult(session.ReadSymbol(name, length));
    }

    [McpServerTool(Name = "dump_oam", ReadOnly = true, Destructive = false)]
    [Description("Dumps the 64 NES OAM sprite entries from the active session.")]
    public static object DumpOam(INesDebugSession session) => ToToolResult(session.ReadOam());

    [McpServerTool(Name = "read_ppu_state", ReadOnly = true, Destructive = false)]
    [Description("Reads compact NES PPU state registers from the active session.")]
    public static object ReadPpuState(INesDebugSession session) => ToToolResult(session.ReadPpuState());

    [McpServerTool(Name = "capture_screen", ReadOnly = true, Destructive = false)]
    [Description("Captures the current 256x240 screen image as inline PNG image content.")]
    public static object CaptureScreen(INesDebugSession session)
    {
        var result = session.CaptureScreen();
        return result.IsSuccess
            ? ImageContentBlock.FromBytes(result.Value.Data, result.Value.MimeType)
            : new ToolError(result.Error!);
    }

    [McpServerTool(Name = "find_last_writer", ReadOnly = true, Destructive = false)]
    [Description("Returns the last observed write to an address since the session started or tracing began.")]
    public static object FindLastWriter(INesDebugSession session, string address)
    {
        var parsed = ParseAddress(address);
        return parsed.IsSuccess
            ? ToToolResult(session.FindLastWriter(parsed.Value.Address))
            : new ToolError(parsed.Error!);
    }

    [McpServerTool(Name = "trace_until_write", ReadOnly = false, Destructive = false)]
    [Description("Runs until the requested address is written or the explicit instruction limit is reached.")]
    public static object TraceUntilWrite(INesDebugSession session, string address, int maxInstructions)
    {
        if (maxInstructions is < 1 or > MaxTraceInstructions)
        {
            return Error("invalid_max_instructions", $"maxInstructions must be between 1 and {MaxTraceInstructions}.");
        }

        var parsed = ParseAddress(address);
        return parsed.IsSuccess
            ? ToToolResult(session.TraceUntilWrite(parsed.Value.Address, maxInstructions))
            : new ToolError(parsed.Error!);
    }

    [McpServerTool(Name = "dump_tilemap", ReadOnly = true, Destructive = false)]
    [Description("Dumps a 32x30 NES nametable tilemap from PPU memory.")]
    public static object DumpTilemap(INesDebugSession session, string address = "0x2000")
    {
        var parsed = ParseAddress(address);
        if (!parsed.IsSuccess)
        {
            return new ToolError(parsed.Error!);
        }

        if (parsed.Value.Address < 0x2000 || parsed.Value.Address + 32 * 30 > 0x3000)
        {
            return Error("invalid_tilemap_address", "Tilemap range must fit within PPU nametable memory 0x2000..0x2FFF.");
        }

        return ToToolResult(session.DumpTilemap(parsed.Value.Address));
    }

    [McpServerTool(Name = "dump_tileset", ReadOnly = true, Destructive = false)]
    [Description("Dumps NES pattern table tile data from PPU memory. Each tile is 16 bytes.")]
    public static object DumpTileset(INesDebugSession session, string address = "0x0000", int tileCount = 512)
    {
        if (tileCount is < 1 or > MaxTileCount)
        {
            return Error("invalid_tile_count", $"tileCount must be between 1 and {MaxTileCount}.");
        }

        var parsed = ParseAddress(address);
        if (!parsed.IsSuccess)
        {
            return new ToolError(parsed.Error!);
        }

        if (parsed.Value.Address + tileCount * 16 > 0x2000)
        {
            return Error("invalid_tileset_range", "Tileset range must fit within PPU pattern table memory 0x0000..0x1FFF.");
        }

        return ToToolResult(session.DumpTileset(parsed.Value.Address, tileCount));
    }

    private static DebugResult<NesAddress> ParseAddress(string address) => NesAddress.Parse(address);

    private static DebugResult<WatchpointMode> ParseWatchpointMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return DebugResult<WatchpointMode>.Failure("invalid_watchpoint_mode", "Watchpoint mode must be read, write, or access.");
        }

        return mode.Trim().ToLowerInvariant() switch
        {
            "read" => DebugResult<WatchpointMode>.Success(WatchpointMode.Read),
            "write" => DebugResult<WatchpointMode>.Success(WatchpointMode.Write),
            "access" => DebugResult<WatchpointMode>.Success(WatchpointMode.Access),
            _ => DebugResult<WatchpointMode>.Failure("invalid_watchpoint_mode", "Watchpoint mode must be read, write, or access."),
        };
    }

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
