using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ADNES.Cartridge;
using ADNES.Controller;
using ADNES.Controller.Enums;
using Nes.Debug.Core;
using Nes.Debug.Symbols;

namespace Nes.Debug.Emulator;

public sealed class ManagedNesDebugSession : INesDebugSession, IPaletteIndexFrameSource, IDisposable
{
    private const int ScreenWidth = ADNES.Emulator.Width;
    private const int ScreenHeight = ADNES.Emulator.Height;
    private const int MaxInstructionsPerFrame = 1_000_000;
    private const uint StateMagicV1 = 0x31534D4E; // "NMS1"
    private const uint StateMagicV2 = 0x32534D4E; // "NMS2"

    private static readonly IReadOnlyDictionary<NesButton, Buttons> ButtonMap =
        new Dictionary<NesButton, Buttons>
        {
            [NesButton.A] = Buttons.A,
            [NesButton.B] = Buttons.B,
            [NesButton.Select] = Buttons.Select,
            [NesButton.Start] = Buttons.Start,
            [NesButton.Up] = Buttons.Up,
            [NesButton.Down] = Buttons.Down,
            [NesButton.Left] = Buttons.Left,
            [NesButton.Right] = Buttons.Right,
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
    private string? romPath;
    private string? romTitle;
    private int? mapper;
    private int prgRomBanks;
    private int chrRomBanks;
    private bool romLoaded;
    private bool trackWrites;
    private bool trackReads;
    private WatchHit? watchHit;
    private bool disposed;
    private int cpuIdleCycles;
    private long totalFrames;
    private ulong totalCycles;
    private ulong totalInstructions;
    private byte[] lastFrame = new byte[ScreenWidth * ScreenHeight];
    private int traceAddress = -1;
    private int traceLength;
    private bool traceHit;
    private ushort traceHitAddress;
    private ushort traceHitPc;
    private byte traceHitValue;

    private NESCartridge? cartridge;
    private ADNES.PPU.Core? ppu;
    private ADNES.CPU.Core? cpu;
    private NESController? controller;

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

            romBytes = bytes;
            romPath = path;
            romTitle = Path.GetFileNameWithoutExtension(path);
            mapper = header.Value.Mapper;
            prgRomBanks = header.Value.PrgRomBanks;
            chrRomBanks = header.Value.ChrRomBanks;
            BuildMachine();
            breakpoints.ClearAll();
            watchpoints.ClearAll();

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
            writer.Write(StateMagicV2);
            writer.Write(Cpu.A);
            writer.Write(Cpu.X);
            writer.Write(Cpu.Y);
            writer.Write((ushort)Cpu.PC);
            writer.Write(Cpu.SP);
            writer.Write(Cpu.Status.ToByte());
            writer.Write(Cpu.Cycles);
            writer.Write(Cpu.NMI);
            writer.Write(cpuIdleCycles);
            writer.Write(totalFrames);
            writer.Write(totalCycles);
            writer.Write(totalInstructions);
            var ppuState = Ppu.SnapshotState();
            WriteBytes(writer, Cpu.CPUMemory.SnapshotInternalRam());
            WriteBytes(writer, ppuState.PatternTables);
            WriteBytes(writer, ppuState.Vram);
            WriteBytes(writer, ppuState.PaletteMemory);
            WritePpuState(writer, ppuState);
            WriteBytes(writer, lastFrame);

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
            if (magic is not (StateMagicV1 or StateMagicV2))
            {
                return DebugResult<LoadStateResult>.Failure("invalid_state", "The file is not a managed NES save state.");
            }

            var a = reader.ReadByte();
            var x = reader.ReadByte();
            var y = reader.ReadByte();
            var pc = reader.ReadUInt16();
            var sp = reader.ReadByte();
            var status = reader.ReadByte();
            var cycles = reader.ReadInt64();
            var nmi = reader.ReadBoolean();
            var idleCycles = reader.ReadInt32();
            var frames = reader.ReadInt64();
            var cyclesTotal = magic == StateMagicV2 ? reader.ReadUInt64() : 0;
            var instructionsTotal = magic == StateMagicV2 ? reader.ReadUInt64() : 0;
            var cpuRam = ReadBytes(reader);
            var patternTables = ReadBytes(reader);
            var vram = ReadBytes(reader);
            var palette = ReadBytes(reader);
            var ppuState = ReadPpuState(reader, patternTables, vram, palette);
            var frame = ReadBytes(reader);

            trackWrites = false;
            try
            {
                Cpu.A = a;
                Cpu.X = x;
                Cpu.Y = y;
                Cpu.PC = pc;
                Cpu.SP = sp;
                Cpu.Status.FromByte(status);
                Cpu.Cycles = cycles;
                Cpu.NMI = nmi;
                Cpu.CPUMemory.RestoreInternalRam(cpuRam);
                Ppu.RestoreState(ppuState);
                cpuIdleCycles = idleCycles;
                totalFrames = frames;
                totalCycles = cyclesTotal;
                totalInstructions = instructionsTotal;
                lastFrame = frame;
            }
            finally
            {
                trackWrites = true;
            }

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
            BuildMachine();
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

        var cpuCore = Cpu;
        var pcBefore = (ushort)cpuCore.PC;
        var instructions = Disassemble(pcBefore, Math.Min(count, 16));

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

        var registers = ReadRegisters();
        if (!registers.IsSuccess)
        {
            return DebugResult<StepInstructionResult>.Failure(registers.Error!.Code, registers.Error.Message);
        }

        var disassembly = instructions.IsSuccess
            ? string.Join('\n', instructions.Value.Instructions.Select(instruction => $"{instruction.Address}: {instruction.Text}"))
            : "";

        return DebugResult<StepInstructionResult>.Success(
            new StepInstructionResult(Hex.FormatWord(pcBefore), registers.Value.Pc, registers.Value, disassembly, count, GetTimeline()));
    }

    public DebugResult<RunFrameResult> RunFrame(int count)
    {
        if (!romLoaded)
        {
            return NoRom<RunFrameResult>();
        }

        watchHit = null;
        var framesRun = 0;
        var startFrames = totalFrames;

        AttachReadObserverIfNeeded();
        try
        {
            while (framesRun < count)
            {
                var frameStart = totalFrames;
                for (var i = 0; i < MaxInstructionsPerFrame && totalFrames == frameStart; i++)
                {
                    StepMachineInstruction();
                    if (watchHit.HasValue)
                    {
                        var watchRegisters = ReadRegisters();
                        return watchRegisters.IsSuccess
                            ? DebugResult<RunFrameResult>.Success(new RunFrameResult(framesRun, totalFrames, watchRegisters.Value, false, GetTimeline()))
                            : DebugResult<RunFrameResult>.Failure(watchRegisters.Error!.Code, watchRegisters.Error.Message);
                    }

                    if (!breakpoints.HasBreakpointAt((ushort)Cpu.PC))
                    {
                        continue;
                    }

                    var breakRegisters = ReadRegisters();
                    if (!breakRegisters.IsSuccess)
                    {
                        return DebugResult<RunFrameResult>.Failure(breakRegisters.Error!.Code, breakRegisters.Error.Message);
                    }

                    var hit = IsBreakpointHit((ushort)Cpu.PC, breakRegisters.Value);
                    if (!hit.IsSuccess)
                    {
                        return DebugResult<RunFrameResult>.Failure(hit.Error!.Code, hit.Error.Message);
                    }

                    if (hit.Value)
                    {
                        return DebugResult<RunFrameResult>.Success(
                            new RunFrameResult(framesRun, totalFrames, breakRegisters.Value, true, GetTimeline()));
                    }
                }

                if (totalFrames == frameStart)
                {
                    break;
                }

                framesRun = (int)(totalFrames - startFrames);
            }
        }
        catch (Exception ex)
        {
            return DebugResult<RunFrameResult>.Failure("run_frame_failed", ex.Message);
        }
        finally
        {
            DetachReadObserver();
        }

        var registers = ReadRegisters();
        if (!registers.IsSuccess)
        {
            return DebugResult<RunFrameResult>.Failure(registers.Error!.Code, registers.Error.Message);
        }

        var finalHit = IsBreakpointHit((ushort)Cpu.PC, registers.Value);
        if (!finalHit.IsSuccess)
        {
            return DebugResult<RunFrameResult>.Failure(finalHit.Error!.Code, finalHit.Error.Message);
        }

        return DebugResult<RunFrameResult>.Success(
            new RunFrameResult(framesRun, totalFrames, registers.Value, finalHit.Value, GetTimeline()));
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
            Controller.ButtonRelease(ButtonMap[button]);
        }

        foreach (var button in pressedButtons)
        {
            Controller.ButtonPress(ButtonMap[button]);
        }

        return DebugResult<ControllerStateResult>.Success(ToControllerState());
    }

    public DebugResult<PressButtonsResult> PressButtons(IReadOnlyList<NesButton> buttons, int frameCount)
    {
        var pressed = SetController(buttons);
        if (!pressed.IsSuccess)
        {
            return DebugResult<PressButtonsResult>.Failure(pressed.Error!.Code, pressed.Error.Message);
        }

        var run = RunFrame(frameCount);
        var released = SetController(Array.Empty<NesButton>());
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
                var pc = (ushort)Cpu.PC;
                if (breakpoints.HasBreakpointAt(pc))
                {
                    var registers = ReadRegisters();
                    if (!registers.IsSuccess)
                    {
                        return DebugResult<ContinueResult>.Failure(registers.Error!.Code, registers.Error.Message);
                    }

                    var hit = IsBreakpointHit(pc, registers.Value);
                    if (!hit.IsSuccess)
                    {
                        return DebugResult<ContinueResult>.Failure(hit.Error!.Code, hit.Error.Message);
                    }

                    if (hit.Value)
                    {
                        return Stop("breakpoint", registers.Value, (ulong)i);
                    }
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

        var startPc = (ushort)Cpu.PC;
        var opcode = ReadByte(startPc);
        if (opcode != 0x20)
        {
            return StepSingle("step");
        }

        var returnAddress = (ushort)(startPc + 3);
        var startSp = Cpu.SP;
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

        var startSp = Cpu.SP;
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
        var startFrames = totalFrames;
        AttachReadObserverIfNeeded();
        try
        {
            for (var i = 0; i < maxInstructions; i++)
            {
                var before = ReadRegisters();
                if (!before.IsSuccess)
                {
                    return DebugResult<RunUntilConditionResult>.Failure(before.Error!.Code, before.Error.Message);
                }

                var conditionResult = parsedCondition.Evaluate(new ConditionContext(this, before.Value));
                if (!conditionResult.IsSuccess)
                {
                    return DebugResult<RunUntilConditionResult>.Failure(conditionResult.Error!.Code, conditionResult.Error.Message);
                }

                if (conditionResult.Value)
                {
                    return StopRunUntilCondition("condition", before.Value, (uint)i, startFrames);
                }

                var hit = IsBreakpointHit(ParseWord(before.Value.Pc), before.Value);
                if (!hit.IsSuccess)
                {
                    return DebugResult<RunUntilConditionResult>.Failure(hit.Error!.Code, hit.Error.Message);
                }

                if (hit.Value)
                {
                    return StopRunUntilCondition("breakpoint", before.Value, (uint)i, startFrames);
                }

                if (totalFrames - startFrames >= maxFrames)
                {
                    return StopRunUntilCondition("maxFrames", before.Value, (uint)i, startFrames);
                }

                StepMachineInstruction();

                var after = ReadRegisters();
                if (!after.IsSuccess)
                {
                    return DebugResult<RunUntilConditionResult>.Failure(after.Error!.Code, after.Error.Message);
                }

                conditionResult = parsedCondition.Evaluate(new ConditionContext(this, after.Value));
                if (!conditionResult.IsSuccess)
                {
                    return DebugResult<RunUntilConditionResult>.Failure(conditionResult.Error!.Code, conditionResult.Error.Message);
                }

                if (conditionResult.Value)
                {
                    return StopRunUntilCondition("condition", after.Value, (uint)i + 1, startFrames);
                }

                if (watchHit.HasValue)
                {
                    return StopRunUntilCondition("watchpoint", after.Value, (uint)i + 1, startFrames);
                }

                if (totalFrames - startFrames >= maxFrames)
                {
                    return StopRunUntilCondition("maxFrames", after.Value, (uint)i + 1, startFrames);
                }
            }

            var final = ReadRegisters();
            return final.IsSuccess
                ? StopRunUntilCondition("maxInstructions", final.Value, (uint)maxInstructions, startFrames)
                : DebugResult<RunUntilConditionResult>.Failure(final.Error!.Code, final.Error.Message);
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
                romLoaded ? Hex.FormatWord((ushort)Cpu.PC) : null,
                totalFrames,
                GetTimeline()));
    }

    public DebugResult<NesCpuRegisters> ReadRegisters()
    {
        if (!romLoaded)
        {
            return NoRom<NesCpuRegisters>();
        }

        return DebugResult<NesCpuRegisters>.Success(ToRegisters());
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
            trackWrites = false;
            try
            {
                for (var i = 0; i < bytes.Count; i++)
                {
                    Cpu.CPUMemory.WriteByte((address + i) & 0xFFFF, bytes[i]);
                }
            }
            finally
            {
                trackWrites = true;
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

        var oam = Ppu.SnapshotOam();
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

        return DebugResult<PpuStateResult>.Success(PpuStateBuilder.Build(
            Ppu.RegisterPpuCtrl,
            Ppu.RegisterPpuMask,
            Ppu.RegisterPpuStatus,
            Ppu.RegisterOamAddr,
            (ushort)Ppu.RegisterPpuAddr,
            (ushort)Ppu.RegisterPpuScroll,
            Ppu.FineX,
            Ppu.WriteToggle,
            Ppu.CurrentScanline,
            Ppu.CurrentCycle,
            Ppu.NMI,
            Ppu.Cycles,
            GetTimeline()));
    }

    public DebugResult<ScreenCaptureResult> CaptureScreen()
    {
        if (!romLoaded)
        {
            return NoRom<ScreenCaptureResult>();
        }

        var pixels = lastFrame.Select(value => NesPalette[value & 0x3F]).ToArray();
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

        var registers = ReadRegisters();
        if (!registers.IsSuccess)
        {
            return DebugResult<TraceUntilWriteResult>.Failure(registers.Error!.Code, registers.Error.Message);
        }

        return DebugResult<TraceUntilWriteResult>.Success(traceHit
            ? new TraceUntilWriteResult(true, "write", Hex.FormatWord(address), Hex.FormatWord(traceHitPc), Hex.FormatByte(traceHitValue), instructionsRun, registers.Value, GetTimeline())
            : new TraceUntilWriteResult(true, "maxInstructions", Hex.FormatWord(address), null, null, instructionsRun, registers.Value, GetTimeline()));
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

        var registers = ReadRegisters();
        if (!registers.IsSuccess)
        {
            return DebugResult<TraceUntilWriteRangeResult>.Failure(registers.Error!.Code, registers.Error.Message);
        }

        var ppu = ReadPpuState();
        if (!ppu.IsSuccess)
        {
            return DebugResult<TraceUntilWriteRangeResult>.Failure(ppu.Error!.Code, ppu.Error.Message);
        }

        var disassemblyAddress = traceHit ? traceHitPc : ParseWord(registers.Value.Pc);
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
            registers.Value,
            ppu.Value,
            disassembly.Value,
            GetTimeline()));
    }

    public DebugResult<PpuRegisterTraceResult> TracePpuRegisterWrites(PpuRegisterTraceRequest request) =>
        DebugResult<PpuRegisterTraceResult>.Failure(
            "ppu_register_trace_not_supported",
            "Continuous PPU register tracing requires the AprNes backend.");

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
            PaletteIndexScreen.Build(lastFrame, ScreenWidth, x, y, width, height, forceRaw));
    }

    public DebugResult<ScreenObservationResult> ObserveScreen(int frameCount) =>
        ScreenObserver.Observe(this, frameCount);

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

        lastFrame.CopyTo(destination);
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
    }

    private void BuildMachine()
    {
        cartridge = new NESCartridge(romBytes);
        controller = new NESController();
        ppu = new ADNES.PPU.Core(cartridge.MemoryMapper, DmaTransfer);
        cpu = new ADNES.CPU.Core(cartridge.MemoryMapper, controller);
        cpu.Reset();
        cpu.CPUMemory.WriteObserver = OnMemoryWrite;
        cpu.CPUMemory.ReadObserver = null;
        ppu.Reset();
        cpu.Cycles = 4;
        cpuIdleCycles = 0;
        totalFrames = 0;
        totalCycles = 0;
        totalInstructions = 0;
        lastFrame = new byte[ScreenWidth * ScreenHeight];
        pressedButtons.Clear();
        lastWriters.Clear();
        trackWrites = true;
        trackReads = false;
        watchHit = null;
        traceAddress = -1;
        traceLength = 0;
        traceHit = false;
        romLoaded = true;
    }

    private int StepMachineInstruction()
    {
        var wasIdle = cpuIdleCycles > 0;
        var ticks = StepCpu();
        totalCycles += (ulong)Math.Max(0, ticks);
        if (!wasIdle)
        {
            totalInstructions++;
        }

        for (var i = 0; i < ticks * 3; i++)
        {
            Ppu.Tick();
        }

        if (Ppu.NMI)
        {
            Ppu.NMI = false;
            Cpu.NMI = true;
        }

        if (Ppu.FrameReady)
        {
            Array.Copy(Ppu.FrameBuffer, lastFrame, lastFrame.Length);
            totalFrames++;
            Ppu.FrameReady = false;
        }

        return ticks;
    }

    private int StepCpu()
    {
        if (cpuIdleCycles == 0)
        {
            return Cpu.Tick();
        }

        cpuIdleCycles--;
        Cpu.Instruction.Cycles = 1;
        Cpu.Cycles++;
        return 1;
    }

    private byte[] DmaTransfer(byte[] oam, int oamOffset, int offset)
    {
        for (var i = 0; i < 256; i++)
        {
            oam[(oamOffset + i) % 256] = Cpu.CPUMemory.ReadByte(offset + i);
        }

        cpuIdleCycles = Cpu.Cycles % 2 == 1 ? 514 : 513;
        return oam;
    }

    private void OnMemoryWrite(int address, byte value)
    {
        if (!trackWrites)
        {
            return;
        }

        var pc = (ushort)Cpu.PC;
        var masked = address & 0xFFFF;
        lastWriters[masked] = lastWriters.TryGetValue(masked, out var existing)
            ? new WriteRecord(pc, value, existing.Count + 1)
            : new WriteRecord(pc, value, 1);

        if (traceAddress >= 0 && masked >= traceAddress && masked < traceAddress + traceLength)
        {
            traceHit = true;
            traceHitAddress = (ushort)masked;
            traceHitPc = pc;
            traceHitValue = value;
        }

        if (watchpoints.TryMatch((ushort)masked, isWrite: true, out var watchpoint))
        {
            watchHit = new WatchHit((ushort)masked, watchpoint.Mode, pc, value);
        }
    }

    private void OnMemoryRead(int address)
    {
        if (!trackReads)
        {
            return;
        }

        var masked = (ushort)(address & 0xFFFF);
        if (watchpoints.TryMatch(masked, isWrite: false, out var watchpoint))
        {
            watchHit = new WatchHit(masked, watchpoint.Mode, (ushort)Cpu.PC, null);
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
            var registers = ReadRegisters();
            if (!registers.IsSuccess)
            {
                return DebugResult<ContinueResult>.Failure(registers.Error!.Code, registers.Error.Message);
            }

            if (watchHit.HasValue)
            {
                return Stop("watchpoint", registers.Value, 1);
            }

            var breakpoint = IsBreakpointHit(ParseWord(registers.Value.Pc), registers.Value);
            if (!breakpoint.IsSuccess)
            {
                return DebugResult<ContinueResult>.Failure(breakpoint.Error!.Code, breakpoint.Error.Message);
            }

            return breakpoint.Value ? Stop("breakpoint", registers.Value, 1) : Stop(reason, registers.Value, 1);
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
                var registers = ReadRegisters();
                if (!registers.IsSuccess)
                {
                    return DebugResult<ContinueResult>.Failure(registers.Error!.Code, registers.Error.Message);
                }

                if (watchHit.HasValue)
                {
                    return Stop("watchpoint", registers.Value, (ulong)i + 1);
                }

                var breakpoint = IsBreakpointHit(ParseWord(registers.Value.Pc), registers.Value);
                if (!breakpoint.IsSuccess)
                {
                    return DebugResult<ContinueResult>.Failure(breakpoint.Error!.Code, breakpoint.Error.Message);
                }

                if (breakpoint.Value)
                {
                    return Stop("breakpoint", registers.Value, (ulong)i + 1);
                }

                if (completed(registers.Value))
                {
                    return Stop(completedReason, registers.Value, (ulong)i + 1);
                }
            }

            var final = ReadRegisters();
            return final.IsSuccess
                ? Stop("maxInstructions", final.Value, (ulong)maxInstructions)
                : DebugResult<ContinueResult>.Failure(final.Error!.Code, final.Error.Message);
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
        trackReads = false;
        if (watchpoints.HasEnabledReadWatchpoints)
        {
            Cpu.CPUMemory.ReadObserver = OnMemoryRead;
        }
    }

    private void DetachReadObserver()
    {
        if (cpu != null)
        {
            cpu.CPUMemory.ReadObserver = null;
        }

        trackReads = false;
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
                (ulong)Math.Max(0, totalFrames - startFrames),
                registers,
                ppu.Value,
                GetTimeline()))
            : DebugResult<RunUntilConditionResult>.Failure(ppu.Error!.Code, ppu.Error.Message);
    }

    private byte ReadByte(ushort address) => Cpu.CPUMemory.ReadByte(address);

    private byte[] ReadBytes(ushort address, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = ReadByte((ushort)((address + i) & 0xFFFF));
        }

        return bytes;
    }

    private byte[] ReadPpuBytes(ushort address, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Ppu.PPUMemory.ReadByte((address + i) & 0x3FFF);
        }

        return bytes;
    }

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

    private DebugResult<ContinueResult> Stop(string reason, NesCpuRegisters registers, ulong instructionsRun = 0) =>
        DebugResult<ContinueResult>.Success(new ContinueResult(true, reason, registers.Pc, registers, GetTimeline(), instructionsRun));

    private TimelineCounters GetTimeline() => new((ulong)Math.Max(0, totalFrames), totalCycles, totalInstructions);

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

    private static void WriteBytes(BinaryWriter writer, byte[] bytes)
    {
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static byte[] ReadBytes(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        return reader.ReadBytes(length);
    }

    private static void WriteInts(BinaryWriter writer, int[] values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    private static int[] ReadInts(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        var values = new int[length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = reader.ReadInt32();
        }

        return values;
    }

    private static void WritePpuState(BinaryWriter writer, ADNES.PPU.PpuCoreState state)
    {
        writer.Write(state.FineX);
        writer.Write(state.WriteOrderToggle);
        writer.Write(state.FrameOrderToggle);
        writer.Write(state.PpuCtrl);
        writer.Write(state.PpuMask);
        writer.Write(state.PpuStatus);
        writer.Write(state.OamAddr);
        writer.Write(state.PpuAddr);
        writer.Write(state.PpuScroll);
        writer.Write(state.PpuDataBuffer);
        writer.Write(state.TileShiftRegister);
        writer.Write(state.NameTableByte);
        writer.Write(state.AttributeTableByte);
        writer.Write(state.TileDataLow);
        writer.Write(state.TileDataHigh);
        WriteBytes(writer, state.OamData);
        WriteBytes(writer, state.Sprites);
        WriteInts(writer, state.SpriteIndices);
        writer.Write(state.SpriteIndex);
        writer.Write(state.CountedSprites);
        WriteBytes(writer, state.FrameBuffer);
        writer.Write(state.CurrentCycle);
        writer.Write(state.CurrentScanline);
        writer.Write(state.ScanLineState);
        writer.Write(state.CycleState);
        writer.Write(state.Cycles);
        writer.Write(state.Nmi);
    }

    private static ADNES.PPU.PpuCoreState ReadPpuState(BinaryReader reader, byte[] patternTables, byte[] vram, byte[] paletteMemory)
    {
        var fineX = reader.ReadByte();
        var writeOrderToggle = reader.ReadByte();
        var frameOrderToggle = reader.ReadByte();
        var ppuCtrl = reader.ReadByte();
        var ppuMask = reader.ReadByte();
        var ppuStatus = reader.ReadByte();
        var oamAddr = reader.ReadByte();
        var ppuAddr = reader.ReadInt32();
        var ppuScroll = reader.ReadInt32();
        var ppuDataBuffer = reader.ReadByte();
        var tileShiftRegister = reader.ReadUInt64();
        var nameTableByte = reader.ReadByte();
        var attributeTableByte = reader.ReadByte();
        var tileDataLow = reader.ReadByte();
        var tileDataHigh = reader.ReadByte();
        var oamData = ReadBytes(reader);
        var sprites = ReadBytes(reader);
        var spriteIndices = ReadInts(reader);
        var spriteIndex = reader.ReadInt32();
        var countedSprites = reader.ReadInt32();
        var frameBuffer = ReadBytes(reader);
        var currentCycle = reader.ReadInt32();
        var currentScanline = reader.ReadInt32();
        var scanLineState = reader.ReadInt32();
        var cycleState = reader.ReadInt32();
        var cycles = reader.ReadInt64();
        var nmi = reader.ReadBoolean();

        return new ADNES.PPU.PpuCoreState(
            fineX,
            writeOrderToggle,
            frameOrderToggle,
            ppuCtrl,
            ppuMask,
            ppuStatus,
            oamAddr,
            ppuAddr,
            ppuScroll,
            ppuDataBuffer,
            tileShiftRegister,
            nameTableByte,
            attributeTableByte,
            tileDataLow,
            tileDataHigh,
            oamData,
            sprites,
            spriteIndices,
            spriteIndex,
            countedSprites,
            frameBuffer,
            currentCycle,
            currentScanline,
            scanLineState,
            cycleState,
            cycles,
            nmi,
            patternTables,
            vram,
            paletteMemory);
    }

    private NesCpuRegisters ToRegisters()
    {
        var status = Cpu.Status;
        return new NesCpuRegisters(
            Hex.FormatByte(Cpu.A),
            Hex.FormatByte(Cpu.X),
            Hex.FormatByte(Cpu.Y),
            Hex.FormatByte(Cpu.SP),
            Hex.FormatWord((ushort)Cpu.PC),
            Hex.FormatByte(status.ToByte()),
            status.Carry,
            status.Zero,
            status.InterruptDisable,
            status.DecimalMode,
            status.Overflow,
            status.Negative,
            Cpu.Cycles);
    }

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
        var opcode = Cpu.CPUMemory.ReadByte(address);
        var next = Cpu.CPUMemory.ReadByte((address + 1) & 0xFFFF);
        var high = Cpu.CPUMemory.ReadByte((address + 2) & 0xFFFF);
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
            _ => Instruction(address, [opcode], $".db ${opcode:X2}"),
        };
    }

    private DisassembledInstruction Instruction(ushort address, byte[] bytes, string text) =>
        new(Hex.FormatWord(address), Hex.FormatBytes(bytes), text, symbols.ResolveAddress(address));

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

    private static DebugResult<NesRomHeader> ParseHeader(byte[] bytes)
    {
        if (bytes.Length < 16 || bytes[0] != (byte)'N' || bytes[1] != (byte)'E' || bytes[2] != (byte)'S' || bytes[3] != 0x1A)
        {
            return DebugResult<NesRomHeader>.Failure("invalid_ines_header", "ROM is not an iNES file.");
        }

        var mapper = (bytes[7] & 0xF0) | ((bytes[6] >> 4) & 0x0F);
        return DebugResult<NesRomHeader>.Success(new NesRomHeader(mapper, bytes[4], bytes[5]));
    }

    private DebugResult<T> NoRom<T>() => DebugResult<T>.Failure("no_rom_loaded", "Load a NES ROM before using this tool.");

    private ADNES.CPU.Core Cpu => cpu ?? throw new InvalidOperationException("CPU is not initialized.");

    private ADNES.PPU.Core Ppu => ppu ?? throw new InvalidOperationException("PPU is not initialized.");

    private NESController Controller => controller ?? throw new InvalidOperationException("Controller is not initialized.");

    private sealed class ConditionContext(ManagedNesDebugSession session, NesCpuRegisters registers) : INesBreakpointConditionContext
    {
        public NesCpuRegisters Registers { get; } = registers;

        public DebugResult<byte> ReadByte(ushort address)
        {
            try
            {
                return DebugResult<byte>.Success(session.ReadByte(address));
            }
            catch (Exception ex)
            {
                return DebugResult<byte>.Failure("read_memory_failed", ex.Message);
            }
        }
    }

    private readonly record struct WriteRecord(ushort Pc, byte Value, ulong Count);

    private readonly record struct WatchHit(ushort Address, WatchpointMode Mode, ushort Pc, byte? Value);

    private sealed record NesRomHeader(int Mapper, int PrgRomBanks, int ChrRomBanks);

    private static readonly uint[] NesPalette =
    [
        0x666666, 0x002A88, 0x1412A7, 0x3B00A4, 0x5C007E, 0x6E0040, 0x6C0600, 0x561D00,
        0x333500, 0x0B4800, 0x005200, 0x004F08, 0x00404D, 0x000000, 0x000000, 0x000000,
        0xADADAD, 0x155FD9, 0x4240FF, 0x7527FE, 0xA01ACC, 0xB71E7B, 0xB53120, 0x994E00,
        0x6B6D00, 0x388700, 0x0C9300, 0x008F32, 0x007C8D, 0x000000, 0x000000, 0x000000,
        0xFFFEFF, 0x64B0FF, 0x9290FF, 0xC676FF, 0xF36AFF, 0xFE6ECC, 0xFE8170, 0xEA9E22,
        0xBCBE00, 0x88D800, 0x5CE430, 0x45E082, 0x48CDDE, 0x4F4F4F, 0x000000, 0x000000,
        0xFFFEFF, 0xC0DFFF, 0xD3D2FF, 0xE8C8FF, 0xFBC2FF, 0xFEC4EA, 0xFECCC5, 0xF7D8A5,
        0xE4E594, 0xCFEF96, 0xBDF4AB, 0xB3F3CC, 0xB5EBF2, 0xB8B8B8, 0x000000, 0x000000,
    ];
}
