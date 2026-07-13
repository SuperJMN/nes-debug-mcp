namespace Nes.Debug.Core;

public static class PpuStateBuilder
{
    public static PpuStateResult Build(
        byte ppuCtrl,
        byte ppuMask,
        byte ppuStatus,
        byte oamAddr,
        ushort v,
        ushort t,
        byte fineX,
        bool writeToggle,
        int scanline,
        int dot,
        bool nmi,
        long ppuCycles,
        TimelineCounters timeline)
    {
        var backgroundEnabled = (ppuMask & 0x08) != 0;
        var spritesEnabled = (ppuMask & 0x10) != 0;
        var renderingEnabled = backgroundEnabled || spritesEnabled;
        var vblank = (ppuStatus & 0x80) != 0;
        var nametableSelect = ppuCtrl & 0x03;

        return new PpuStateResult(
            Hex.FormatByte(ppuCtrl),
            Hex.FormatByte(ppuMask),
            Hex.FormatByte(ppuStatus),
            Hex.FormatByte(oamAddr),
            Hex.FormatWord(v),
            null,
            scanline,
            dot,
            nmi,
            renderingEnabled,
            spritesEnabled,
            backgroundEnabled,
            ppuCycles)
        {
            V = Hex.FormatWord(v),
            T = Hex.FormatWord(t),
            X = fineX & 0x07,
            W = writeToggle,
            VBlank = vblank,
            RenderingActive = renderingEnabled && (scanline is >= 0 and < 240 or 261),
            Control = new PpuControlState(
                nametableSelect,
                Hex.FormatWord((ushort)(0x2000 + nametableSelect * 0x400)),
                (ppuCtrl & 0x04) != 0 ? 32 : 1,
                (ppuCtrl & 0x08) != 0 ? "0x1000" : "0x0000",
                (ppuCtrl & 0x10) != 0 ? "0x1000" : "0x0000",
                (ppuCtrl & 0x20) != 0 ? "8x16" : "8x8",
                (ppuCtrl & 0x80) != 0),
            Mask = new PpuMaskState(
                (ppuMask & 0x01) != 0,
                (ppuMask & 0x02) != 0,
                (ppuMask & 0x04) != 0,
                backgroundEnabled,
                spritesEnabled,
                (ppuMask & 0x20) != 0,
                (ppuMask & 0x40) != 0,
                (ppuMask & 0x80) != 0),
            Status = new PpuStatusState(
                (ppuStatus & 0x20) != 0,
                (ppuStatus & 0x40) != 0,
                vblank),
            Timeline = timeline,
        };
    }
}
