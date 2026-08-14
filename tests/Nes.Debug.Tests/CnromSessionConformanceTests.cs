using Nes.Debug.Core;
using Nes.Debug.Emulator;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nes.Debug.Tests;

public abstract class CnromSessionConformanceTests<TSession> : NesDebugSessionConformanceTests<TSession>
    where TSession : INesDebugSession, IDisposable
{
    protected override DebugContractRom CreateContractRom(
        byte[] program,
        int prgRomBanks = 2,
        int chrRomBanks = 1,
        NromMirroring mirroring = NromMirroring.Horizontal)
    {
        if (prgRomBanks != CnromTestRomBuilder.PrgRomBanks)
        {
            throw new ArgumentOutOfRangeException(nameof(prgRomBanks));
        }

        if (chrRomBanks is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(chrRomBanks));
        }

        if (chrRomBanks == 0)
        {
            var chrRamFixture = NromTestRomBuilder.CreateProgram(
                program,
                prgRomBanks,
                chrRomBanks,
                mirroring);
            chrRamFixture.Bytes[6] = (byte)((CnromTestRomBuilder.Mapper << 4) |
                (mirroring == NromMirroring.Vertical ? 0x01 : 0x00));
            return new DebugContractRom(
                chrRamFixture.Bytes,
                CnromTestRomBuilder.Mapper,
                prgRomBanks,
                chrRomBanks);
        }

        var fixture = CnromTestRomBuilder.CreateProgram(program, mirroring);
        return new DebugContractRom(
            fixture.Bytes,
            CnromTestRomBuilder.Mapper,
            CnromTestRomBuilder.PrgRomBanks,
            CnromTestRomBuilder.ChrRomBanks);
    }

    [Theory]
    [InlineData(NromMirroring.Horizontal)]
    [InlineData(NromMirroring.Vertical)]
    public void Chr_bank_selection_reset_and_rendering_are_observable(NromMirroring mirroring)
    {
        var fixture = CnromTestRomBuilder.CreateRenderingFixture(mirroring);
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        var load = session.LoadRom(rom.Path);
        AssertSuccess(load);
        Assert.Equal(CnromTestRomBuilder.Mapper, load.Value.Mapper);
        Assert.Equal(CnromTestRomBuilder.PrgRomBanks, load.Value.PrgRomBanks);
        Assert.Equal(CnromTestRomBuilder.ChrRomBanks, load.Value.ChrRomBanks);
        AssertSuccess(session.StepInstruction(fixture.InitializationInstructionCount));

        var framebufferHashes = new List<string>();
        for (var bank = 0; bank < CnromTestRomBuilder.ChrRomBanks; bank++)
        {
            AssertSuccess(session.WriteMemory(0x8000, [(byte)bank]));
            var tileset = session.DumpTileset(0x0000, 1);
            AssertSuccess(tileset);
            Assert.Equal(CnromTestRomBuilder.BankTileHex[bank], tileset.Value.Tiles[0].BytesHex);

            AssertSuccess(session.RunFrame(2));
            var ppu = session.ReadPpuState();
            var region = session.ReadScreenRegion(16, 16, 8, 8, "palette_indices_raw");
            var capture = session.CaptureScreen();
            AssertSuccess(ppu);
            Assert.Equal("0x0A", ppu.Value.PpuMask);
            Assert.True(ppu.Value.BackgroundEnabled);
            AssertSuccess(region);
            Assert.Equal(64, region.Value.Values?.Count);
            Assert.All(region.Value.Values!, value => Assert.Equal(CnromTestRomBuilder.BankPaletteIndices[bank], value));
            AssertSuccess(capture);
            framebufferHashes.Add(HashBytes(capture.Value.Data));
        }

        Assert.Equal(CnromTestRomBuilder.ChrRomBanks, framebufferHashes.Distinct(StringComparer.Ordinal).Count());

        AssertSuccess(session.WriteMemory(0x8000, [0x03]));
        AssertSuccess(session.Reset());
        var resetState = session.GetState();
        var resetTileset = session.DumpTileset(0x0000, 1);
        AssertSuccess(resetState);
        Assert.Equal(0UL, resetState.Value.Timeline.Frames);
        Assert.Equal(0UL, resetState.Value.Timeline.Instructions);
        AssertSuccess(resetTileset);
        Assert.Equal(CnromTestRomBuilder.BankTileHex[0], resetTileset.Value.Tiles[0].BytesHex);

        AssertSuccess(session.StepInstruction(fixture.InitializationInstructionCount));
        AssertSuccess(session.RunFrame(2));
        var resetRegion = session.ReadScreenRegion(16, 16, 8, 8, "palette_indices_raw");
        AssertSuccess(resetRegion);
        Assert.All(resetRegion.Value.Values!, value => Assert.Equal(CnromTestRomBuilder.BankPaletteIndices[0], value));
    }

    [Theory]
    [InlineData(NromMirroring.Horizontal)]
    [InlineData(NromMirroring.Vertical)]
    public void Supported_mirroring_maps_nametables_deterministically(NromMirroring mirroring)
    {
        var fixture = CnromTestRomBuilder.CreateMirroringFixture(mirroring);
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        var load = session.LoadRom(rom.Path);
        AssertSuccess(load);
        Assert.Equal(CnromTestRomBuilder.Mapper, load.Value.Mapper);
        AssertSuccess(session.StepInstruction(fixture.InitializationInstructionCount));
        var nametables = new[] { 0x2000, 0x2400, 0x2800, 0x2C00 }
            .Select(address => session.DumpTilemap((ushort)address))
            .ToArray();
        Assert.All(nametables, AssertSuccess);

        var expectedTiles = mirroring == NromMirroring.Horizontal
            ? new[] { "22", "22", "44", "44" }
            : ["33", "44", "33", "44"];
        Assert.Equal(expectedTiles, nametables.Select(result => FirstByteFromRow(result.Value.Rows[0])));
    }

    [Fact]
    public void Savestate_restores_selected_chr_bank_complete_state_and_deterministic_future()
    {
        var fixture = CnromTestRomBuilder.CreateSavestateFixture(NromMirroring.Vertical);
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var state = TemporaryTestFile.Empty("nesstate");
        using var session = CreateSession();

        AssertSuccess(session.LoadRom(rom.Path));
        AssertSuccess(session.RunFrame(2));
        AssertSuccess(session.WriteMemory(0x8000, [0x01]));
        AssertSuccess(session.RunFrame(2));
        var savedSnapshot = CaptureObservableSnapshot(session);
        Assert.Equal(CnromTestRomBuilder.BankTileHex[1], savedSnapshot.SelectedChrTile);
        AssertSuccess(session.SaveState(state.Path));

        AssertSuccess(session.WriteMemory(0x8000, [0x02]));
        AssertSuccess(session.RunFrame(1));
        var firstFuture = CaptureObservableSnapshot(session);
        Assert.Equal(CnromTestRomBuilder.BankTileHex[2], firstFuture.SelectedChrTile);
        Assert.NotEqual(savedSnapshot.CpuRamHash, firstFuture.CpuRamHash);
        Assert.NotEqual(savedSnapshot.PpuStateHash, firstFuture.PpuStateHash);
        Assert.NotEqual(savedSnapshot.OamHash, firstFuture.OamHash);
        Assert.NotEqual(savedSnapshot.NametablesHash, firstFuture.NametablesHash);
        Assert.NotEqual(savedSnapshot.SelectedChrTile, firstFuture.SelectedChrTile);
        Assert.NotEqual(savedSnapshot.FramebufferHash, firstFuture.FramebufferHash);
        Assert.NotEqual(savedSnapshot.Timeline, firstFuture.Timeline);

        AssertSuccess(session.WriteMemory(0x0000, [0xEE]));
        AssertSuccess(session.LoadState(state.Path));
        var restoredSnapshot = CaptureObservableSnapshot(session);
        Assert.Equal(savedSnapshot, restoredSnapshot);

        AssertSuccess(session.WriteMemory(0x8000, [0x02]));
        AssertSuccess(session.RunFrame(1));
        var replayedFuture = CaptureObservableSnapshot(session);
        Assert.Equal(firstFuture, replayedFuture);
    }

    [Fact]
    public void Repeated_bounded_frames_apply_each_chr_bank_without_stale_state()
    {
        var fixture = CnromTestRomBuilder.CreateRenderingFixture(NromMirroring.Horizontal);
        using var rom = TemporaryTestFile.FromBytes(fixture.Bytes);
        using var session = CreateSession();

        AssertSuccess(session.LoadRom(rom.Path));
        AssertSuccess(session.StepInstruction(fixture.InitializationInstructionCount));
        AssertSuccess(session.RunFrame(2));
        var initialState = session.GetState();
        AssertSuccess(initialState);
        var initialFrame = initialState.Value.Timeline.Frames;
        var regionHashes = new Dictionary<int, string>();

        for (var iteration = 0; iteration < 60; iteration++)
        {
            var bank = iteration % 3 + 1;
            AssertSuccess(session.WriteMemory(0x8000, [(byte)bank]));
            var run = session.RunFrame(1);
            var tileset = session.DumpTileset(0x0000, 1);
            var region = session.ReadScreenRegion(16, 16, 8, 8, "palette_indices_raw");

            AssertSuccess(run);
            Assert.Equal(1, run.Value.FramesRun);
            Assert.Equal(initialFrame + (ulong)iteration + 1, run.Value.Timeline.Frames);
            AssertSuccess(tileset);
            Assert.Equal(CnromTestRomBuilder.BankTileHex[bank], tileset.Value.Tiles[0].BytesHex);
            AssertSuccess(region);
            Assert.All(region.Value.Values!, value => Assert.Equal(CnromTestRomBuilder.BankPaletteIndices[bank], value));

            var regionHash = string.Join('|', region.Value.RowHashes);
            if (regionHashes.TryGetValue(bank, out var previousHash))
            {
                Assert.Equal(previousHash, regionHash);
            }
            else
            {
                regionHashes.Add(bank, regionHash);
            }
        }

        Assert.Equal(3, regionHashes.Count);
        Assert.Equal(3, regionHashes.Values.Distinct(StringComparer.Ordinal).Count());
        var finalState = session.GetState();
        AssertSuccess(finalState);
        Assert.Equal("AprNes", finalState.Value.Backend);
        Assert.Equal(initialFrame + 60, finalState.Value.Timeline.Frames);
    }

    private static CnromObservableSnapshot CaptureObservableSnapshot(INesDebugSession session)
    {
        var registers = session.ReadRegisters();
        var ram = session.ReadMemory(0x0000, 0x800);
        var ppu = session.ReadPpuState();
        var oam = session.ReadOam();
        var nametables = session.DumpNametables(includeDetails: true);
        var tileset = session.DumpTileset(0x0000, 1);
        var capture = session.CaptureScreen();
        var state = session.GetState();

        AssertSuccess(registers);
        AssertSuccess(ram);
        Assert.Equal(0x800, ram.Value.Bytes.Length);
        AssertSuccess(ppu);
        AssertSuccess(oam);
        Assert.Equal(64, oam.Value.Sprites.Count);
        AssertSuccess(nametables);
        Assert.True(nametables.Value.DetailsIncluded);
        Assert.Equal(4, nametables.Value.Nametables.Count);
        Assert.All(nametables.Value.Nametables, table => Assert.NotNull(table.Detail));
        AssertSuccess(tileset);
        Assert.Single(tileset.Value.Tiles);
        AssertSuccess(capture);
        AssertSuccess(state);

        return new CnromObservableSnapshot(
            registers.Value,
            HashBytes(ram.Value.Bytes),
            HashCanonical(ppu.Value),
            HashCanonical(oam.Value.Sprites),
            HashCanonical(nametables.Value.Nametables),
            tileset.Value.Tiles[0].BytesHex,
            HashBytes(capture.Value.Data),
            state.Value.Timeline);
    }

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static string HashCanonical<T>(T value) =>
        HashBytes(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)));

    private sealed record CnromObservableSnapshot(
        NesCpuRegisters Registers,
        string CpuRamHash,
        string PpuStateHash,
        string OamHash,
        string NametablesHash,
        string SelectedChrTile,
        string FramebufferHash,
        TimelineCounters Timeline);
}

[Collection(NesDebugSessionCollection.Name)]
public sealed class AprNesCnromSessionConformanceTests : CnromSessionConformanceTests<AprNesDebugSession>
{
    protected override AprNesDebugSession CreateSession() => new();

    protected override bool SupportsContinuousObservation => true;
}
