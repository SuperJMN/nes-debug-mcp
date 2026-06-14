using ModelContextProtocol.Protocol;
using Nes.Debug.Core;
using Nes.Debug.Mcp;

namespace Nes.Debug.Tests;

public sealed class McpToolValidationTests
{
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

    private sealed class FakeDebugSession : INesDebugSession
    {
        public bool ReadMemoryCalled { get; private set; }
        public bool SetControllerCalled { get; private set; }
        public ushort LastReadAddress { get; private set; }
        public int LastReadLength { get; private set; }
        public IReadOnlyList<NesButton> LastButtons { get; private set; } = [];
        public DebugResult<MemoryReadResult> ReadMemoryResult { get; init; } =
            DebugResult<MemoryReadResult>.Failure("not_configured", "Fake session result was not configured.");
        public DebugResult<ControllerStateResult> SetControllerResult { get; init; } =
            DebugResult<ControllerStateResult>.Failure("not_configured", "Fake session result was not configured.");

        public DebugResult<LoadRomResult> LoadRom(string path) => throw new NotSupportedException();

        public DebugResult<ResetResult> Reset() => throw new NotSupportedException();

        public DebugResult<StepInstructionResult> StepInstruction(int count) => throw new NotSupportedException();

        public DebugResult<RunFrameResult> RunFrame(int count) => throw new NotSupportedException();

        public DebugResult<ControllerStateResult> SetController(IReadOnlyList<NesButton> pressedButtons)
        {
            SetControllerCalled = true;
            LastButtons = pressedButtons.ToArray();
            return SetControllerResult;
        }

        public DebugResult<PressButtonsResult> PressButtons(IReadOnlyList<NesButton> pressedButtons, int frameCount) =>
            throw new NotSupportedException();

        public DebugResult<ContinueResult> ContinueUntilBreak(int maxInstructions) => throw new NotSupportedException();

        public DebugResult<BreakpointSetResult> SetBreakpoint(ushort address) => throw new NotSupportedException();

        public DebugResult<ClearBreakpointResult> ClearBreakpoint(string breakpointId) => throw new NotSupportedException();

        public DebugResult<ListBreakpointsResult> ListBreakpoints() => throw new NotSupportedException();

        public DebugResult<SessionStateResult> GetState() => throw new NotSupportedException();

        public DebugResult<NesCpuRegisters> ReadRegisters() => throw new NotSupportedException();

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

        public DebugResult<ScreenCaptureResult> CaptureScreen() => throw new NotSupportedException();
    }
}
