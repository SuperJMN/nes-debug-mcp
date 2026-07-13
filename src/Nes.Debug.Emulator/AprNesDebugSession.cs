using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AprNes;
using Nes.Debug.Core;
using Nes.Debug.Symbols;

namespace Nes.Debug.Emulator;

public sealed class AprNesDebugSession : INesDebugSession, IPaletteIndexFrameSource, IDisposable
{
    private const int ScreenWidth = 256;
    private const int ScreenHeight = 240;
    private const uint StateMagicV1 = 0x31534E41; // "ANS1"

    private static readonly IReadOnlyDictionary<NesButton, byte> ButtonMap =
        new Dictionary<NesButton, byte>
        {
            [NesButton.A] = 0,
            [NesButton.B] = 1,
            [NesButton.Select] = 2,
            [NesButton.Start] = 3,
            [NesButton.Up] = 4,
            [NesButton.Down] = 5,
            [NesButton.Left] = 6,
            [NesButton.Right] = 7,
        };

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

    private readonly BreakpointCollection breakpoints = new();
    private readonly WatchpointCollection watchpoints = new();
    private readonly SymbolService symbols = new();
    private readonly Dictionary<int, WriteRecord> lastWriters = [];
    private readonly HashSet<NesButton> pressedButtons = [];
    private byte[] romBytes = [];
    private string? romTitle;
    private int? mapper;
    private int prgRomBanks;
    private int chrRomBanks;
    private bool romLoaded;
    private bool trackWrites;
    private bool trackReads;
    private WatchHit? watchHit;
    private bool disposed;
    private ulong totalInstructions;
    private int traceAddress = -1;
    private int traceLength;
    private bool traceHit;
    private ushort traceHitAddress;
    private ushort traceHitPc;
    private byte traceHitValue;
    private ActivePpuRegisterTrace? activePpuRegisterTrace;

    public DebugResult<LoadRomResult> LoadRom(string path)
    {
        if (disposed)
        {
            return DebugResult<LoadRomResult>.Failure("session_disposed", "The debug session has been disposed.");
        }

        if (!File.Exists(path))
        {
            return DebugResult<LoadRomResult>.Failure("rom_not_found", $"ROM was not found: {path}");
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var header = ParseHeader(bytes);
            if (!header.IsSuccess)
            {
                return DebugResult<LoadRomResult>.Failure(header.Error!.Code, header.Error.Message);
            }

            if (!NesCore.DebugLoad(bytes))
            {
                romLoaded = false;
                return DebugResult<LoadRomResult>.Failure("load_rom_failed", "AprNes rejected the ROM.");
            }

            romBytes = bytes;
            romTitle = Path.GetFileNameWithoutExtension(path);
            mapper = header.Value.Mapper;
            prgRomBanks = header.Value.PrgRomBanks;
            chrRomBanks = header.Value.ChrRomBanks;
            romLoaded = true;
            totalInstructions = 0;
            pressedButtons.Clear();
            breakpoints.ClearAll();
            watchpoints.ClearAll();
            InitializeDebugTracking();

            return DebugResult<LoadRomResult>.Success(new LoadRomResult(true, romTitle, mapper.Value, prgRomBanks, chrRomBanks));
        }
        catch (Exception ex)
        {
            romLoaded = false;
            return DebugResult<LoadRomResult>.Failure("load_rom_failed", ex.Message);
        }
    }

    public DebugResult<SaveStateResult> SaveState(string path)
    {
        if (!romLoaded)
        {
            return NoRom<SaveStateResult>();
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream);
            writer.Write(StateMagicV1);
            writer.Write(totalInstructions);
            NesCore.DebugSaveState(writer);

            return DebugResult<SaveStateResult>.Success(new SaveStateResult(true, path));
        }
        catch (Exception ex)
        {
            return DebugResult<SaveStateResult>.Failure("save_state_failed", ex.Message);
        }
    }

    public DebugResult<LoadStateResult> LoadState(string path)
    {
        if (!romLoaded)
        {
            return NoRom<LoadStateResult>();
        }

        if (!File.Exists(path))
        {
            return DebugResult<LoadStateResult>.Failure("state_not_found", $"Save state was not found: {path}");
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(stream);
            var magic = reader.ReadUInt32();
            if (magic != StateMagicV1)
            {
                return DebugResult<LoadStateResult>.Failure("invalid_state", "The file is not an AprNes save state.");
            }

            totalInstructions = reader.ReadUInt64();
            NesCore.DebugLoadState(reader);

            return DebugResult<LoadStateResult>.Success(new LoadStateResult(true, path));
        }
        catch (Exception ex)
        {
            return DebugResult<LoadStateResult>.Failure("load_state_failed", ex.Message);
        }
    }

    public DebugResult<ResetResult> Reset()
    {
        if (!romLoaded)
        {
            return NoRom<ResetResult>();
        }

        try
        {
            if (!NesCore.DebugLoad(romBytes))
            {
                return DebugResult<ResetResult>.Failure("reset_failed", "AprNes rejected the previously loaded ROM.");
            }

            totalInstructions = 0;
            pressedButtons.Clear();
            InitializeDebugTracking();
            return DebugResult<ResetResult>.Success(new ResetResult(true));
        }
        catch (Exception ex)
        {
            return DebugResult<ResetResult>.Failure("reset_failed", ex.Message);
        }
    }

    public DebugResult<StepInstructionResult> StepInstruction(int count)
    {
        if (!romLoaded)
        {
            return NoRom<StepInstructionResult>();
        }

        var before = NesCore.DebugReadRegisters();
        var instructions = Disassemble(before.Pc, Math.Min(count, 16));

        try
        {
            for (var i = 0; i < count; i++)
            {
                StepMachineInstruction();
            }
        }
        catch (Exception ex)
        {
            return DebugResult<StepInstructionResult>.Failure("step_failed", ex.Message);
        }

        var registers = ToRegisters(NesCore.DebugReadRegisters());
        var disassembly = instructions.IsSuccess
            ? string.Join('\n', instructions.Value.Instructions.Select(instruction => $"{instruction.Address}: {instruction.Text}"))
            : "";

        return DebugResult<StepInstructionResult>.Success(
            new StepInstructionResult(Hex.FormatWord(before.Pc), registers.Pc, registers, disassembly, count, GetTimeline()));
    }

    public DebugResult<RunFrameResult> RunFrame(int count) => RunFrame(count, stopAtBreakpoint: false);

    private DebugResult<RunFrameResult> RunFrame(int count, bool stopAtBreakpoint)
    {
        if (!romLoaded)
        {
            return NoRom<RunFrameResult>();
        }

        try
        {
            var startFrame = NesCore.frame_count;
            var instructionLimit = (long)count * PpuRegisterTracing.MaxInstructionsPerFrame;
            long instructionsRun = 0;
            while (NesCore.frame_count - startFrame < count)
            {
                if (instructionsRun >= instructionLimit)
                {
                    return DebugResult<RunFrameResult>.Failure(
                        "run_frame_instruction_limit",
                        $"Frame execution exceeded {PpuRegisterTracing.MaxInstructionsPerFrame} instructions per requested frame.");
                }

                if (stopAtBreakpoint)
                {
                    var registers = ToRegisters(NesCore.DebugReadRegisters());
                    var breakpoint = IsBreakpointHit(ParseWord(registers.Pc), registers);
                    if (!breakpoint.IsSuccess)
                    {
                        return DebugResult<RunFrameResult>.Failure(breakpoint.Error!.Code, breakpoint.Error.Message);
                    }

                    if (breakpoint.Value)
                    {
                        return DebugResult<RunFrameResult>.Success(
                            new RunFrameResult(
                                Math.Max(0, NesCore.frame_count - startFrame),
                                NesCore.frame_count,
                                registers,
                                true,
                                GetTimeline()));
                    }
                }

                StepMachineInstruction();
                instructionsRun++;
            }

            return DebugResult<RunFrameResult>.Success(
                new RunFrameResult(
                    Math.Max(0, NesCore.frame_count - startFrame),
                    NesCore.frame_count,
                    ToRegisters(NesCore.DebugReadRegisters()),
                    false,
                    GetTimeline()));
        }
        catch (Exception ex)
        {
            return DebugResult<RunFrameResult>.Failure("run_frame_failed", ex.Message);
        }
    }

    public DebugResult<ControllerStateResult> SetController(IReadOnlyList<NesButton> buttons)
    {
        if (!romLoaded)
        {
            return NoRom<ControllerStateResult>();
        }

        pressedButtons.Clear();
        foreach (var button in buttons)
        {
            pressedButtons.Add(button);
        }

        foreach (var button in CanonicalButtons)
        {
            NesCore.P1_ButtonUnPress(ButtonMap[button]);
        }

        foreach (var button in pressedButtons)
        {
            NesCore.P1_ButtonPress(ButtonMap[button]);
        }

        return DebugResult<ControllerStateResult>.Success(ToControllerState());
    }

    public DebugResult<PressButtonsResult> PressButtons(IReadOnlyList<NesButton> pressedButtons, int frameCount)
    {
        var pressed = SetController(pressedButtons);
        if (!pressed.IsSuccess)
        {
            return DebugResult<PressButtonsResult>.Failure(pressed.Error!.Code, pressed.Error.Message);
        }

        var run = RunFrame(frameCount);
        var released = SetController([]);
        if (!run.IsSuccess)
        {
            return DebugResult<PressButtonsResult>.Failure(run.Error!.Code, run.Error.Message);
        }

        if (!released.IsSuccess)
        {
            return DebugResult<PressButtonsResult>.Failure(released.Error!.Code, released.Error.Message);
        }

        return DebugResult<PressButtonsResult>.Success(
            new PressButtonsResult(run.Value.FramesRun, released.Value, run.Value.Registers));
    }

    public DebugResult<ContinueResult> ContinueUntilBreak(int maxInstructions)
    {
        if (!romLoaded)
        {
            return NoRom<ContinueResult>();
        }

        watchHit = null;
        AttachReadObserverIfNeeded();
        try
        {
            for (var i = 0; i < maxInstructions; i++)
            {
                var registers = ToRegisters(NesCore.DebugReadRegisters());
                var pc = ParseWord(registers.Pc);
                var hit = IsBreakpointHit(pc, registers);
                if (!hit.IsSuccess)
                {
                    return DebugResult<ContinueResult>.Failure(hit.Error!.Code, hit.Error.Message);
                }

                if (hit.Value)
                {
                    return Stop("breakpoint", registers, (ulong)i);
                }

                StepMachineInstruction();
                if (watchHit.HasValue)
                {
                    return ContinueStopped("watchpoint", (ulong)i + 1);
                }
            }

            return ContinueStopped("maxInstructions", (ulong)maxInstructions);
        }
        catch (Exception ex)
        {
            return DebugResult<ContinueResult>.Failure("continue_failed", ex.Message);
        }
        finally
        {
            DetachReadObserver();
        }
    }

    public DebugResult<ContinueResult> StepOver(int maxInstructions)
    {
        if (!romLoaded)
        {
            return NoRom<ContinueResult>();
        }

        var start = NesCore.DebugReadRegisters();
        var opcode = NesCore.DebugReadCpu(start.Pc);
        if (opcode != 0x20)
        {
            return StepSingle("step");
        }

        var returnAddress = (ushort)(start.Pc + 3);
        var startSp = start.Sp;
        return StepUntil(
            maxInstructions,
            registers => ParseWord(registers.Pc) == returnAddress && ParseByte(registers.Sp) >= startSp,
            "step_over");
    }

    public DebugResult<ContinueResult> StepOut(int maxInstructions)
    {
        if (!romLoaded)
        {
            return NoRom<ContinueResult>();
        }

        var startSp = NesCore.DebugReadRegisters().Sp;
        return StepUntil(maxInstructions, registers => ParseByte(registers.Sp) > startSp, "step_out");
    }

    public DebugResult<RunUntilConditionResult> RunUntilCondition(string condition, int maxInstructions, int maxFrames)
    {
        if (!romLoaded)
        {
            return NoRom<RunUntilConditionResult>();
        }

        if (!BreakpointCondition.TryParse(condition, out var parsedCondition, out var conditionError) || parsedCondition is null)
        {
            return DebugResult<RunUntilConditionResult>.Failure("invalid_condition", $"Invalid condition: {conditionError ?? "Condition is required."}");
        }

        watchHit = null;
        var startFrames = NesCore.frame_count;
        AttachReadObserverIfNeeded();
        try
        {
            for (var i = 0; i < maxInstructions; i++)
            {
                var before = ToRegisters(NesCore.DebugReadRegisters());
                var conditionResult = parsedCondition.Evaluate(new ConditionContext(this, before));
                if (!conditionResult.IsSuccess)
                {
                    return DebugResult<RunUntilConditionResult>.Failure(conditionResult.Error!.Code, conditionResult.Error.Message);
                }

                if (conditionResult.Value)
                {
                    return StopRunUntilCondition("condition", before, (uint)i, startFrames);
                }

                var hit = IsBreakpointHit(ParseWord(before.Pc), before);
                if (!hit.IsSuccess)
                {
                    return DebugResult<RunUntilConditionResult>.Failure(hit.Error!.Code, hit.Error.Message);
                }

                if (hit.Value)
                {
                    return StopRunUntilCondition("breakpoint", before, (uint)i, startFrames);
                }

                if (NesCore.frame_count - startFrames >= maxFrames)
                {
                    return StopRunUntilCondition("maxFrames", before, (uint)i, startFrames);
                }

                StepMachineInstruction();

                var after = ToRegisters(NesCore.DebugReadRegisters());
                conditionResult = parsedCondition.Evaluate(new ConditionContext(this, after));
                if (!conditionResult.IsSuccess)
                {
                    return DebugResult<RunUntilConditionResult>.Failure(conditionResult.Error!.Code, conditionResult.Error.Message);
                }

                if (conditionResult.Value)
                {
                    return StopRunUntilCondition("condition", after, (uint)i + 1, startFrames);
                }

                if (watchHit.HasValue)
                {
                    return StopRunUntilCondition("watchpoint", after, (uint)i + 1, startFrames);
                }

                if (NesCore.frame_count - startFrames >= maxFrames)
                {
                    return StopRunUntilCondition("maxFrames", after, (uint)i + 1, startFrames);
                }
            }

            return StopRunUntilCondition("maxInstructions", ToRegisters(NesCore.DebugReadRegisters()), (uint)maxInstructions, startFrames);
        }
        catch (Exception ex)
        {
            return DebugResult<RunUntilConditionResult>.Failure("run_until_condition_failed", ex.Message);
        }
        finally
        {
            DetachReadObserver();
        }
    }

    public DebugResult<BreakpointSetResult> SetBreakpoint(ushort address, string? condition)
    {
        if (!BreakpointCondition.TryParse(condition, out var parsedCondition, out var conditionError))
        {
            return DebugResult<BreakpointSetResult>.Failure(
                "invalid_breakpoint_condition",
                $"Invalid breakpoint condition: {conditionError}");
        }

        var info = breakpoints.Set(address, condition, parsedCondition);
        return DebugResult<BreakpointSetResult>.Success(new BreakpointSetResult(info.Id, info.Address, info.Enabled));
    }

    public DebugResult<ClearBreakpointResult> ClearBreakpoint(string breakpointId)
    {
        return breakpoints.Clear(breakpointId)
            ? DebugResult<ClearBreakpointResult>.Success(new ClearBreakpointResult(true))
            : DebugResult<ClearBreakpointResult>.Failure("breakpoint_not_found", $"Breakpoint '{breakpointId}' was not found.");
    }

    public DebugResult<ListBreakpointsResult> ListBreakpoints()
    {
        var entries = breakpoints.All
            .Select(breakpoint => new BreakpointEntry(breakpoint.Id, breakpoint.Address, breakpoint.Enabled, breakpoint.Condition))
            .ToArray();

        return DebugResult<ListBreakpointsResult>.Success(new ListBreakpointsResult(entries));
    }

    public DebugResult<WatchpointSetResult> SetWatchpoint(ushort address, WatchpointMode mode)
    {
        var watchpoint = watchpoints.Set(address, mode);
        return ToWatchpointSetResult(watchpoint);
    }

    public DebugResult<WatchpointSetResult> SetWatchpointRange(ushort address, int length, WatchpointMode mode)
    {
        if (length < 1 || address + length > 0x10000)
        {
            return DebugResult<WatchpointSetResult>.Failure("invalid_range", "Watchpoint range must fit within 0x0000..0xFFFF.");
        }

        var watchpoint = watchpoints.Set(address, length, mode);
        return ToWatchpointSetResult(watchpoint);
    }

    public DebugResult<ClearWatchpointResult> ClearWatchpoint(string watchpointId)
    {
        return watchpoints.Clear(watchpointId)
            ? DebugResult<ClearWatchpointResult>.Success(new ClearWatchpointResult(true))
            : DebugResult<ClearWatchpointResult>.Failure("watchpoint_not_found", $"Watchpoint '{watchpointId}' was not found.");
    }

    public DebugResult<ListWatchpointsResult> ListWatchpoints()
    {
        var entries = watchpoints.All
            .Select(watchpoint => new WatchpointEntry(watchpoint.Id, watchpoint.Address, ToWatchpointModeName(watchpoint.Mode), watchpoint.Enabled, watchpoint.Length))
            .ToArray();

        return DebugResult<ListWatchpointsResult>.Success(new ListWatchpointsResult(entries));
    }

    public DebugResult<SessionStateResult> GetState()
    {
        return DebugResult<SessionStateResult>.Success(
            new SessionStateResult(
                romLoaded,
                romTitle,
                mapper,
                romLoaded ? Hex.FormatWord(NesCore.DebugReadRegisters().Pc) : null,
                NesCore.frame_count,
                GetTimeline()));
    }

    public DebugResult<NesCpuRegisters> ReadRegisters()
    {
        return romLoaded
            ? DebugResult<NesCpuRegisters>.Success(ToRegisters(NesCore.DebugReadRegisters()))
            : NoRom<NesCpuRegisters>();
    }

    public DebugResult<MemoryReadResult> ReadMemory(ushort address, int length)
    {
        if (!romLoaded)
        {
            return NoRom<MemoryReadResult>();
        }

        try
        {
            var bytes = ReadBytes(address, length);
            return DebugResult<MemoryReadResult>.Success(
                new MemoryReadResult(Hex.FormatWord(address), Hex.FormatBytes(bytes), bytes, MemoryFormatter.ToAscii(bytes)));
        }
        catch (Exception ex)
        {
            return DebugResult<MemoryReadResult>.Failure("read_memory_failed", ex.Message);
        }
    }

    public DebugResult<WriteMemoryResult> WriteMemory(ushort address, IReadOnlyList<byte> bytes)
    {
        if (!romLoaded)
        {
            return NoRom<WriteMemoryResult>();
        }

        try
        {
            for (var i = 0; i < bytes.Count; i++)
            {
                NesCore.DebugWriteCpu((ushort)((address + i) & 0xFFFF), bytes[i]);
            }

            return DebugResult<WriteMemoryResult>.Success(new WriteMemoryResult(true, Hex.FormatWord(address), bytes.Count));
        }
        catch (Exception ex)
        {
            return DebugResult<WriteMemoryResult>.Failure("write_memory_failed", ex.Message);
        }
    }

    public DebugResult<DisassembleResult> Disassemble(ushort address, int instructionCount)
    {
        if (!romLoaded)
        {
            return NoRom<DisassembleResult>();
        }

        try
        {
            var instructions = new List<DisassembledInstruction>();
            var pc = address;
            for (var i = 0; i < instructionCount; i++)
            {
                var decoded = DecodeInstruction(pc);
                instructions.Add(decoded);
                pc = (ushort)(pc + decoded.Bytes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            }

            return DebugResult<DisassembleResult>.Success(new DisassembleResult(Hex.FormatWord(address), instructions));
        }
        catch (Exception ex)
        {
            return DebugResult<DisassembleResult>.Failure("disassemble_failed", ex.Message);
        }
    }

    public DebugResult<LoadSymbolsResult> LoadSymbols(string path)
    {
        var loaded = symbols.Load(path);
        return loaded.IsSuccess
            ? DebugResult<LoadSymbolsResult>.Success(new LoadSymbolsResult(true, loaded.Value))
            : DebugResult<LoadSymbolsResult>.Failure(loaded.Error!.Code, loaded.Error.Message);
    }

    public DebugResult<ResolveSymbolResult> ResolveSymbol(string name)
    {
        var resolved = symbols.Resolve(name);
        return resolved.IsSuccess
            ? DebugResult<ResolveSymbolResult>.Success(new ResolveSymbolResult(name, Hex.FormatWord(resolved.Value.Address), resolved.Value.Bank))
            : DebugResult<ResolveSymbolResult>.Failure(resolved.Error!.Code, resolved.Error.Message);
    }

    public DebugResult<ReadSymbolResult> ReadSymbol(string name, int? length)
    {
        var resolved = symbols.Resolve(name);
        if (!resolved.IsSuccess)
        {
            return DebugResult<ReadSymbolResult>.Failure(resolved.Error!.Code, resolved.Error.Message);
        }

        if (!romLoaded)
        {
            return NoRom<ReadSymbolResult>();
        }

        var bytes = ReadBytes(resolved.Value.Address, length ?? 1);
        return DebugResult<ReadSymbolResult>.Success(new ReadSymbolResult(name, Hex.FormatWord(resolved.Value.Address), bytes, Hex.FormatBytes(bytes)));
    }

    public DebugResult<OamDumpResult> ReadOam()
    {
        if (!romLoaded)
        {
            return NoRom<OamDumpResult>();
        }

        var oam = NesCore.DebugReadOam();
        var sprites = Enumerable.Range(0, 64)
            .Select(index =>
            {
                var offset = index * 4;
                var y = oam[offset];
                var x = oam[offset + 3];
                return new OamSprite(index, y, x, Hex.FormatByte(oam[offset + 1]), Hex.FormatByte(oam[offset + 2]), y < 0xEF);
            })
            .ToArray();

        return DebugResult<OamDumpResult>.Success(new OamDumpResult(sprites));
    }

    public DebugResult<PpuStateResult> ReadPpuState()
    {
        if (!romLoaded)
        {
            return NoRom<PpuStateResult>();
        }

        var state = NesCore.DebugReadPpuState();
        return DebugResult<PpuStateResult>.Success(PpuStateBuilder.Build(
            state.PpuCtrl,
            state.PpuMask,
            state.PpuStatus,
            state.OamAddr,
            state.V,
            state.T,
            state.FineX,
            state.WriteToggle,
            state.Scanline,
            state.Cycle,
            state.Nmi,
            (long)Math.Min(state.PpuCycles, long.MaxValue),
            GetTimeline()));
    }

    public DebugResult<ScreenCaptureResult> CaptureScreen()
    {
        if (!romLoaded)
        {
            return NoRom<ScreenCaptureResult>();
        }

        var pixels = NesCore.DebugReadRgbFrame();
        return DebugResult<ScreenCaptureResult>.Success(
            new ScreenCaptureResult(ScreenWidth, ScreenHeight, "image/png", PngEncoder.EncodeRgb24(pixels, ScreenWidth, ScreenHeight)));
    }

    public DebugResult<LastWriterResult> FindLastWriter(ushort address)
    {
        if (!romLoaded)
        {
            return NoRom<LastWriterResult>();
        }

        return lastWriters.TryGetValue(address, out var record)
            ? DebugResult<LastWriterResult>.Success(new LastWriterResult(true, Hex.FormatWord(address), Hex.FormatWord(record.Pc), Hex.FormatByte(record.Value), record.Count))
            : DebugResult<LastWriterResult>.Success(new LastWriterResult(false, Hex.FormatWord(address), null, null, 0));
    }

    public DebugResult<LastWritersResult> FindLastWriters(ushort address, int length)
    {
        if (!romLoaded)
        {
            return NoRom<LastWritersResult>();
        }

        if (length < 1 || address + length > 0x10000)
        {
            return DebugResult<LastWritersResult>.Failure("invalid_range", "Writer range must fit within 0x0000..0xFFFF.");
        }

        var writers = Enumerable.Range(0, length)
            .Select(offset =>
            {
                var current = (ushort)(address + offset);
                return lastWriters.TryGetValue(current, out var record)
                    ? new LastWriterResult(true, Hex.FormatWord(current), Hex.FormatWord(record.Pc), Hex.FormatByte(record.Value), record.Count)
                    : new LastWriterResult(false, Hex.FormatWord(current), null, null, 0);
            })
            .ToArray();

        return DebugResult<LastWritersResult>.Success(new LastWritersResult(writers));
    }

    public DebugResult<TraceUntilWriteResult> TraceUntilWrite(ushort address, int maxInstructions)
    {
        if (!romLoaded)
        {
            return NoRom<TraceUntilWriteResult>();
        }

        traceAddress = address;
        traceLength = 1;
        traceHit = false;
        uint instructionsRun = 0;
        try
        {
            for (var i = 0; i < maxInstructions && !traceHit; i++)
            {
                StepMachineInstruction();
                instructionsRun++;
            }
        }
        finally
        {
            traceAddress = -1;
            traceLength = 0;
        }

        var registers = ToRegisters(NesCore.DebugReadRegisters());
        return DebugResult<TraceUntilWriteResult>.Success(traceHit
            ? new TraceUntilWriteResult(true, "write", Hex.FormatWord(address), Hex.FormatWord(traceHitPc), Hex.FormatByte(traceHitValue), instructionsRun, registers, GetTimeline())
            : new TraceUntilWriteResult(true, "maxInstructions", Hex.FormatWord(address), null, null, instructionsRun, registers, GetTimeline()));
    }

    public DebugResult<TraceUntilWriteRangeResult> TraceUntilWriteRange(ushort address, int length, int maxInstructions)
    {
        if (!romLoaded)
        {
            return NoRom<TraceUntilWriteRangeResult>();
        }

        if (length < 1 || address + length > 0x10000)
        {
            return DebugResult<TraceUntilWriteRangeResult>.Failure("invalid_range", "Trace range must fit within 0x0000..0xFFFF.");
        }

        traceAddress = address;
        traceLength = length;
        traceHit = false;
        uint instructionsRun = 0;
        try
        {
            for (var i = 0; i < maxInstructions && !traceHit; i++)
            {
                StepMachineInstruction();
                instructionsRun++;
            }
        }
        finally
        {
            traceAddress = -1;
            traceLength = 0;
        }

        var registers = ToRegisters(NesCore.DebugReadRegisters());
        var ppu = ReadPpuState();
        if (!ppu.IsSuccess)
        {
            return DebugResult<TraceUntilWriteRangeResult>.Failure(ppu.Error!.Code, ppu.Error.Message);
        }

        var disassemblyAddress = traceHit ? traceHitPc : ParseWord(registers.Pc);
        var disassembly = Disassemble(disassemblyAddress, 4);
        if (!disassembly.IsSuccess)
        {
            return DebugResult<TraceUntilWriteRangeResult>.Failure(disassembly.Error!.Code, disassembly.Error.Message);
        }

        return DebugResult<TraceUntilWriteRangeResult>.Success(new TraceUntilWriteRangeResult(
            true,
            traceHit ? "write" : "maxInstructions",
            Hex.FormatWord(address),
            length,
            traceHit ? Hex.FormatWord(traceHitAddress) : null,
            traceHit ? Hex.FormatWord(traceHitPc) : null,
            traceHit ? Hex.FormatByte(traceHitValue) : null,
            instructionsRun,
            registers,
            ppu.Value,
            disassembly.Value,
            GetTimeline()));
    }

    public DebugResult<PpuRegisterTraceResult> TracePpuRegisterWrites(PpuRegisterTraceRequest request)
    {
        if (!romLoaded)
        {
            return NoRom<PpuRegisterTraceResult>();
        }

        if (request.FrameCount is < 1 or > PpuRegisterTracing.MaxFrames ||
            request.MaxEvents is < 1 or > PpuRegisterTracing.MaxEvents ||
            request.Registers.Count is < 1 or > 8 ||
            request.Registers.Any(address => address is < 0x2000 or > 0x2007))
        {
            return DebugResult<PpuRegisterTraceResult>.Failure("invalid_ppu_trace_request", "PPU trace limits or register filters are invalid.");
        }

        var initial = ReadPpuState();
        if (!initial.IsSuccess)
        {
            return DebugResult<PpuRegisterTraceResult>.Failure(initial.Error!.Code, initial.Error.Message);
        }

        var controller = SetController(request.Buttons);
        if (!controller.IsSuccess)
        {
            return DebugResult<PpuRegisterTraceResult>.Failure(controller.Error!.Code, controller.Error.Message);
        }

        var startFrame = NesCore.frame_count;
        var trace = new ActivePpuRegisterTrace(startFrame, request.MaxEvents, request.Registers);
        activePpuRegisterTrace = trace;
        var hitBreakpoint = false;
        var stopReason = "framesComplete";
        var instructionLimit = (long)request.FrameCount * PpuRegisterTracing.MaxInstructionsPerFrame;
        long instructionsRun = 0;

        try
        {
            while (NesCore.frame_count - startFrame < request.FrameCount)
            {
                if (instructionsRun >= instructionLimit)
                {
                    stopReason = "instructionLimit";
                    break;
                }

                var registers = ToRegisters(NesCore.DebugReadRegisters());
                var breakpoint = IsBreakpointHit(ParseWord(registers.Pc), registers);
                if (!breakpoint.IsSuccess)
                {
                    return DebugResult<PpuRegisterTraceResult>.Failure(breakpoint.Error!.Code, breakpoint.Error.Message);
                }

                if (breakpoint.Value)
                {
                    hitBreakpoint = true;
                    stopReason = "breakpoint";
                    break;
                }

                StepMachineInstruction();
                instructionsRun++;
            }

            var final = ReadPpuState();
            if (!final.IsSuccess)
            {
                return DebugResult<PpuRegisterTraceResult>.Failure(final.Error!.Code, final.Error.Message);
            }

            return DebugResult<PpuRegisterTraceResult>.Success(new PpuRegisterTraceResult(
                request.FrameCount,
                Math.Max(0, NesCore.frame_count - startFrame),
                initial.Value,
                final.Value,
                trace.Events,
                trace.Events.Count,
                trace.EventsObserved,
                trace.Truncated,
                hitBreakpoint,
                stopReason,
                GetTimeline()));
        }
        catch (Exception ex)
        {
            return DebugResult<PpuRegisterTraceResult>.Failure("trace_ppu_register_writes_failed", ex.Message);
        }
        finally
        {
            activePpuRegisterTrace = null;
            _ = SetController([]);
        }
    }

    public DebugResult<ScreenRegionResult> ReadScreenRegion(int x, int y, int width, int height, string format)
    {
        if (!romLoaded)
        {
            return NoRom<ScreenRegionResult>();
        }

        if (x < 0 || y < 0 || width < 1 || height < 1 || x + width > ScreenWidth || y + height > ScreenHeight)
        {
            return DebugResult<ScreenRegionResult>.Failure("invalid_screen_region", "Screen region must fit within 256x240.");
        }

        if (!PaletteIndexScreen.TryParseFormat(format, out var forceRaw))
        {
            return DebugResult<ScreenRegionResult>.Failure("invalid_screen_region_format", "format must be palette_indices or palette_indices_raw.");
        }

        return DebugResult<ScreenRegionResult>.Success(
            PaletteIndexScreen.Build(NesCore.DebugReadPaletteIndices(), ScreenWidth, x, y, width, height, forceRaw));
    }

    public DebugResult<ScreenObservationResult> ObserveScreen(int frameCount) =>
        ScreenObserver.Observe(this, frameCount);

    public DebugResult<ExecutionObservationResult> ObserveExecution(ExecutionObservationRequest request)
    {
        if (!romLoaded)
        {
            return NoRom<ExecutionObservationResult>();
        }

        if (request.FrameCount is < 1 or > ExecutionObserver.MaxFrames ||
            request.MemoryProbes.Count > ExecutionObserver.MaxMemoryProbes ||
            request.MemoryProbes.Any(probe => probe.Length is < 1 or > ExecutionObserver.MaxMemoryProbeLength || probe.Address + probe.Length > 0x2000) ||
            request.MemoryProbes.Sum(probe => probe.Length) > ExecutionObserver.MaxMemoryBytesPerFrame ||
            request.MaxPpuEvents is < 1 or > ExecutionObserver.MaxPpuEvents ||
            request.PpuRegisters.Count is < 1 or > 8 ||
            request.PpuRegisters.Any(address => address is < 0x2000 or > 0x2007))
        {
            return DebugResult<ExecutionObservationResult>.Failure("invalid_execution_observation_request", "Execution observation limits or probe ranges are invalid.");
        }

        var previous = new byte[ScreenFrameAnalyzer.PixelCount];
        var current = new byte[ScreenFrameAnalyzer.PixelCount];
        var initialFrame = ScreenFrameAnalyzer.Capture(this, previous);
        if (!initialFrame.IsSuccess)
        {
            return DebugResult<ExecutionObservationResult>.Failure(initialFrame.Error!.Code, initialFrame.Error.Message);
        }

        var initialHash = ScreenFrameAnalyzer.Hash(previous);
        var initialNametables = NametableReader.ReadAll(ReadPpuBytes, includeDetails: false, GetTimeline());
        var controller = SetController(request.Buttons);
        if (!controller.IsSuccess)
        {
            return DebugResult<ExecutionObservationResult>.Failure(controller.Error!.Code, controller.Error.Message);
        }

        var startFrame = NesCore.frame_count;
        var trace = new ActivePpuRegisterTrace(startFrame, request.MaxPpuEvents, request.PpuRegisters);
        if (request.TracePpuWrites)
        {
            activePpuRegisterTrace = trace;
        }

        var frames = new List<ExecutionFrameObservation>(request.FrameCount);
        var framesRun = 0;
        var hitBreakpoint = false;
        var stopReason = "framesComplete";
        var released = false;

        try
        {
            for (var frameOffset = 1; frameOffset <= request.FrameCount; frameOffset++)
            {
                var run = RunFrame(1, stopAtBreakpoint: true);
                if (!run.IsSuccess)
                {
                    return DebugResult<ExecutionObservationResult>.Failure(run.Error!.Code, run.Error.Message);
                }

                framesRun += run.Value.FramesRun;
                hitBreakpoint = run.Value.HitBreakpoint;
                if (run.Value.FramesRun == 0)
                {
                    stopReason = hitBreakpoint ? "breakpoint" : "stopped";
                    break;
                }

                var captured = ScreenFrameAnalyzer.Capture(this, current);
                if (!captured.IsSuccess)
                {
                    return DebugResult<ExecutionObservationResult>.Failure(captured.Error!.Code, captured.Error.Message);
                }

                var screen = ScreenFrameAnalyzer.Compare(previous, current, frameOffset, run.Value.Timeline.Frames);
                var memory = request.MemoryProbes
                    .Select(probe =>
                    {
                        var bytes = ReadBytes(probe.Address, probe.Length);
                        return new MemoryProbeObservation(Hex.FormatWord(probe.Address), probe.Length, Hex.FormatBytes(bytes));
                    })
                    .ToArray();

                PpuStateResult? ppuState = null;
                if (request.IncludePpuState)
                {
                    var readPpuState = ReadPpuState();
                    if (!readPpuState.IsSuccess)
                    {
                        return DebugResult<ExecutionObservationResult>.Failure(readPpuState.Error!.Code, readPpuState.Error.Message);
                    }

                    ppuState = readPpuState.Value;
                }

                frames.Add(new ExecutionFrameObservation(screen, memory, ppuState));
                (previous, current) = (current, previous);

                if (hitBreakpoint)
                {
                    stopReason = "breakpoint";
                    break;
                }
            }

            var finalNametables = NametableReader.ReadAll(ReadPpuBytes, includeDetails: false, GetTimeline());
            var release = SetController([]);
            if (!release.IsSuccess)
            {
                return DebugResult<ExecutionObservationResult>.Failure(release.Error!.Code, release.Error.Message);
            }

            released = true;
            return DebugResult<ExecutionObservationResult>.Success(new ExecutionObservationResult(
                request.FrameCount,
                framesRun,
                request.Buttons.Select(ButtonName).ToArray(),
                initialHash,
                frames,
                trace.Events,
                trace.Events.Count,
                trace.EventsObserved,
                trace.Truncated,
                trace.Truncated,
                initialNametables,
                finalNametables,
                hitBreakpoint,
                stopReason,
                release.Value,
                ExecutionObserver.AppliedLimits,
                GetTimeline()));
        }
        catch (Exception ex)
        {
            return DebugResult<ExecutionObservationResult>.Failure("observe_execution_failed", ex.Message);
        }
        finally
        {
            activePpuRegisterTrace = null;
            if (!released)
            {
                _ = SetController([]);
            }
        }
    }

    public DebugResult<int> CopyPaletteIndexFrame(Memory<byte> destination)
    {
        if (!romLoaded)
        {
            return NoRom<int>();
        }

        if (destination.Length < ScreenWidth * ScreenHeight)
        {
            return DebugResult<int>.Failure(
                "invalid_screen_frame_buffer",
                $"destination must contain at least {ScreenWidth * ScreenHeight} bytes.");
        }

        NesCore.DebugCopyPaletteIndices(destination.Span);
        return DebugResult<int>.Success(ScreenWidth * ScreenHeight);
    }

    public DebugResult<InputTimelineResult> RunInputTimeline(IReadOnlyList<InputTimelineStep> steps)
    {
        if (!romLoaded)
        {
            return NoRom<InputTimelineResult>();
        }

        var results = new List<InputTimelineStepResult>(steps.Count);
        var totalFramesRun = 0;
        var completed = false;
        try
        {
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index];
                var buttons = ParseButtonNames(step.Buttons);
                if (!buttons.IsSuccess)
                {
                    return DebugResult<InputTimelineResult>.Failure(buttons.Error!.Code, buttons.Error.Message);
                }

                var controllerState = SetController(buttons.Value);
                if (!controllerState.IsSuccess)
                {
                    return DebugResult<InputTimelineResult>.Failure(controllerState.Error!.Code, controllerState.Error.Message);
                }

                var run = RunFrame(step.Frames);
                if (!run.IsSuccess)
                {
                    return DebugResult<InputTimelineResult>.Failure(run.Error!.Code, run.Error.Message);
                }

                totalFramesRun += run.Value.FramesRun;

                NesCpuRegisters? registers = null;
                if (step.ReadRegisters)
                {
                    var readRegisters = ReadRegisters();
                    if (!readRegisters.IsSuccess)
                    {
                        return DebugResult<InputTimelineResult>.Failure(readRegisters.Error!.Code, readRegisters.Error.Message);
                    }

                    registers = readRegisters.Value;
                }

                PpuStateResult? ppuState = null;
                if (step.ReadPpuState)
                {
                    var readPpu = ReadPpuState();
                    if (!readPpu.IsSuccess)
                    {
                        return DebugResult<InputTimelineResult>.Failure(readPpu.Error!.Code, readPpu.Error.Message);
                    }

                    ppuState = readPpu.Value;
                }

                OamDumpResult? oam = null;
                if (step.DumpOam)
                {
                    var readOam = ReadOam();
                    if (!readOam.IsSuccess)
                    {
                        return DebugResult<InputTimelineResult>.Failure(readOam.Error!.Code, readOam.Error.Message);
                    }

                    oam = readOam.Value;
                }

                ScreenCaptureResult? capture = null;
                if (step.Capture)
                {
                    var screen = CaptureScreen();
                    if (!screen.IsSuccess)
                    {
                        return DebugResult<InputTimelineResult>.Failure(screen.Error!.Code, screen.Error.Message);
                    }

                    capture = screen.Value;
                }

                TilemapDumpResult? tilemap = null;
                if (step.DumpTilemap)
                {
                    var address = (ushort)0x2000;
                    if (!string.IsNullOrWhiteSpace(step.TilemapAddress))
                    {
                        var parsed = NesAddress.Parse(step.TilemapAddress);
                        if (!parsed.IsSuccess)
                        {
                            return DebugResult<InputTimelineResult>.Failure(parsed.Error!.Code, parsed.Error.Message);
                        }

                        address = parsed.Value.Address;
                    }

                    var dump = DumpTilemap(address);
                    if (!dump.IsSuccess)
                    {
                        return DebugResult<InputTimelineResult>.Failure(dump.Error!.Code, dump.Error.Message);
                    }

                    tilemap = dump.Value;
                }

                MemoryReadResult? memory = null;
                if (!string.IsNullOrWhiteSpace(step.MemoryAddress))
                {
                    var parsed = NesAddress.Parse(step.MemoryAddress);
                    if (!parsed.IsSuccess)
                    {
                        return DebugResult<InputTimelineResult>.Failure(parsed.Error!.Code, parsed.Error.Message);
                    }

                    var readMemory = ReadMemory(parsed.Value.Address, step.MemoryLength ?? 1);
                    if (!readMemory.IsSuccess)
                    {
                        return DebugResult<InputTimelineResult>.Failure(readMemory.Error!.Code, readMemory.Error.Message);
                    }

                    memory = readMemory.Value;
                }

                results.Add(new InputTimelineStepResult(
                    index,
                    run.Value.FramesRun,
                    GetTimeline().Frames,
                    buttons.Value.Select(ButtonName).ToArray(),
                    registers,
                    ppuState,
                    oam,
                    capture,
                    tilemap,
                    memory,
                    GetTimeline()));
            }

            completed = true;
        }
        finally
        {
            if (!completed)
            {
                _ = SetController([]);
            }
        }

        var released = SetController([]);
        if (!released.IsSuccess)
        {
            return DebugResult<InputTimelineResult>.Failure(released.Error!.Code, released.Error.Message);
        }

        return DebugResult<InputTimelineResult>.Success(new InputTimelineResult(totalFramesRun, released.Value, results, GetTimeline()));
    }

    public DebugResult<TilemapDumpResult> DumpTilemap(ushort address)
    {
        if (!romLoaded)
        {
            return NoRom<TilemapDumpResult>();
        }

        return DebugResult<TilemapDumpResult>.Success(NametableReader.Read(address, ReadPpuBytes));
    }

    public DebugResult<NametableDumpResult> DumpNametables(bool includeDetails)
    {
        if (!romLoaded)
        {
            return NoRom<NametableDumpResult>();
        }

        return DebugResult<NametableDumpResult>.Success(
            NametableReader.ReadAll(ReadPpuBytes, includeDetails, GetTimeline()));
    }

    public DebugResult<TilesetDumpResult> DumpTileset(ushort address, int tileCount)
    {
        if (!romLoaded)
        {
            return NoRom<TilesetDumpResult>();
        }

        var bytes = ReadPpuBytes(address, tileCount * 16);
        var tiles = Enumerable.Range(0, tileCount)
            .Select(index => new TileDump(index, Hex.FormatWord((ushort)(address + index * 16)), Hex.FormatBytes(bytes.Skip(index * 16).Take(16))))
            .ToArray();

        return DebugResult<TilesetDumpResult>.Success(new TilesetDumpResult(Hex.FormatWord(address), tileCount, tiles));
    }

    public void Dispose()
    {
        disposed = true;
        NesCore.exit = true;
        NesCore.DebugSetMemoryObservers(null, null);
        NesCore.DebugSetPpuRegisterWriteObserver(null);
    }

    private void InitializeDebugTracking()
    {
        lastWriters.Clear();
        trackWrites = true;
        trackReads = false;
        watchHit = null;
        traceAddress = -1;
        traceLength = 0;
        traceHit = false;
        NesCore.DebugSetMemoryObservers(OnMemoryWrite, null);
        NesCore.DebugSetPpuRegisterWriteObserver(OnPpuRegisterWrite);
    }

    private void StepMachineInstruction()
    {
        NesCore.DebugStepInstruction();
        totalInstructions++;
    }

    private byte[] ReadBytes(ushort address, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = NesCore.DebugReadCpu((ushort)((address + i) & 0xFFFF));
        }

        return bytes;
    }

    private void OnMemoryWrite(ushort address, byte value, ushort pc)
    {
        if (!trackWrites)
        {
            return;
        }

        lastWriters[address] = lastWriters.TryGetValue(address, out var existing)
            ? new WriteRecord(pc, value, existing.Count + 1)
            : new WriteRecord(pc, value, 1);

        if (traceAddress >= 0 && address >= traceAddress && address < traceAddress + traceLength)
        {
            traceHit = true;
            traceHitAddress = address;
            traceHitPc = pc;
            traceHitValue = value;
        }

        if (watchpoints.TryMatch(address, isWrite: true, out var watchpoint))
        {
            watchHit = new WatchHit(address, watchpoint.Mode, pc, value);
        }
    }

    private void OnPpuRegisterWrite(NesCoreDebugPpuRegisterWrite write)
    {
        var trace = activePpuRegisterTrace;
        if (trace is null || !trace.Registers.Contains(write.Address))
        {
            return;
        }

        trace.EventsObserved++;
        if (trace.Events.Count >= trace.MaxEvents)
        {
            trace.Truncated = true;
            return;
        }

        trace.Events.Add(new PpuRegisterWriteEvent(
            write.Before.Frame - trace.StartFrame,
            (ulong)Math.Max(0, write.Before.Frame),
            write.CpuCycle,
            totalInstructions,
            Hex.FormatWord(write.Pc),
            Hex.FormatWord(write.Address),
            PpuRegisterTracing.RegisterName(write.Address),
            Hex.FormatByte(write.Value),
            ToPpuRegisterSnapshot(write.Before),
            ToPpuRegisterSnapshot(write.After)));
    }

    private static PpuRegisterSnapshot ToPpuRegisterSnapshot(NesCoreDebugPpuRegisterSnapshot snapshot) =>
        new(
            snapshot.Scanline,
            snapshot.Dot,
            snapshot.VBlank,
            snapshot.RenderingActive,
            Hex.FormatWord(snapshot.V),
            Hex.FormatWord(snapshot.T),
            snapshot.X,
            snapshot.W);

    private void OnMemoryRead(ushort address, ushort pc)
    {
        if (!trackReads)
        {
            return;
        }

        if (watchpoints.TryMatch(address, isWrite: false, out var watchpoint))
        {
            watchHit = new WatchHit(address, watchpoint.Mode, pc, null);
        }
    }

    private DebugResult<ContinueResult> ContinueStopped(string reason, ulong instructionsRun)
    {
        var registers = ReadRegisters();
        return registers.IsSuccess
            ? Stop(reason, registers.Value, instructionsRun)
            : DebugResult<ContinueResult>.Failure(registers.Error!.Code, registers.Error.Message);
    }

    private DebugResult<ContinueResult> StepSingle(string reason)
    {
        watchHit = null;
        AttachReadObserverIfNeeded();
        try
        {
            StepMachineInstruction();
            var registers = ToRegisters(NesCore.DebugReadRegisters());
            if (watchHit.HasValue)
            {
                return Stop("watchpoint", registers, 1);
            }

            var breakpoint = IsBreakpointHit(ParseWord(registers.Pc), registers);
            if (!breakpoint.IsSuccess)
            {
                return DebugResult<ContinueResult>.Failure(breakpoint.Error!.Code, breakpoint.Error.Message);
            }

            return breakpoint.Value ? Stop("breakpoint", registers, 1) : Stop(reason, registers, 1);
        }
        finally
        {
            DetachReadObserver();
        }
    }

    private DebugResult<ContinueResult> StepUntil(int maxInstructions, Func<NesCpuRegisters, bool> completed, string completedReason)
    {
        watchHit = null;
        AttachReadObserverIfNeeded();
        try
        {
            for (var i = 0; i < maxInstructions; i++)
            {
                StepMachineInstruction();
                var registers = ToRegisters(NesCore.DebugReadRegisters());
                if (watchHit.HasValue)
                {
                    return Stop("watchpoint", registers, (ulong)i + 1);
                }

                var breakpoint = IsBreakpointHit(ParseWord(registers.Pc), registers);
                if (!breakpoint.IsSuccess)
                {
                    return DebugResult<ContinueResult>.Failure(breakpoint.Error!.Code, breakpoint.Error.Message);
                }

                if (breakpoint.Value)
                {
                    return Stop("breakpoint", registers, (ulong)i + 1);
                }

                if (completed(registers))
                {
                    return Stop(completedReason, registers, (ulong)i + 1);
                }
            }

            return Stop("maxInstructions", ToRegisters(NesCore.DebugReadRegisters()), (ulong)maxInstructions);
        }
        finally
        {
            DetachReadObserver();
        }
    }

    private DebugResult<bool> IsBreakpointHit(ushort address, NesCpuRegisters registers)
    {
        foreach (var breakpoint in breakpoints.FindAll(address))
        {
            var shouldBreak = ShouldBreak(breakpoint, registers);
            if (!shouldBreak.IsSuccess)
            {
                return shouldBreak;
            }

            if (shouldBreak.Value)
            {
                return DebugResult<bool>.Success(true);
            }
        }

        return DebugResult<bool>.Success(false);
    }

    private DebugResult<bool> ShouldBreak(BreakpointInfo breakpoint, NesCpuRegisters registers)
    {
        if (breakpoint.ParsedCondition is null)
        {
            return string.IsNullOrWhiteSpace(breakpoint.Condition)
                ? DebugResult<bool>.Success(true)
                : DebugResult<bool>.Failure("invalid_breakpoint_condition", $"Breakpoint '{breakpoint.Id}' has an invalid condition.");
        }

        return breakpoint.ParsedCondition.Evaluate(new ConditionContext(this, registers));
    }

    private void AttachReadObserverIfNeeded()
    {
        trackReads = watchpoints.HasEnabledReadWatchpoints;
        NesCore.DebugSetMemoryObservers(OnMemoryWrite, trackReads ? OnMemoryRead : null);
    }

    private void DetachReadObserver()
    {
        trackReads = false;
        if (romLoaded && !disposed)
        {
            NesCore.DebugSetMemoryObservers(OnMemoryWrite, null);
        }
    }

    private DebugResult<RunUntilConditionResult> StopRunUntilCondition(
        string reason,
        NesCpuRegisters registers,
        uint instructionsRun,
        long startFrames)
    {
        var ppu = ReadPpuState();
        return ppu.IsSuccess
            ? DebugResult<RunUntilConditionResult>.Success(new RunUntilConditionResult(
                true,
                reason,
                registers.Pc,
                instructionsRun,
                (ulong)Math.Max(0, NesCore.frame_count - startFrames),
                registers,
                ppu.Value,
                GetTimeline()))
            : DebugResult<RunUntilConditionResult>.Failure(ppu.Error!.Code, ppu.Error.Message);
    }

    private static byte[] ReadPpuBytes(ushort address, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = NesCore.DebugReadPpu((ushort)((address + i) & 0x3FFF));
        }

        return bytes;
    }

    private DebugResult<ContinueResult> Stop(string reason, NesCpuRegisters registers, ulong instructionsRun = 0) =>
        DebugResult<ContinueResult>.Success(new ContinueResult(true, reason, registers.Pc, registers, GetTimeline(), instructionsRun));

    private TimelineCounters GetTimeline() =>
        new((ulong)Math.Max(0, NesCore.frame_count), NesCore.DebugReadRegisters().Cycles, totalInstructions);

    private ControllerStateResult ToControllerState() =>
        new(
            pressedButtons.Contains(NesButton.A),
            pressedButtons.Contains(NesButton.B),
            pressedButtons.Contains(NesButton.Select),
            pressedButtons.Contains(NesButton.Start),
            pressedButtons.Contains(NesButton.Up),
            pressedButtons.Contains(NesButton.Down),
            pressedButtons.Contains(NesButton.Left),
            pressedButtons.Contains(NesButton.Right),
            CanonicalButtons.Where(pressedButtons.Contains).Select(ButtonName).ToArray());

    private DisassembledInstruction DecodeInstruction(ushort address)
    {
        var opcode = NesCore.DebugReadCpu(address);
        var next = NesCore.DebugReadCpu((ushort)((address + 1) & 0xFFFF));
        var high = NesCore.DebugReadCpu((ushort)((address + 2) & 0xFFFF));
        var absolute = (ushort)(next | (high << 8));

        return opcode switch
        {
            0xA9 => Instruction(address, [opcode, next], $"LDA #${next:X2}"),
            0xA5 => Instruction(address, [opcode, next], $"LDA ${next:X2}"),
            0xAD => Instruction(address, [opcode, next, high], $"LDA ${absolute:X4}"),
            0x85 => Instruction(address, [opcode, next], $"STA ${next:X2}"),
            0x8D => Instruction(address, [opcode, next, high], $"STA ${absolute:X4}"),
            0x4C => Instruction(address, [opcode, next, high], $"JMP ${absolute:X4}"),
            0x20 => Instruction(address, [opcode, next, high], $"JSR ${absolute:X4}"),
            0x60 => Instruction(address, [opcode], "RTS"),
            0xEA => Instruction(address, [opcode], "NOP"),
            0x00 => Instruction(address, [opcode], "BRK"),
            _ => Instruction(address, [opcode], $"DB ${opcode:X2}"),
        };
    }

    private static DisassembledInstruction Instruction(ushort address, IReadOnlyList<byte> bytes, string text) =>
        new(Hex.FormatWord(address), Hex.FormatBytes(bytes), text, null);

    private static NesCpuRegisters ToRegisters(NesCoreDebugRegisters registers) =>
        new(
            Hex.FormatByte(registers.A),
            Hex.FormatByte(registers.X),
            Hex.FormatByte(registers.Y),
            Hex.FormatByte(registers.Sp),
            Hex.FormatWord(registers.Pc),
            Hex.FormatByte(registers.Status),
            registers.Carry,
            registers.Zero,
            registers.InterruptDisable,
            registers.DecimalMode,
            registers.Overflow,
            registers.Negative,
            (long)Math.Min(registers.Cycles, long.MaxValue));

    private static DebugResult<IReadOnlyList<NesButton>> ParseButtonNames(IReadOnlyList<string>? buttons)
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

    private static DebugResult<WatchpointSetResult> ToWatchpointSetResult(WatchpointInfo watchpoint)
    {
        return DebugResult<WatchpointSetResult>.Success(
            new WatchpointSetResult(watchpoint.Id, watchpoint.Address, ToWatchpointModeName(watchpoint.Mode), watchpoint.Enabled, watchpoint.Length));
    }

    private static string ToWatchpointModeName(WatchpointMode mode) => mode switch
    {
        WatchpointMode.Read => "read",
        WatchpointMode.Write => "write",
        WatchpointMode.Access => "access",
        _ => mode.ToString().ToLowerInvariant(),
    };

    private static ushort ParseWord(string text)
    {
        var normalized = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return Convert.ToUInt16(normalized, 16);
    }

    private static byte ParseByte(string text)
    {
        var normalized = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return Convert.ToByte(normalized, 16);
    }

    private static string ButtonName(NesButton button) => button switch
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

    private static DebugResult<T> NoRom<T>() => DebugResult<T>.Failure("no_rom_loaded", "Load a ROM before using this tool.");

    private static DebugResult<NesRomHeader> ParseHeader(byte[] bytes)
    {
        if (bytes.Length < 16 || bytes[0] != 'N' || bytes[1] != 'E' || bytes[2] != 'S' || bytes[3] != 0x1A)
        {
            return DebugResult<NesRomHeader>.Failure("invalid_ines", "ROM does not have a valid iNES header.");
        }

        var flags6 = bytes[6];
        var flags7 = bytes[7];
        var mapper = ((flags7 & 0xF0) | (flags6 >> 4)) & 0xFF;
        return DebugResult<NesRomHeader>.Success(new NesRomHeader(mapper, bytes[4], bytes[5]));
    }

    private sealed class ConditionContext(AprNesDebugSession session, NesCpuRegisters registers) : INesBreakpointConditionContext
    {
        public NesCpuRegisters Registers { get; } = registers;

        public DebugResult<byte> ReadByte(ushort address)
        {
            try
            {
                return DebugResult<byte>.Success(session.ReadBytes(address, 1)[0]);
            }
            catch (Exception ex)
            {
                return DebugResult<byte>.Failure("read_memory_failed", ex.Message);
            }
        }
    }

    private readonly record struct WriteRecord(ushort Pc, byte Value, ulong Count);

    private readonly record struct WatchHit(ushort Address, WatchpointMode Mode, ushort Pc, byte? Value);

    private sealed class ActivePpuRegisterTrace(
        int startFrame,
        int maxEvents,
        IReadOnlySet<ushort> registers)
    {
        public int StartFrame { get; } = startFrame;

        public int MaxEvents { get; } = maxEvents;

        public IReadOnlySet<ushort> Registers { get; } = registers;

        public List<PpuRegisterWriteEvent> Events { get; } = [];

        public int EventsObserved { get; set; }

        public bool Truncated { get; set; }
    }

    private sealed record NesRomHeader(int Mapper, int PrgRomBanks, int ChrRomBanks);
}
