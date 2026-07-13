namespace Nes.Debug.Core;

/// <summary>
/// Internal debugger primitive for copying the current 256x240 palette-index frame
/// without constructing the public screen-region JSON DTO.
/// </summary>
public interface IPaletteIndexFrameSource
{
    DebugResult<int> CopyPaletteIndexFrame(Memory<byte> destination);
}
