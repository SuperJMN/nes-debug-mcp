using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nes.Debug.Core;
using Nes.Debug.Mcp;

namespace Nes.Debug.Tests;

public sealed class McpToolValidationTests
{
    [Fact]
    public void Tool_surface_includes_gameboy_compatible_debug_tools()
    {
        string[] expected =
        [
            "load_rom",
            "save_state",
            "load_state",
            "reset",
            "step_instruction",
            "run_frame",
            "set_joypad",
            "press_buttons",
            "continue_until_break",
            "step_over",
            "step_out",
            "run_until_condition",
            "set_breakpoint",
            "clear_breakpoint",
            "list_breakpoints",
            "set_watchpoint",
            "set_watchpoint_range",
            "clear_watchpoint",
            "list_watchpoints",
            "get_state",
            "read_registers",
            "read_memory",
            "write_memory",
            "disassemble",
            "load_symbols",
            "resolve_symbol",
            "read_symbol",
            "dump_oam",
            "read_ppu_state",
            "capture_screen",
            "find_last_writer",
            "find_last_writers",
            "trace_until_write",
            "trace_until_write_range",
            "read_screen_region",
            "run_input_timeline",
            "dump_tilemap",
            "dump_tileset",
        ];

        var actual = typeof(NesDebugTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var toolName in expected)
        {
            Assert.Contains(toolName, actual);
        }
    }

    [Fact]
    public void Read_memory_rejects_non_positive_length_without_calling_session()
    {
        var session = new FakeDebugSession();

        var result = NesDebugTools.ReadMemory(session, "0x8000", 0);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_length", error.Error.Code);
        Assert.False(session.ReadMemoryCalled);
    }

    [Fact]
    public void Read_memory_returns_session_payload_for_valid_input()
    {
        var session = new FakeDebugSession
        {
            ReadMemoryResult = DebugResult<MemoryReadResult>.Success(
                new MemoryReadResult("0x8000", "A9 42", [0xA9, 0x42], ".B")),
        };

        var result = NesDebugTools.ReadMemory(session, "0x8000", 2);

        var payload = Assert.IsType<MemoryReadResult>(result);
        Assert.True(session.ReadMemoryCalled);
        Assert.Equal((ushort)0x8000, session.LastReadAddress);
        Assert.Equal(2, session.LastReadLength);
        Assert.Equal("A9 42", payload.BytesHex);
    }

    [Fact]
    public void Set_controller_rejects_unknown_buttons_without_calling_session()
    {
        var session = new FakeDebugSession();

        var result = NesDebugTools.SetController(session, ["a", "jump"]);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_button", error.Error.Code);
        Assert.False(session.SetControllerCalled);
    }

    [Fact]
    public void Set_controller_sends_normalized_button_set_to_session()
    {
        var session = new FakeDebugSession
        {
            SetControllerResult = DebugResult<ControllerStateResult>.Success(
                new ControllerStateResult(true, false, false, false, false, false, false, true, ["a", "right"])),
        };

        var result = NesDebugTools.SetController(session, ["RIGHT", "a", "right"]);

        var payload = Assert.IsType<ControllerStateResult>(result);
        Assert.True(session.SetControllerCalled);
        Assert.Equal([NesButton.A, NesButton.Right], session.LastButtons);
        Assert.Equal(["a", "right"], payload.Pressed);
    }

    [Fact]
    public void Set_joypad_alias_sends_normalized_button_set_to_session()
    {
        var session = new FakeDebugSession
        {
            SetControllerResult = DebugResult<ControllerStateResult>.Success(
                new ControllerStateResult(false, true, false, false, true, false, false, false, ["b", "up"])),
        };

        var result = NesDebugTools.SetJoypad(session, ["UP", "b", "up"]);

        var payload = Assert.IsType<ControllerStateResult>(result);
        Assert.True(session.SetControllerCalled);
        Assert.Equal([NesButton.B, NesButton.Up], session.LastButtons);
        Assert.Equal(["b", "up"], payload.Pressed);
    }

    [Fact]
    public void Press_buttons_validates_frame_count_before_calling_session()
    {
        var session = new FakeDebugSession();

        var result = NesDebugTools.PressButtons(session, ["a"], 0);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_frame_count", error.Error.Code);
        Assert.False(session.PressButtonsCalled);
    }

    [Fact]
    public void Press_buttons_sends_normalized_button_set_and_frame_count_to_session()
    {
        var registers = EmptyRegisters();
        var session = new FakeDebugSession
        {
            PressButtonsResult = DebugResult<PressButtonsResult>.Success(
                new PressButtonsResult(4, new ControllerStateResult(false, false, false, false, false, false, false, false, []), registers)),
        };

        var result = NesDebugTools.PressButtons(session, ["left", "b"], 4);

        var payload = Assert.IsType<PressButtonsResult>(result);
        Assert.True(session.PressButtonsCalled);
        Assert.Equal([NesButton.B, NesButton.Left], session.LastPressedButtons);
        Assert.Equal(4, session.LastPressFrameCount);
        Assert.Equal(4, payload.FramesRun);
    }

    [Fact]
    public void Set_breakpoint_rejects_invalid_condition_without_calling_session()
    {
        var session = new FakeDebugSession();

        var result = NesDebugTools.SetBreakpoint(session, "0x8000", "A = 1");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_breakpoint_condition", error.Error.Code);
        Assert.False(session.SetBreakpointCalled);
    }

    [Fact]
    public void Set_breakpoint_sends_optional_condition_to_session()
    {
        var session = new FakeDebugSession
        {
            SetBreakpointResult = DebugResult<BreakpointSetResult>.Success(new BreakpointSetResult("bp-1", "0x8000", true)),
        };

        var result = NesDebugTools.SetBreakpoint(session, "0x8000", "A == 1");

        var payload = Assert.IsType<BreakpointSetResult>(result);
        Assert.True(session.SetBreakpointCalled);
        Assert.Equal((ushort)0x8000, session.LastBreakpointAddress);
        Assert.Equal("A == 1", session.LastBreakpointCondition);
        Assert.Equal("bp-1", payload.BreakpointId);
    }

    [Theory]
    [InlineData("read", WatchpointMode.Read)]
    [InlineData("ACCESS", WatchpointMode.Access)]
    public void Set_watchpoint_accepts_supported_modes(string mode, WatchpointMode expectedMode)
    {
        var session = new FakeDebugSession
        {
            SetWatchpointResult = DebugResult<WatchpointSetResult>.Success(
                new WatchpointSetResult("wp-1", "0x0002", mode.ToLowerInvariant(), true)),
        };

        var result = NesDebugTools.SetWatchpoint(session, "0x0002", mode);

        var payload = Assert.IsType<WatchpointSetResult>(result);
        Assert.True(session.SetWatchpointCalled);
        Assert.Equal((ushort)0x0002, session.LastWatchpointAddress);
        Assert.Equal(expectedMode, session.LastWatchpointMode);
        Assert.Equal(mode.ToLowerInvariant(), payload.Mode);
    }

    [Fact]
    public void Set_watchpoint_rejects_invalid_mode_without_calling_session()
    {
        var session = new FakeDebugSession();

        var result = NesDebugTools.SetWatchpoint(session, "0x0002", "execute");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_watchpoint_mode", error.Error.Code);
        Assert.False(session.SetWatchpointCalled);
    }

    [Fact]
    public void Set_watchpoint_range_validates_length_before_calling_session()
    {
        var session = new FakeDebugSession();

        var result = NesDebugTools.SetWatchpointRange(session, "0x0002", 0, "write");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_range_length", error.Error.Code);
        Assert.False(session.SetWatchpointRangeCalled);
    }

    [Fact]
    public void Set_watchpoint_range_passes_range_and_mode_to_session()
    {
        var session = new FakeDebugSession
        {
            SetWatchpointRangeResult = DebugResult<WatchpointSetResult>.Success(
                new WatchpointSetResult("wp-1", "0x0002", "write", true, 4)),
        };

        var result = NesDebugTools.SetWatchpointRange(session, "0x0002", 4, "write");

        var payload = Assert.IsType<WatchpointSetResult>(result);
        Assert.True(session.SetWatchpointRangeCalled);
        Assert.Equal((ushort)0x0002, session.LastWatchpointAddress);
        Assert.Equal(4, session.LastWatchpointLength);
        Assert.Equal(WatchpointMode.Write, session.LastWatchpointMode);
        Assert.Equal(4, payload.Length);
    }

    [Fact]
    public void Run_until_condition_rejects_invalid_condition_without_calling_session()
    {
        var session = new FakeDebugSession();

        var result = NesDebugTools.RunUntilCondition(session, "A => 0x42", 100, 2);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_condition", error.Error.Code);
        Assert.False(session.RunUntilConditionCalled);
    }

    [Fact]
    public void Trace_until_write_range_validates_address_range_before_calling_session()
    {
        var session = new FakeDebugSession();

        var result = NesDebugTools.TraceUntilWriteRange(session, "0xFFF0", 32, 1000);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_range", error.Error.Code);
        Assert.False(session.TraceUntilWriteRangeCalled);
    }

    [Fact]
    public void Read_screen_region_validates_bounds_before_calling_session()
    {
        var session = new FakeDebugSession();

        var result = NesDebugTools.ReadScreenRegion(session, 255, 0, 2, 1);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_screen_region", error.Error.Code);
        Assert.False(session.ReadScreenRegionCalled);
    }

    [Fact]
    public void Run_input_timeline_passes_normalized_steps_to_session_atomically()
    {
        var session = new FakeDebugSession
        {
            RunInputTimelineResult = DebugResult<InputTimelineResult>.Success(
                new InputTimelineResult(
                    4,
                    new ControllerStateResult(false, false, false, false, false, false, false, false, []),
                    [
                        new InputTimelineStepResult(0, 4, 4, ["a", "right"], EmptyRegisters(), null, null, null, null, null, null),
                    ],
                    new TimelineCounters(4, 0, 0))),
        };

        var result = NesDebugTools.RunInputTimeline(
            session,
            [
                new InputTimelineStep { Frames = 4, Buttons = ["RIGHT", "a", "right"], ReadRegisters = true },
            ]);

        var payload = Assert.IsType<InputTimelineResult>(result);
        Assert.Equal(4, payload.FramesRun);
        Assert.True(session.RunInputTimelineCalled);
        Assert.Equal(["a", "right"], session.LastInputTimelineSteps[0].Buttons);
        var step = Assert.Single(payload.Steps);
        Assert.NotNull(step.Registers);
    }

    [Fact]
    public void Step_over_validates_instruction_limit_before_calling_session()
    {
        var session = new FakeDebugSession();

        var result = NesDebugTools.StepOver(session, 0);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_max_instructions", error.Error.Code);
        Assert.False(session.StepOverCalled);
    }

    [Fact]
    public void Save_state_rejects_blank_path_without_calling_session()
    {
        var session = new FakeDebugSession();

        var result = NesDebugTools.SaveState(session, " ");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_path", error.Error.Code);
        Assert.False(session.SaveStateCalled);
    }

    [Fact]
    public void Read_symbol_validates_length_before_calling_session()
    {
        var session = new FakeDebugSession();

        var result = NesDebugTools.ReadSymbol(session, "PlayerX", 0);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_length", error.Error.Code);
        Assert.False(session.ReadSymbolCalled);
    }

    [Fact]
    public void Dump_tileset_rejects_ranges_outside_pattern_tables()
    {
        var session = new FakeDebugSession();

        var result = NesDebugTools.DumpTileset(session, "0x1FF0", 2);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_tileset_range", error.Error.Code);
        Assert.False(session.DumpTilesetCalled);
    }

    [Fact]
    public void Capture_screen_can_save_png_artifact_with_metadata()
    {
        var previousDirectory = Environment.CurrentDirectory;
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"nes-mcp-artifact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        Environment.CurrentDirectory = tempDirectory;
        var pngBytes = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' };
        var session = new FakeDebugSession
        {
            CaptureScreenResult = DebugResult<ScreenCaptureResult>.Success(
                new ScreenCaptureResult(256, 240, "image/png", pngBytes)),
            GetStateResult = DebugResult<SessionStateResult>.Success(
                new SessionStateResult(true, "MCPTEST", 0, "0x8000", 3, new TimelineCounters(3, 12, 2))),
            ReadRegistersResult = DebugResult<NesCpuRegisters>.Success(EmptyRegisters()),
            ReadPpuStateResult = DebugResult<PpuStateResult>.Success(EmptyPpuState()),
        };

        try
        {
            var result = NesDebugTools.CaptureScreen(session, "artifacts/frame.png", includeMetadata: true);

            var payload = Assert.IsType<ScreenCaptureArtifactResult>(result);
            Assert.True(session.CaptureScreenCalled);
            Assert.Equal("artifacts/frame.png", payload.Path);
            Assert.Equal(256, payload.Width);
            Assert.True(File.Exists(Path.Combine(tempDirectory, "artifacts", "frame.png")));
            Assert.Equal(pngBytes, File.ReadAllBytes(Path.Combine(tempDirectory, "artifacts", "frame.png")));
            Assert.NotNull(payload.Metadata);
            Assert.Equal("MCPTEST", payload.Metadata.RomTitle);
            Assert.Equal(3UL, payload.Metadata.Timeline.Frames);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static NesCpuRegisters EmptyRegisters() =>
        new(
            "0x00",
            "0x00",
            "0x00",
            "0xFD",
            "0x8000",
            "0x24",
            false,
            false,
            true,
            false,
            false,
            false,
            0);

    private static PpuStateResult EmptyPpuState() =>
        new(
            "0x00",
            "0x00",
            "0x00",
            "0x00",
            "0x0000",
            "0x0000",
            0,
            0,
            false,
            false,
            false,
            false,
            0);

    private sealed class FakeDebugSession : INesDebugSession
    {
        public bool ReadMemoryCalled { get; private set; }
        public bool SetControllerCalled { get; private set; }
        public bool PressButtonsCalled { get; private set; }
        public bool SetBreakpointCalled { get; private set; }
        public bool SetWatchpointCalled { get; private set; }
        public bool SetWatchpointRangeCalled { get; private set; }
        public bool RunUntilConditionCalled { get; private set; }
        public bool TraceUntilWriteRangeCalled { get; private set; }
        public bool ReadScreenRegionCalled { get; private set; }
        public bool RunInputTimelineCalled { get; private set; }
        public bool StepOverCalled { get; private set; }
        public bool SaveStateCalled { get; private set; }
        public bool CaptureScreenCalled { get; private set; }
        public bool ReadSymbolCalled { get; private set; }
        public bool DumpTilesetCalled { get; private set; }
        public ushort LastReadAddress { get; private set; }
        public int LastReadLength { get; private set; }
        public IReadOnlyList<NesButton> LastButtons { get; private set; } = [];
        public IReadOnlyList<NesButton> LastPressedButtons { get; private set; } = [];
        public IReadOnlyList<InputTimelineStep> LastInputTimelineSteps { get; private set; } = [];
        public int LastPressFrameCount { get; private set; }
        public ushort LastBreakpointAddress { get; private set; }
        public string? LastBreakpointCondition { get; private set; }
        public ushort LastWatchpointAddress { get; private set; }
        public int LastWatchpointLength { get; private set; }
        public WatchpointMode LastWatchpointMode { get; private set; }
        public DebugResult<MemoryReadResult> ReadMemoryResult { get; init; } =
            DebugResult<MemoryReadResult>.Failure("not_configured", "Fake session result was not configured.");
        public DebugResult<ControllerStateResult> SetControllerResult { get; init; } =
            DebugResult<ControllerStateResult>.Failure("not_configured", "Fake session result was not configured.");
        public DebugResult<PressButtonsResult> PressButtonsResult { get; init; } =
            DebugResult<PressButtonsResult>.Failure("not_configured", "Fake session result was not configured.");
        public DebugResult<BreakpointSetResult> SetBreakpointResult { get; init; } =
            DebugResult<BreakpointSetResult>.Failure("not_configured", "Fake session result was not configured.");
        public DebugResult<WatchpointSetResult> SetWatchpointResult { get; init; } =
            DebugResult<WatchpointSetResult>.Failure("not_configured", "Fake session result was not configured.");
        public DebugResult<WatchpointSetResult> SetWatchpointRangeResult { get; init; } =
            DebugResult<WatchpointSetResult>.Failure("not_configured", "Fake session result was not configured.");
        public DebugResult<InputTimelineResult> RunInputTimelineResult { get; init; } =
            DebugResult<InputTimelineResult>.Failure("not_configured", "Fake session result was not configured.");
        public DebugResult<ScreenCaptureResult> CaptureScreenResult { get; init; } =
            DebugResult<ScreenCaptureResult>.Failure("not_configured", "Fake session result was not configured.");
        public DebugResult<SessionStateResult> GetStateResult { get; init; } =
            DebugResult<SessionStateResult>.Failure("not_configured", "Fake session result was not configured.");
        public DebugResult<NesCpuRegisters> ReadRegistersResult { get; init; } =
            DebugResult<NesCpuRegisters>.Failure("not_configured", "Fake session result was not configured.");
        public DebugResult<PpuStateResult> ReadPpuStateResult { get; init; } =
            DebugResult<PpuStateResult>.Failure("not_configured", "Fake session result was not configured.");

        public DebugResult<LoadRomResult> LoadRom(string path) => throw new NotSupportedException();

        public DebugResult<SaveStateResult> SaveState(string path)
        {
            SaveStateCalled = true;
            throw new NotSupportedException();
        }

        public DebugResult<LoadStateResult> LoadState(string path) => throw new NotSupportedException();

        public DebugResult<ResetResult> Reset() => throw new NotSupportedException();

        public DebugResult<StepInstructionResult> StepInstruction(int count) => throw new NotSupportedException();

        public DebugResult<RunFrameResult> RunFrame(int count) => throw new NotSupportedException();

        public DebugResult<ControllerStateResult> SetController(IReadOnlyList<NesButton> pressedButtons)
        {
            SetControllerCalled = true;
            LastButtons = pressedButtons.ToArray();
            return SetControllerResult;
        }

        public DebugResult<PressButtonsResult> PressButtons(IReadOnlyList<NesButton> pressedButtons, int frameCount)
        {
            PressButtonsCalled = true;
            LastPressedButtons = pressedButtons.ToArray();
            LastPressFrameCount = frameCount;
            return PressButtonsResult;
        }

        public DebugResult<ContinueResult> ContinueUntilBreak(int maxInstructions) => throw new NotSupportedException();

        public DebugResult<ContinueResult> StepOver(int maxInstructions)
        {
            StepOverCalled = true;
            throw new NotSupportedException();
        }

        public DebugResult<ContinueResult> StepOut(int maxInstructions) => throw new NotSupportedException();

        public DebugResult<BreakpointSetResult> SetBreakpoint(ushort address, string? condition)
        {
            SetBreakpointCalled = true;
            LastBreakpointAddress = address;
            LastBreakpointCondition = condition;
            return SetBreakpointResult;
        }

        public DebugResult<ClearBreakpointResult> ClearBreakpoint(string breakpointId) => throw new NotSupportedException();

        public DebugResult<ListBreakpointsResult> ListBreakpoints() => throw new NotSupportedException();

        public DebugResult<WatchpointSetResult> SetWatchpoint(ushort address, WatchpointMode mode)
        {
            SetWatchpointCalled = true;
            LastWatchpointAddress = address;
            LastWatchpointMode = mode;
            return SetWatchpointResult;
        }

        public DebugResult<WatchpointSetResult> SetWatchpointRange(ushort address, int length, WatchpointMode mode)
        {
            SetWatchpointRangeCalled = true;
            LastWatchpointAddress = address;
            LastWatchpointLength = length;
            LastWatchpointMode = mode;
            return SetWatchpointRangeResult;
        }

        public DebugResult<ClearWatchpointResult> ClearWatchpoint(string watchpointId) => throw new NotSupportedException();

        public DebugResult<ListWatchpointsResult> ListWatchpoints() => throw new NotSupportedException();

        public DebugResult<RunUntilConditionResult> RunUntilCondition(string condition, int maxInstructions, int maxFrames)
        {
            RunUntilConditionCalled = true;
            throw new NotSupportedException();
        }

        public DebugResult<SessionStateResult> GetState() => GetStateResult;

        public DebugResult<NesCpuRegisters> ReadRegisters() => ReadRegistersResult;

        public DebugResult<MemoryReadResult> ReadMemory(ushort address, int length)
        {
            ReadMemoryCalled = true;
            LastReadAddress = address;
            LastReadLength = length;
            return ReadMemoryResult;
        }

        public DebugResult<WriteMemoryResult> WriteMemory(ushort address, IReadOnlyList<byte> bytes) =>
            throw new NotSupportedException();

        public DebugResult<DisassembleResult> Disassemble(ushort address, int instructionCount) =>
            throw new NotSupportedException();

        public DebugResult<LoadSymbolsResult> LoadSymbols(string path) => throw new NotSupportedException();

        public DebugResult<ResolveSymbolResult> ResolveSymbol(string name) => throw new NotSupportedException();

        public DebugResult<ReadSymbolResult> ReadSymbol(string name, int? length)
        {
            ReadSymbolCalled = true;
            throw new NotSupportedException();
        }

        public DebugResult<OamDumpResult> ReadOam() => throw new NotSupportedException();

        public DebugResult<PpuStateResult> ReadPpuState() => ReadPpuStateResult;

        public DebugResult<ScreenCaptureResult> CaptureScreen()
        {
            CaptureScreenCalled = true;
            return CaptureScreenResult;
        }

        public DebugResult<LastWriterResult> FindLastWriter(ushort address) => throw new NotSupportedException();

        public DebugResult<LastWritersResult> FindLastWriters(ushort address, int length) => throw new NotSupportedException();

        public DebugResult<TraceUntilWriteResult> TraceUntilWrite(ushort address, int maxInstructions) =>
            throw new NotSupportedException();

        public DebugResult<TraceUntilWriteRangeResult> TraceUntilWriteRange(ushort address, int length, int maxInstructions)
        {
            TraceUntilWriteRangeCalled = true;
            throw new NotSupportedException();
        }

        public DebugResult<ScreenRegionResult> ReadScreenRegion(int x, int y, int width, int height, string format)
        {
            ReadScreenRegionCalled = true;
            throw new NotSupportedException();
        }

        public DebugResult<InputTimelineResult> RunInputTimeline(IReadOnlyList<InputTimelineStep> steps)
        {
            RunInputTimelineCalled = true;
            LastInputTimelineSteps = steps.ToArray();
            return RunInputTimelineResult;
        }

        public DebugResult<TilemapDumpResult> DumpTilemap(ushort address) => throw new NotSupportedException();

        public DebugResult<TilesetDumpResult> DumpTileset(ushort address, int tileCount)
        {
            DumpTilesetCalled = true;
            throw new NotSupportedException();
        }
    }
}
