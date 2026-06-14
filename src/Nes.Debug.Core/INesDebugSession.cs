namespace Nes.Debug.Core;

public interface INesDebugSession
{
    DebugResult<LoadRomResult> LoadRom(string path);

    DebugResult<ResetResult> Reset();

    DebugResult<StepInstructionResult> StepInstruction(int count);

    DebugResult<RunFrameResult> RunFrame(int count);

    DebugResult<ControllerStateResult> SetController(IReadOnlyList<NesButton> pressedButtons);

    DebugResult<PressButtonsResult> PressButtons(IReadOnlyList<NesButton> pressedButtons, int frameCount);

    DebugResult<ContinueResult> ContinueUntilBreak(int maxInstructions);

    DebugResult<BreakpointSetResult> SetBreakpoint(ushort address);

    DebugResult<ClearBreakpointResult> ClearBreakpoint(string breakpointId);

    DebugResult<ListBreakpointsResult> ListBreakpoints();

    DebugResult<SessionStateResult> GetState();

    DebugResult<NesCpuRegisters> ReadRegisters();

    DebugResult<MemoryReadResult> ReadMemory(ushort address, int length);

    DebugResult<WriteMemoryResult> WriteMemory(ushort address, IReadOnlyList<byte> bytes);

    DebugResult<DisassembleResult> Disassemble(ushort address, int instructionCount);

    DebugResult<ScreenCaptureResult> CaptureScreen();
}
