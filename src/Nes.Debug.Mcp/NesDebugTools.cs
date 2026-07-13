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
    private const int MaxWatchpointRangeLength = 4096;
    private const int MaxInputTimelineSteps = 128;
    private const int MaxInputTimelineFrames = 3600;
    private const int ScreenWidth = 256;
    private const int ScreenHeight = 240;

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

    [McpServerTool(Name = "run_until_condition", ReadOnly = false, Destructive = false)]
    [Description("Runs until a register or memory condition is true, or a bounded stop condition is reached.")]
    public static object RunUntilCondition(INesDebugSession session, string condition, int maxInstructions = 1_000_000, int maxFrames = 120)
    {
        if (maxInstructions is < 1 or > MaxContinueInstructions)
        {
            return Error("invalid_max_instructions", $"maxInstructions must be between 1 and {MaxContinueInstructions}.");
        }

        if (maxFrames is < 1 or > MaxFrameCount)
        {
            return Error("invalid_max_frames", $"maxFrames must be between 1 and {MaxFrameCount}.");
        }

        if (!BreakpointCondition.TryParse(condition, out var parsed, out var conditionError) || parsed is null)
        {
            return Error("invalid_condition", $"Invalid condition: {conditionError ?? "Condition is required."}");
        }

        return ToToolResult(session.RunUntilCondition(condition, maxInstructions, maxFrames));
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

    [McpServerTool(Name = "set_watchpoint_range", ReadOnly = false, Destructive = false)]
    [Description("Sets a bounded memory watchpoint range. mode is read, write, or access.")]
    public static object SetWatchpointRange(INesDebugSession session, string address, int length, string mode = "write")
    {
        var parsed = ParseAddress(address);
        if (!parsed.IsSuccess)
        {
            return new ToolError(parsed.Error!);
        }

        var range = ValidateAddressRange(parsed.Value.Address, length, MaxWatchpointRangeLength);
        if (!range.IsSuccess)
        {
            return new ToolError(range.Error!);
        }

        var parsedMode = ParseWatchpointMode(mode);
        return parsedMode.IsSuccess
            ? ToToolResult(session.SetWatchpointRange(parsed.Value.Address, length, parsedMode.Value))
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
    [Description("Captures the current 256x240 screen image as inline PNG image content, or saves it to a safe relative PNG path.")]
    public static object CaptureScreen(INesDebugSession session, string? path = null, bool includeMetadata = false)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var safePath = ResolveSafeArtifactPath(path);
            if (!safePath.IsSuccess)
            {
                return new ToolError(safePath.Error!);
            }

            var savedCapture = session.CaptureScreen();
            if (!savedCapture.IsSuccess)
            {
                return new ToolError(savedCapture.Error!);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(safePath.Value.FullPath)!);
            File.WriteAllBytes(safePath.Value.FullPath, savedCapture.Value.Data);
            return new ScreenCaptureArtifactResult(
                savedCapture.Value.Width,
                savedCapture.Value.Height,
                savedCapture.Value.MimeType,
                true,
                safePath.Value.RelativePath,
                includeMetadata ? BuildCaptureMetadata(session) : null);
        }

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

    [McpServerTool(Name = "find_last_writers", ReadOnly = true, Destructive = false)]
    [Description("Returns the last observed writes in a bounded address range.")]
    public static object FindLastWriters(INesDebugSession session, string address, int length)
    {
        var parsed = ParseAddress(address);
        if (!parsed.IsSuccess)
        {
            return new ToolError(parsed.Error!);
        }

        var range = ValidateAddressRange(parsed.Value.Address, length, MaxWatchpointRangeLength);
        return range.IsSuccess
            ? ToToolResult(session.FindLastWriters(parsed.Value.Address, length))
            : new ToolError(range.Error!);
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

    [McpServerTool(Name = "trace_until_write_range", ReadOnly = false, Destructive = false)]
    [Description("Runs until any address in a bounded range is written or the explicit instruction limit is reached.")]
    public static object TraceUntilWriteRange(INesDebugSession session, string address, int length, int maxInstructions)
    {
        if (maxInstructions is < 1 or > MaxTraceInstructions)
        {
            return Error("invalid_max_instructions", $"maxInstructions must be between 1 and {MaxTraceInstructions}.");
        }

        var parsed = ParseAddress(address);
        if (!parsed.IsSuccess)
        {
            return new ToolError(parsed.Error!);
        }

        var range = ValidateAddressRange(parsed.Value.Address, length, MaxWatchpointRangeLength);
        return range.IsSuccess
            ? ToToolResult(session.TraceUntilWriteRange(parsed.Value.Address, length, maxInstructions))
            : new ToolError(range.Error!);
    }

    [McpServerTool(Name = "trace_ppu_register_writes", ReadOnly = false, Destructive = false)]
    [Description("Atomically runs bounded AprNes frames and records every selected CPU write to $2000-$2007 with exact pre/post PPU register snapshots.")]
    public static object TracePpuRegisterWrites(
        INesDebugSession session,
        int frameCount = 1,
        int maxEvents = 1000,
        string[]? registers = null,
        string[]? buttons = null)
    {
        if (frameCount is < 1 or > PpuRegisterTracing.MaxFrames)
        {
            return Error("invalid_frame_count", $"frameCount must be between 1 and {PpuRegisterTracing.MaxFrames}.");
        }

        if (maxEvents is < 1 or > PpuRegisterTracing.MaxEvents)
        {
            return Error("invalid_max_events", $"maxEvents must be between 1 and {PpuRegisterTracing.MaxEvents}.");
        }

        IReadOnlySet<ushort> selectedRegisters;
        if (registers is null || registers.Length == 0)
        {
            selectedRegisters = PpuRegisterTracing.DefaultRegisters;
        }
        else
        {
            if (registers.Length > 8)
            {
                return Error("invalid_ppu_registers", "registers must contain between 1 and 8 values from $2000-$2007.");
            }

            var parsedRegisters = new HashSet<ushort>();
            foreach (var register in registers)
            {
                var parsed = ParsePpuRegister(register);
                if (!parsed.IsSuccess)
                {
                    return new ToolError(parsed.Error!);
                }

                parsedRegisters.Add(parsed.Value);
            }

            selectedRegisters = parsedRegisters;
        }

        var parsedButtons = ParseButtons(buttons ?? []);
        if (!parsedButtons.IsSuccess)
        {
            return new ToolError(parsedButtons.Error!);
        }

        return ToToolResult(session.TracePpuRegisterWrites(
            new PpuRegisterTraceRequest(frameCount, maxEvents, selectedRegisters, parsedButtons.Value)));
    }

    [McpServerTool(Name = "read_screen_region", ReadOnly = true, Destructive = false)]
    [Description("Reads deterministic palette-index data from a bounded screen region. Use palette_indices_raw to request every value, including a full 256x240 frame.")]
    public static object ReadScreenRegion(INesDebugSession session, int x, int y, int width, int height, string format = "palette_indices")
    {
        if (x < 0 || y < 0 || width < 1 || height < 1 || x + width > ScreenWidth || y + height > ScreenHeight)
        {
            return Error("invalid_screen_region", "Screen region must fit within 256x240.");
        }

        if (!PaletteIndexScreen.TryParseFormat(format, out _))
        {
            return Error("invalid_screen_region_format", "format must be palette_indices or palette_indices_raw.");
        }

        return ToToolResult(session.ReadScreenRegion(x, y, width, height, format));
    }

    [McpServerTool(Name = "observe_screen", ReadOnly = false, Destructive = false)]
    [Description("Runs frames while collecting compact screen-change observations for detecting transient corruption and flicker.")]
    public static object ObserveScreen(INesDebugSession session, int frameCount = 60)
    {
        if (frameCount is < 1 or > ScreenObserver.MaxFrames)
        {
            return Error("invalid_frame_count", $"frameCount must be between 1 and {ScreenObserver.MaxFrames}.");
        }

        return ToToolResult(session.ObserveScreen(frameCount));
    }

    [McpServerTool(Name = "run_input_timeline", ReadOnly = false, Destructive = false)]
    [Description("Runs a bounded deterministic sequence of complete held-button frame steps atomically.")]
    public static object RunInputTimeline(INesDebugSession session, InputTimelineStep[] steps)
    {
        if (steps is null || steps.Length is < 1 or > MaxInputTimelineSteps)
        {
            return Error("invalid_steps", $"steps must contain between 1 and {MaxInputTimelineSteps} entries.");
        }

        var totalFrames = 0;
        var normalized = new List<InputTimelineStep>(steps.Length);
        foreach (var step in steps)
        {
            if (step.Frames is < 1 or > MaxFrameCount)
            {
                return Error("invalid_frame_count", $"Each step frames value must be between 1 and {MaxFrameCount}.");
            }

            totalFrames += step.Frames;
            if (totalFrames > MaxInputTimelineFrames)
            {
                return Error("invalid_total_frames", $"The scenario must run at most {MaxInputTimelineFrames} frames.");
            }

            var buttons = ParseButtons(step.Buttons);
            if (!buttons.IsSuccess)
            {
                return new ToolError(buttons.Error!);
            }

            if (step.MemoryLength is < 1 or > MaxMemoryLength)
            {
                return Error("invalid_length", $"Memory observation length must be between 1 and {MaxMemoryLength} bytes.");
            }

            if (!string.IsNullOrWhiteSpace(step.MemoryAddress))
            {
                var parsedMemory = ParseAddress(step.MemoryAddress);
                if (!parsedMemory.IsSuccess)
                {
                    return new ToolError(parsedMemory.Error!);
                }
            }

            if (!string.IsNullOrWhiteSpace(step.TilemapAddress))
            {
                var parsedTilemap = ParseAddress(step.TilemapAddress);
                if (!parsedTilemap.IsSuccess)
                {
                    return new ToolError(parsedTilemap.Error!);
                }

                if (!NametableReader.IsBaseAddress(parsedTilemap.Value.Address))
                {
                    return Error("invalid_tilemap_address", "tilemapAddress must be a nametable base: 0x2000, 0x2400, 0x2800, or 0x2C00.");
                }
            }

            normalized.Add(new InputTimelineStep
            {
                Frames = step.Frames,
                Buttons = buttons.Value.Select(ButtonName).ToArray(),
                ReadRegisters = step.ReadRegisters,
                ReadPpuState = step.ReadPpuState,
                DumpOam = step.DumpOam,
                Capture = step.Capture,
                DumpTilemap = step.DumpTilemap,
                TilemapAddress = step.TilemapAddress,
                MemoryAddress = step.MemoryAddress,
                MemoryLength = step.MemoryLength,
            });
        }

        return ToToolResult(session.RunInputTimeline(normalized));
    }

    [McpServerTool(Name = "dump_nametables", ReadOnly = true, Destructive = false)]
    [Description("Atomically snapshots all four physical NES nametables with compact SHA-256 identities and optional tile/attribute detail.")]
    public static object DumpNametables(INesDebugSession session, bool includeDetails = false) =>
        ToToolResult(session.DumpNametables(includeDetails));

    [McpServerTool(Name = "dump_tilemap", ReadOnly = true, Destructive = false)]
    [Description("Dumps a complete 32x30 NES nametable and its attribute table from PPU memory.")]
    public static object DumpTilemap(INesDebugSession session, string address = "0x2000")
    {
        var parsed = ParseAddress(address);
        if (!parsed.IsSuccess)
        {
            return new ToolError(parsed.Error!);
        }

        if (!NametableReader.IsBaseAddress(parsed.Value.Address))
        {
            return Error("invalid_tilemap_address", "address must be a nametable base: 0x2000, 0x2400, 0x2800, or 0x2C00.");
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

    private static DebugResult<ushort> ParsePpuRegister(string register)
    {
        if (string.IsNullOrWhiteSpace(register))
        {
            return DebugResult<ushort>.Failure("invalid_ppu_register", "PPU register is required.");
        }

        var normalized = register.Trim().ToUpperInvariant();
        var namedAddress = normalized switch
        {
            "PPUCTRL" => (ushort?)0x2000,
            "PPUMASK" => (ushort?)0x2001,
            "PPUSTATUS" => (ushort?)0x2002,
            "OAMADDR" => (ushort?)0x2003,
            "OAMDATA" => (ushort?)0x2004,
            "PPUSCROLL" => (ushort?)0x2005,
            "PPUADDR" => (ushort?)0x2006,
            "PPUDATA" => (ushort?)0x2007,
            _ => null,
        };
        if (namedAddress.HasValue)
        {
            return DebugResult<ushort>.Success(namedAddress.Value);
        }

        var parsed = NesAddress.Parse(register);
        if (!parsed.IsSuccess || parsed.Value.Address is < 0x2000 or > 0x2007)
        {
            return DebugResult<ushort>.Failure("invalid_ppu_register", "registers must contain names or addresses from $2000-$2007.");
        }

        return DebugResult<ushort>.Success(parsed.Value.Address);
    }

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

    private static DebugResult<bool> ValidateAddressRange(ushort address, int length, int maxLength)
    {
        if (length is < 1)
        {
            return DebugResult<bool>.Failure("invalid_range_length", "Range length must be positive.");
        }

        if (length > maxLength)
        {
            return DebugResult<bool>.Failure("invalid_range_length", $"Range length must be at most {maxLength} bytes.");
        }

        if (address + length > 0x10000)
        {
            return DebugResult<bool>.Failure("invalid_range", "Address range must fit within 0x0000..0xFFFF.");
        }

        return DebugResult<bool>.Success(true);
    }

    private static DebugResult<SafeArtifactPath> ResolveSafeArtifactPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DebugResult<SafeArtifactPath>.Failure("invalid_artifact_path", "Artifact path is required.");
        }

        if (Path.IsPathRooted(path))
        {
            return DebugResult<SafeArtifactPath>.Failure("invalid_artifact_path", "Artifact path must be relative.");
        }

        if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return DebugResult<SafeArtifactPath>.Failure("invalid_artifact_path", "Screen capture artifact path must end in .png.");
        }

        var root = Path.GetFullPath(Environment.CurrentDirectory);
        var fullPath = Path.GetFullPath(path, root);
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && fullPath != root)
        {
            return DebugResult<SafeArtifactPath>.Failure("invalid_artifact_path", "Artifact path must stay within the current working directory.");
        }

        var relative = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return DebugResult<SafeArtifactPath>.Failure("invalid_artifact_path", "Artifact path must stay within the current working directory.");
        }

        return DebugResult<SafeArtifactPath>.Success(new SafeArtifactPath(fullPath, relative));
    }

    private static ScreenCaptureMetadata BuildCaptureMetadata(INesDebugSession session)
    {
        var state = session.GetState();
        var registers = session.ReadRegisters();
        var ppu = session.ReadPpuState();
        return new ScreenCaptureMetadata(
            state.IsSuccess ? state.Value.Timeline : new TimelineCounters(0, 0),
            registers.IsSuccess ? registers.Value : null,
            ppu.IsSuccess ? ppu.Value : null,
            state.IsSuccess ? state.Value.Title : null,
            state.IsSuccess ? state.Value.Mapper : null);
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

    private static string ButtonName(NesButton button) =>
        button switch
        {
            NesButton.A => "a",
            NesButton.B => "b",
            NesButton.Select => "select",
            NesButton.Start => "start",
            NesButton.Up => "up",
            NesButton.Down => "down",
            NesButton.Left => "left",
            NesButton.Right => "right",
            _ => button.ToString().ToLowerInvariant(),
        };

    private static object ToToolResult<T>(DebugResult<T> result) => result.IsSuccess ? result.Value! : new ToolError(result.Error!);

    private static ToolError Error(string code, string message) => new(new DebugError(code, message));

    private sealed record SafeArtifactPath(string FullPath, string RelativePath);
}
