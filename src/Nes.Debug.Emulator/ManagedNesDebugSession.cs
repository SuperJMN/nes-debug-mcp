using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ADNES.Cartridge;
using ADNES.Controller;
using ADNES.Controller.Enums;
using Nes.Debug.Core;

namespace Nes.Debug.Emulator;

public sealed class ManagedNesDebugSession : INesDebugSession, IDisposable
{
    private const int ScreenWidth = ADNES.Emulator.Width;
    private const int ScreenHeight = ADNES.Emulator.Height;
    private const int MaxInstructionsPerFrame = 1_000_000;

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
    private readonly HashSet<NesButton> pressedButtons = [];
    private byte[] romBytes = [];
    private string? romPath;
    private string? romTitle;
    private int? mapper;
    private int prgRomBanks;
    private int chrRomBanks;
    private bool romLoaded;
    private bool disposed;
    private int cpuIdleCycles;
    private long totalFrames;
    private byte[] lastFrame = new byte[ScreenWidth * ScreenHeight];

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

            return DebugResult<LoadRomResult>.Success(new LoadRomResult(true, romTitle, mapper.Value, prgRomBanks, chrRomBanks));
        }
        catch (Exception ex)
        {
            romLoaded = false;
            return DebugResult<LoadRomResult>.Failure("load_rom_failed", ex.Message);
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
            new StepInstructionResult(Hex.FormatWord(pcBefore), registers.Value.Pc, registers.Value, disassembly));
    }

    public DebugResult<RunFrameResult> RunFrame(int count)
    {
        if (!romLoaded)
        {
            return NoRom<RunFrameResult>();
        }

        var framesRun = 0;
        var startFrames = totalFrames;

        try
        {
            while (framesRun < count)
            {
                var frameStart = totalFrames;
                for (var i = 0; i < MaxInstructionsPerFrame && totalFrames == frameStart; i++)
                {
                    StepMachineInstruction();
                    if (breakpoints.Contains((ushort)Cpu.PC))
                    {
                        var breakRegisters = ReadRegisters();
                        if (!breakRegisters.IsSuccess)
                        {
                            return DebugResult<RunFrameResult>.Failure(breakRegisters.Error!.Code, breakRegisters.Error.Message);
                        }

                        return DebugResult<RunFrameResult>.Success(
                            new RunFrameResult(framesRun, totalFrames, breakRegisters.Value, true));
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

        var registers = ReadRegisters();
        if (!registers.IsSuccess)
        {
            return DebugResult<RunFrameResult>.Failure(registers.Error!.Code, registers.Error.Message);
        }

        return DebugResult<RunFrameResult>.Success(
            new RunFrameResult(framesRun, totalFrames, registers.Value, breakpoints.Contains((ushort)Cpu.PC)));
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

        try
        {
            for (var i = 0; i < maxInstructions; i++)
            {
                var pc = (ushort)Cpu.PC;
                if (breakpoints.Contains(pc))
                {
                    return ContinueStopped("breakpoint");
                }

                StepMachineInstruction();
            }

            return ContinueStopped("instruction_limit");
        }
        catch (Exception ex)
        {
            return DebugResult<ContinueResult>.Failure("continue_failed", ex.Message);
        }
    }

    public DebugResult<BreakpointSetResult> SetBreakpoint(ushort address)
    {
        var info = breakpoints.Set(address);
        return DebugResult<BreakpointSetResult>.Success(new BreakpointSetResult(info.Id, info.Address, info.Enabled));
    }

    public DebugResult<ClearBreakpointResult> ClearBreakpoint(string breakpointId) =>
        DebugResult<ClearBreakpointResult>.Success(new ClearBreakpointResult(breakpoints.Clear(breakpointId)));

    public DebugResult<ListBreakpointsResult> ListBreakpoints()
    {
        var entries = breakpoints.All
            .Select(breakpoint => new BreakpointEntry(breakpoint.Id, breakpoint.Address, breakpoint.Enabled))
            .ToArray();

        return DebugResult<ListBreakpointsResult>.Success(new ListBreakpointsResult(entries));
    }

    public DebugResult<SessionStateResult> GetState()
    {
        return DebugResult<SessionStateResult>.Success(
            new SessionStateResult(
                romLoaded,
                romTitle,
                mapper,
                romLoaded ? Hex.FormatWord((ushort)Cpu.PC) : null,
                totalFrames));
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
            var bytes = new byte[length];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Cpu.CPUMemory.ReadByte((address + i) & 0xFFFF);
            }

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
                Cpu.CPUMemory.WriteByte((address + i) & 0xFFFF, bytes[i]);
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
        ppu.Reset();
        cpu.Cycles = 4;
        cpuIdleCycles = 0;
        totalFrames = 0;
        lastFrame = new byte[ScreenWidth * ScreenHeight];
        pressedButtons.Clear();
        romLoaded = true;
    }

    private int StepMachineInstruction()
    {
        var ticks = StepCpu();
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

    private DebugResult<ContinueResult> ContinueStopped(string reason)
    {
        var registers = ReadRegisters();
        return registers.IsSuccess
            ? DebugResult<ContinueResult>.Success(new ContinueResult(true, reason, registers.Value.Pc, registers.Value))
            : DebugResult<ContinueResult>.Failure(registers.Error!.Code, registers.Error.Message);
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

    private static DisassembledInstruction Instruction(ushort address, byte[] bytes, string text) =>
        new(Hex.FormatWord(address), Hex.FormatBytes(bytes), text);

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
