namespace Nes.Debug.Core;

public sealed class SynchronizedNesDebugSession(INesDebugSession inner) : INesDebugSession, IDisposable
{
    private readonly object gate = new();

    public DebugResult<LoadRomResult> LoadRom(string path)
    {
        lock (gate) { return inner.LoadRom(path); }
    }

    public DebugResult<ResetResult> Reset()
    {
        lock (gate) { return inner.Reset(); }
    }

    public DebugResult<StepInstructionResult> StepInstruction(int count)
    {
        lock (gate) { return inner.StepInstruction(count); }
    }

    public DebugResult<RunFrameResult> RunFrame(int count)
    {
        lock (gate) { return inner.RunFrame(count); }
    }

    public DebugResult<ControllerStateResult> SetController(IReadOnlyList<NesButton> pressedButtons)
    {
        lock (gate) { return inner.SetController(pressedButtons); }
    }

    public DebugResult<PressButtonsResult> PressButtons(IReadOnlyList<NesButton> pressedButtons, int frameCount)
    {
        lock (gate) { return inner.PressButtons(pressedButtons, frameCount); }
    }

    public DebugResult<ContinueResult> ContinueUntilBreak(int maxInstructions)
    {
        lock (gate) { return inner.ContinueUntilBreak(maxInstructions); }
    }

    public DebugResult<BreakpointSetResult> SetBreakpoint(ushort address)
    {
        lock (gate) { return inner.SetBreakpoint(address); }
    }

    public DebugResult<ClearBreakpointResult> ClearBreakpoint(string breakpointId)
    {
        lock (gate) { return inner.ClearBreakpoint(breakpointId); }
    }

    public DebugResult<ListBreakpointsResult> ListBreakpoints()
    {
        lock (gate) { return inner.ListBreakpoints(); }
    }

    public DebugResult<SessionStateResult> GetState()
    {
        lock (gate) { return inner.GetState(); }
    }

    public DebugResult<NesCpuRegisters> ReadRegisters()
    {
        lock (gate) { return inner.ReadRegisters(); }
    }

    public DebugResult<MemoryReadResult> ReadMemory(ushort address, int length)
    {
        lock (gate) { return inner.ReadMemory(address, length); }
    }

    public DebugResult<WriteMemoryResult> WriteMemory(ushort address, IReadOnlyList<byte> bytes)
    {
        lock (gate) { return inner.WriteMemory(address, bytes); }
    }

    public DebugResult<DisassembleResult> Disassemble(ushort address, int instructionCount)
    {
        lock (gate) { return inner.Disassemble(address, instructionCount); }
    }

    public DebugResult<ScreenCaptureResult> CaptureScreen()
    {
        lock (gate) { return inner.CaptureScreen(); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            (inner as IDisposable)?.Dispose();
        }
    }
}
