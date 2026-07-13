using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AprNes;

public readonly record struct NesCoreDebugRegisters(
    byte A,
    byte X,
    byte Y,
    byte Sp,
    ushort Pc,
    byte Status,
    bool Carry,
    bool Zero,
    bool InterruptDisable,
    bool DecimalMode,
    bool Overflow,
    bool Negative,
    ulong Cycles);

public readonly record struct NesCoreDebugPpuState(
    byte PpuCtrl,
    byte PpuMask,
    byte PpuStatus,
    byte OamAddr,
    ushort V,
    ushort T,
    byte FineX,
    bool WriteToggle,
    int Scanline,
    int Cycle,
    bool Nmi,
    bool RenderingEnabled,
    bool SpritesEnabled,
    bool BackgroundEnabled,
    ulong PpuCycles);

public readonly record struct NesCoreDebugPpuRegisterSnapshot(
    int Frame,
    int Scanline,
    int Dot,
    bool VBlank,
    bool RenderingActive,
    ushort V,
    ushort T,
    byte X,
    bool W);

public readonly record struct NesCoreDebugPpuRegisterWrite(
    ushort Address,
    byte Value,
    ushort Pc,
    ulong CpuCycle,
    NesCoreDebugPpuRegisterSnapshot Before,
    NesCoreDebugPpuRegisterSnapshot After);

unsafe public partial class NesCore
{
    private const int DebugScreenWidth = 256;
    private const int DebugScreenHeight = 240;
    private const int MaxCpuCyclesPerInstruction = 64;
    private const int MaxCpuCyclesPerReset = 128;
    private const int MaxCpuCyclesPerFrame = 1_000_000;

    private static ulong debugCpuCycles;
    private static bool debugTimingInitialized;
    private static ushort debugCurrentInstructionPc;
    private static Action<ushort, byte, ushort>? debugWriteObserver;
    private static Action<ushort, ushort>? debugReadObserver;
    private static Action<NesCoreDebugPpuRegisterWrite>? debugPpuRegisterWriteObserver;

    public static bool DebugLoad(byte[] romBytes)
    {
        HeadlessMode = true;
        AnalogEnabled = false;
        exit = false;
        Region = RegionType.NTSC;
        debugCpuCycles = 0;
        debugTimingInitialized = false;
        debugCurrentInstructionPc = 0;
        DebugSetMemoryObservers(null, null);
        DebugSetPpuRegisterWriteObserver(null);

        if (!init(romBytes))
        {
            return false;
        }

        BindDebugTiming();
        RunResetSequence();
        return true;
    }

    public static void DebugStepInstruction()
    {
        debugCurrentInstructionPc = r_PC;
        var observedBody = false;
        for (var i = 0; i < MaxCpuCyclesPerInstruction; i++)
        {
            StepCpuCycle();
            if (operationCycle != 0)
            {
                observedBody = true;
            }
            else if (observedBody)
            {
                return;
            }
        }

        throw new InvalidOperationException("Instruction did not complete within the debug cycle limit.");
    }

    public static int DebugRunFrame()
    {
        var startFrame = frame_count;
        for (var i = 0; i < MaxCpuCyclesPerFrame && frame_count == startFrame; i++)
        {
            StepCpuCycle();
        }

        return frame_count - startFrame;
    }

    public static NesCoreDebugRegisters DebugReadRegisters()
    {
        var status = GetFlag();
        return new NesCoreDebugRegisters(
            r_A,
            r_X,
            r_Y,
            r_SP,
            r_PC,
            status,
            flagC != 0,
            flagZ != 0,
            flagI != 0,
            flagD != 0,
            flagV != 0,
            flagN != 0,
            debugCpuCycles);
    }

    public static byte DebugReadCpu(ushort address) => CpuRead(address);

    public static void DebugWriteCpu(ushort address, byte value) => CpuWrite(address, value);

    public static void DebugSetMemoryObservers(
        Action<ushort, byte, ushort>? writeObserver,
        Action<ushort, ushort>? readObserver)
    {
        debugWriteObserver = writeObserver;
        debugReadObserver = readObserver;
    }

    public static void DebugSetPpuRegisterWriteObserver(Action<NesCoreDebugPpuRegisterWrite>? observer) =>
        debugPpuRegisterWriteObserver = observer;

    private static NesCoreDebugPpuRegisterSnapshot DebugReadPpuRegisterSnapshot()
    {
        var renderingEnabled = ShowBackGround_Instant || ShowSprites_Instant;
        return new NesCoreDebugPpuRegisterSnapshot(
            frame_count,
            scanline,
            ppu_cycles_x,
            isVblank,
            renderingEnabled && (scanline is >= 0 and < 240 || scanline == preRenderLine),
            (ushort)vram_addr,
            (ushort)vram_addr_internal,
            (byte)FineX,
            vram_latch);
    }

    private static void DebugObservePpuRegisterWrite(
        ushort address,
        byte value,
        ushort pc,
        ulong cpuCycle,
        NesCoreDebugPpuRegisterSnapshot before)
    {
        debugPpuRegisterWriteObserver?.Invoke(
            new NesCoreDebugPpuRegisterWrite(
                address,
                value,
                pc,
                cpuCycle,
                before,
                DebugReadPpuRegisterSnapshot()));
    }

    public static byte DebugReadPpu(ushort address) => PpuBusRead(address);

    public static byte[] DebugReadOam()
    {
        var bytes = new byte[256];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = spr_ram[i];
        }

        return bytes;
    }

    public static byte[] DebugReadPaletteIndices()
    {
        var bytes = new byte[DebugScreenWidth * DebugScreenHeight];
        DebugCopyPaletteIndices(bytes);
        return bytes;
    }

    public static void DebugCopyPaletteIndices(Span<byte> destination)
    {
        if (destination.Length < DebugScreenWidth * DebugScreenHeight)
        {
            throw new ArgumentException($"Destination must contain at least {DebugScreenWidth * DebugScreenHeight} bytes.", nameof(destination));
        }

        var frame = destination[..(DebugScreenWidth * DebugScreenHeight)];
        if (ntsc_rowPalettes == null)
        {
            frame.Clear();
            return;
        }

        for (var i = 0; i < frame.Length; i++)
        {
            frame[i] = (byte)(ntsc_rowPalettes[i] & 0x3F);
        }
    }

    public static uint[] DebugReadRgbFrame()
    {
        var pixels = new uint[DebugScreenWidth * DebugScreenHeight];
        if (digitalFrameRgb == null)
        {
            return pixels;
        }

        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = digitalFrameRgb[i] & 0x00FFFFFF;
        }

        return pixels;
    }

    public static NesCoreDebugPpuState DebugReadPpuState()
    {
        var status = (byte)(
            (isVblank ? 0x80 : 0) |
            (isSprite0hit ? 0x40 : 0) |
            (isSpriteOverflow ? 0x20 : 0));

        return new NesCoreDebugPpuState(
            ppuCtrlRegister,
            ppuMaskRegister,
            status,
            spr_ram_add,
            (ushort)vram_addr,
            (ushort)vram_addr_internal,
            (byte)FineX,
            vram_latch,
            scanline,
            ppu_cycles_x,
            NMILine,
            (ppuMaskRegister & 0x18) != 0,
            (ppuMaskRegister & 0x10) != 0,
            (ppuMaskRegister & 0x08) != 0,
            debugCpuCycles * 3);
    }

    public static void DebugSaveState(BinaryWriter writer)
    {
        WriteStateFields(writer, GetCoreStateFields(), null);
        WriteMapperState(writer);
        WriteUnmanagedBlocks(writer);
    }

    public static void DebugLoadState(BinaryReader reader)
    {
        ReadStateFields(reader, GetCoreStateFieldMap(), null);
        ReadMapperState(reader);
        ReadUnmanagedBlocks(reader);
        MapperObj?.UpdateCHRBanks();
        RebuildPaletteCache();
        UpdateIRQLine();
    }

    private static void RunResetSequence()
    {
        for (var i = 0; i < MaxCpuCyclesPerReset; i++)
        {
            if (!doReset && operationCycle == 0)
            {
                return;
            }

            StepCpuCycle();
        }

        throw new InvalidOperationException("Reset sequence did not complete within the debug cycle limit.");
    }

    private static void StepCpuCycle()
    {
        BindDebugTiming();
        MasterClockTickUnrolledNTSC();
        debugCpuCycles++;
    }

    private static void BindDebugTiming()
    {
        if (debugTimingInitialized)
        {
            return;
        }

        if (isFDS)
        {
            nestedTick7Fn = &NestedTick7_NTSC;
            nestedTick2Fn = &NestedTick2_NTSC;
            WarmUpFDS();
        }
        else
        {
            nestedTick7Fn = &NestedTick7_NTSC;
            nestedTick2Fn = &NestedTick2_NTSC;
            WarmUpNTSC();
        }

        debugTimingInitialized = true;
    }

    private static IReadOnlyList<FieldInfo> GetCoreStateFields() =>
        typeof(NesCore)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(IsSupportedStateField)
            .OrderBy(StateFieldKey, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyDictionary<string, FieldInfo> GetCoreStateFieldMap() =>
        GetCoreStateFields().ToDictionary(StateFieldKey, StringComparer.Ordinal);

    private static IReadOnlyList<FieldInfo> GetInstanceStateFields(Type type)
    {
        var fields = new List<FieldInfo>();
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            fields.AddRange(current.GetFields(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly));
        }

        return fields
            .Where(IsSupportedStateField)
            .OrderBy(StateFieldKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsSupportedStateField(FieldInfo field)
    {
        if (field.IsLiteral || field.IsInitOnly)
        {
            return false;
        }

        var type = field.FieldType;
        if (type.IsPointer || type.IsByRef || type.IsFunctionPointer || typeof(Delegate).IsAssignableFrom(type))
        {
            return false;
        }

        if (IsSupportedScalarStateType(type))
        {
            return true;
        }

        return type.IsArray &&
               type.GetArrayRank() == 1 &&
               IsSupportedScalarStateType(type.GetElementType()!);
    }

    private static bool IsSupportedScalarStateType(Type type) =>
        type.IsEnum ||
        type == typeof(bool) ||
        type == typeof(byte) ||
        type == typeof(sbyte) ||
        type == typeof(short) ||
        type == typeof(ushort) ||
        type == typeof(int) ||
        type == typeof(uint) ||
        type == typeof(long) ||
        type == typeof(ulong) ||
        type == typeof(float) ||
        type == typeof(double) ||
        type == typeof(char) ||
        type == typeof(string);

    private static string StateFieldKey(FieldInfo field) => $"{field.DeclaringType!.FullName}|{field.Name}";

    private static void WriteStateFields(BinaryWriter writer, IReadOnlyList<FieldInfo> fields, object? target)
    {
        writer.Write(fields.Count);
        foreach (var field in fields)
        {
            writer.Write(StateFieldKey(field));
            WriteStateValue(writer, field.FieldType, field.GetValue(target));
        }
    }

    private static void ReadStateFields(BinaryReader reader, IReadOnlyDictionary<string, FieldInfo> fields, object? target)
    {
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadString();
            if (!fields.TryGetValue(key, out var field))
            {
                _ = ReadStateValue(reader);
                continue;
            }

            var value = ReadStateValue(reader, field.FieldType);
            field.SetValue(target, value);
        }
    }

    private static void WriteMapperState(BinaryWriter writer)
    {
        writer.Write(MapperObj?.GetType().FullName ?? "");
        if (MapperObj is null)
        {
            writer.Write(0);
            return;
        }

        WriteStateFields(writer, GetInstanceStateFields(MapperObj.GetType()), MapperObj);
    }

    private static void ReadMapperState(BinaryReader reader)
    {
        var mapperType = reader.ReadString();
        if (MapperObj is null)
        {
            if (mapperType.Length != 0)
            {
                throw new InvalidDataException("State contains mapper data but no mapper is loaded.");
            }

            _ = reader.ReadInt32();
            return;
        }

        if (!string.Equals(mapperType, MapperObj.GetType().FullName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"State mapper '{mapperType}' does not match loaded mapper '{MapperObj.GetType().FullName}'.");
        }

        var fields = GetInstanceStateFields(MapperObj.GetType()).ToDictionary(StateFieldKey, StringComparer.Ordinal);
        ReadStateFields(reader, fields, MapperObj);
    }

    private static void WriteStateValue(BinaryWriter writer, Type type, object? value)
    {
        writer.Write(GetStateTypeCode(type));
        if (type.IsArray)
        {
            if (value is null)
            {
                writer.Write(-1);
                return;
            }

            var array = (Array)value;
            var elementType = type.GetElementType()!;
            writer.Write(array.Length);
            for (var i = 0; i < array.Length; i++)
            {
                WriteScalarStateValue(writer, elementType, array.GetValue(i));
            }

            return;
        }

        WriteScalarStateValue(writer, type, value);
    }

    private static object? ReadStateValue(BinaryReader reader)
    {
        var code = reader.ReadByte();
        if ((code & ArrayStateTypeFlag) != 0)
        {
            var elementCode = (byte)(code & ~ArrayStateTypeFlag);
            var length = reader.ReadInt32();
            if (length < 0)
            {
                return null;
            }

            var elementType = GetStateType(elementCode);
            var array = Array.CreateInstance(elementType, length);
            for (var i = 0; i < length; i++)
            {
                array.SetValue(ReadScalarStateValue(reader, elementCode), i);
            }

            return array;
        }

        return ReadScalarStateValue(reader, code);
    }

    private static object? ReadStateValue(BinaryReader reader, Type expectedType)
    {
        var code = reader.ReadByte();
        var expectedCode = GetStateTypeCode(expectedType);
        if (code != expectedCode)
        {
            throw new InvalidDataException($"State field type mismatch. Expected {expectedType.FullName}, got code {code}.");
        }

        if ((code & ArrayStateTypeFlag) != 0)
        {
            var length = reader.ReadInt32();
            if (length < 0)
            {
                return null;
            }

            var elementCode = (byte)(code & ~ArrayStateTypeFlag);
            var elementType = expectedType.GetElementType()!;
            var array = Array.CreateInstance(elementType, length);
            for (var i = 0; i < length; i++)
            {
                array.SetValue(ReadScalarStateValue(reader, elementCode, elementType), i);
            }

            return array;
        }

        return ReadScalarStateValue(reader, code, expectedType);
    }

    private static void WriteScalarStateValue(BinaryWriter writer, Type type, object? value)
    {
        if (type.IsEnum)
        {
            writer.Write(Convert.ToInt64(value));
            return;
        }

        if (type == typeof(bool)) { writer.Write((bool)value!); return; }
        if (type == typeof(byte)) { writer.Write((byte)value!); return; }
        if (type == typeof(sbyte)) { writer.Write((sbyte)value!); return; }
        if (type == typeof(short)) { writer.Write((short)value!); return; }
        if (type == typeof(ushort)) { writer.Write((ushort)value!); return; }
        if (type == typeof(int)) { writer.Write((int)value!); return; }
        if (type == typeof(uint)) { writer.Write((uint)value!); return; }
        if (type == typeof(long)) { writer.Write((long)value!); return; }
        if (type == typeof(ulong)) { writer.Write((ulong)value!); return; }
        if (type == typeof(float)) { writer.Write((float)value!); return; }
        if (type == typeof(double)) { writer.Write((double)value!); return; }
        if (type == typeof(char)) { writer.Write((char)value!); return; }
        if (type == typeof(string))
        {
            writer.Write(value is not null);
            if (value is not null)
            {
                writer.Write((string)value);
            }

            return;
        }

        throw new InvalidDataException($"Unsupported state type: {type.FullName}");
    }

    private static object? ReadScalarStateValue(BinaryReader reader, byte code)
    {
        return code switch
        {
            BoolStateType => reader.ReadBoolean(),
            ByteStateType => reader.ReadByte(),
            SByteStateType => reader.ReadSByte(),
            Int16StateType => reader.ReadInt16(),
            UInt16StateType => reader.ReadUInt16(),
            Int32StateType => reader.ReadInt32(),
            UInt32StateType => reader.ReadUInt32(),
            Int64StateType => reader.ReadInt64(),
            UInt64StateType => reader.ReadUInt64(),
            SingleStateType => reader.ReadSingle(),
            DoubleStateType => reader.ReadDouble(),
            CharStateType => reader.ReadChar(),
            StringStateType => reader.ReadBoolean() ? reader.ReadString() : null,
            EnumStateType => reader.ReadInt64(),
            _ => throw new InvalidDataException($"Unsupported state type code: {code}"),
        };
    }

    private static object? ReadScalarStateValue(BinaryReader reader, byte code, Type expectedType)
    {
        if (expectedType.IsEnum)
        {
            if (code != EnumStateType)
            {
                throw new InvalidDataException($"State field type mismatch. Expected {expectedType.FullName}, got code {code}.");
            }

            return Enum.ToObject(expectedType, reader.ReadInt64());
        }

        return ReadScalarStateValue(reader, code);
    }

    private static byte GetStateTypeCode(Type type)
    {
        if (type.IsArray)
        {
            return (byte)(ArrayStateTypeFlag | GetStateTypeCode(type.GetElementType()!));
        }

        if (type.IsEnum) { return EnumStateType; }
        if (type == typeof(bool)) { return BoolStateType; }
        if (type == typeof(byte)) { return ByteStateType; }
        if (type == typeof(sbyte)) { return SByteStateType; }
        if (type == typeof(short)) { return Int16StateType; }
        if (type == typeof(ushort)) { return UInt16StateType; }
        if (type == typeof(int)) { return Int32StateType; }
        if (type == typeof(uint)) { return UInt32StateType; }
        if (type == typeof(long)) { return Int64StateType; }
        if (type == typeof(ulong)) { return UInt64StateType; }
        if (type == typeof(float)) { return SingleStateType; }
        if (type == typeof(double)) { return DoubleStateType; }
        if (type == typeof(char)) { return CharStateType; }
        if (type == typeof(string)) { return StringStateType; }
        throw new InvalidDataException($"Unsupported state type: {type.FullName}");
    }

    private static Type GetStateType(byte code)
    {
        return code switch
        {
            BoolStateType => typeof(bool),
            ByteStateType => typeof(byte),
            SByteStateType => typeof(sbyte),
            Int16StateType => typeof(short),
            UInt16StateType => typeof(ushort),
            Int32StateType => typeof(int),
            UInt32StateType => typeof(uint),
            Int64StateType => typeof(long),
            UInt64StateType => typeof(ulong),
            SingleStateType => typeof(float),
            DoubleStateType => typeof(double),
            CharStateType => typeof(char),
            StringStateType => typeof(string),
            EnumStateType => typeof(long),
            _ => throw new InvalidDataException($"Unsupported state type code: {code}"),
        };
    }

    private static void WriteUnmanagedBlocks(BinaryWriter writer)
    {
        writer.Write(13);
        WriteByteBlock(writer, "NES_MEM", NES_MEM, 0x10000);
        WriteByteBlock(writer, "ppu_ram", ppu_ram, 0x4000);
        WriteByteBlock(writer, "spr_ram", spr_ram, 0x100);
        WriteByteBlock(writer, "secondaryOAM", secondaryOAM, 0x20);
        WriteByteBlock(writer, "corruptOamRow", corruptOamRow, 0x20);
        WriteByteBlock(writer, "ntsc_rowPalettes", ntsc_rowPalettes, DebugScreenWidth * DebugScreenHeight);
        WriteByteBlock(writer, "sprShiftL", sprShiftL, 8);
        WriteByteBlock(writer, "sprShiftH", sprShiftH, 8);
        WriteByteBlock(writer, "sprXCounter", sprXCounter, 8);
        WriteByteBlock(writer, "sprFetchAttr", sprFetchAttr, 8);
        WriteByteBlock(writer, "ntBankWritable", ntBankWritable, 4);
        WriteUIntBlock(writer, "digitalFrameRgb", digitalFrameRgb, DebugScreenWidth * DebugScreenHeight);
        WriteIntBlock(writer, "expansionChannels", expansionChannels, 8);
    }

    private static void ReadUnmanagedBlocks(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            switch (name)
            {
                case "NES_MEM": ReadByteBlock(reader, NES_MEM, 0x10000); break;
                case "ppu_ram": ReadByteBlock(reader, ppu_ram, 0x4000); break;
                case "spr_ram": ReadByteBlock(reader, spr_ram, 0x100); break;
                case "secondaryOAM": ReadByteBlock(reader, secondaryOAM, 0x20); break;
                case "corruptOamRow": ReadByteBlock(reader, corruptOamRow, 0x20); break;
                case "ntsc_rowPalettes": ReadByteBlock(reader, ntsc_rowPalettes, DebugScreenWidth * DebugScreenHeight); break;
                case "sprShiftL": ReadByteBlock(reader, sprShiftL, 8); break;
                case "sprShiftH": ReadByteBlock(reader, sprShiftH, 8); break;
                case "sprXCounter": ReadByteBlock(reader, sprXCounter, 8); break;
                case "sprFetchAttr": ReadByteBlock(reader, sprFetchAttr, 8); break;
                case "ntBankWritable": ReadByteBlock(reader, ntBankWritable, 4); break;
                case "digitalFrameRgb": ReadUIntBlock(reader, digitalFrameRgb, DebugScreenWidth * DebugScreenHeight); break;
                case "expansionChannels": ReadIntBlock(reader, expansionChannels, 8); break;
                default: SkipUnmanagedBlock(reader); break;
            }
        }
    }

    private static void WriteByteBlock(BinaryWriter writer, string name, byte* source, int length)
    {
        writer.Write(name);
        writer.Write(source != null);
        if (source == null)
        {
            return;
        }

        writer.Write(length);
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = source[i];
        }

        writer.Write(bytes);
    }

    private static void ReadByteBlock(BinaryReader reader, byte* target, int expectedLength)
    {
        if (!reader.ReadBoolean())
        {
            return;
        }

        var length = reader.ReadInt32();
        if (length != expectedLength)
        {
            throw new InvalidDataException($"State block length mismatch. Expected {expectedLength}, got {length}.");
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException("State block ended before all bytes were read.");
        }

        if (target == null)
        {
            throw new InvalidDataException("State block target memory is not allocated.");
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            target[i] = bytes[i];
        }
    }

    private static void WriteUIntBlock(BinaryWriter writer, string name, uint* source, int length)
    {
        writer.Write(name);
        writer.Write(source != null);
        if (source == null)
        {
            return;
        }

        writer.Write(length);
        for (var i = 0; i < length; i++)
        {
            writer.Write(source[i]);
        }
    }

    private static void ReadUIntBlock(BinaryReader reader, uint* target, int expectedLength)
    {
        if (!reader.ReadBoolean())
        {
            return;
        }

        var length = reader.ReadInt32();
        if (length != expectedLength)
        {
            throw new InvalidDataException($"State block length mismatch. Expected {expectedLength}, got {length}.");
        }

        if (target == null)
        {
            throw new InvalidDataException("State block target memory is not allocated.");
        }

        for (var i = 0; i < length; i++)
        {
            target[i] = reader.ReadUInt32();
        }
    }

    private static void WriteIntBlock(BinaryWriter writer, string name, int* source, int length)
    {
        writer.Write(name);
        writer.Write(source != null);
        if (source == null)
        {
            return;
        }

        writer.Write(length);
        for (var i = 0; i < length; i++)
        {
            writer.Write(source[i]);
        }
    }

    private static void ReadIntBlock(BinaryReader reader, int* target, int expectedLength)
    {
        if (!reader.ReadBoolean())
        {
            return;
        }

        var length = reader.ReadInt32();
        if (length != expectedLength)
        {
            throw new InvalidDataException($"State block length mismatch. Expected {expectedLength}, got {length}.");
        }

        if (target == null)
        {
            throw new InvalidDataException("State block target memory is not allocated.");
        }

        for (var i = 0; i < length; i++)
        {
            target[i] = reader.ReadInt32();
        }
    }

    private static void SkipUnmanagedBlock(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
        {
            return;
        }

        var length = reader.ReadInt32();
        _ = reader.ReadBytes(length);
    }

    private const byte ArrayStateTypeFlag = 0x80;
    private const byte BoolStateType = 1;
    private const byte ByteStateType = 2;
    private const byte SByteStateType = 3;
    private const byte Int16StateType = 4;
    private const byte UInt16StateType = 5;
    private const byte Int32StateType = 6;
    private const byte UInt32StateType = 7;
    private const byte Int64StateType = 8;
    private const byte UInt64StateType = 9;
    private const byte SingleStateType = 10;
    private const byte DoubleStateType = 11;
    private const byte CharStateType = 12;
    private const byte StringStateType = 13;
    private const byte EnumStateType = 14;
}
