# Third-Party Notices

`Nes.Mcp` includes vendored emulator code and emulator reference work from other projects. Keep this notice with source and binary distributions.

## Vendored Emulator Cores

### ADNES

- Path: `src/Nes.Debug.Emulator/Adnes/`
- Upstream: https://github.com/enusbaum/ADNES
- Author: Eric P. Nusbaum
- License: MIT
- License file: `src/Nes.Debug.Emulator/ADNES-LICENSE.txt`

ADNES is still included in the current package. The `auto` backend uses it for mappers 0, 1, 2, and 3, and it can also be selected explicitly with `NES_MCP_EMULATOR_BACKEND=adnes`.

### AprNes

- Path: `src/Nes.Debug.Emulator/AprNes/NesCore/`
- Upstream: https://github.com/erspicu/AprNes
- License: WTFPL
- License file: `src/Nes.Debug.Emulator/AprNes/APRNES-LICENSE.txt`

The vendored AprNes core has local integration changes for headless MCP use, debug-session control, memory/register inspection, screen capture, watchpoints, tracing, and savestates.

## Additional Credits In The AprNes Tree

The AprNes source tree carries explicit comments crediting or referencing the projects below. They are included here so the package attribution matches the actual source comments, not only the top-level vendored directories.

### TriCNES

- Upstream: https://github.com/100thCoin/TriCNES
- Author: Chris "100th_Coin" Siebert
- License: MIT
- License file: `src/Nes.Debug.Emulator/AprNes/TRICNES-LICENSE.txt`
- Usage in this tree: AprNes source comments identify TriCNES timing, PPU, APU, controller, DMA, and interrupt behavior as reference or ported behavior in files such as `PPU.cs`, `ppu_new.cs`, `APU.cs`, `MEM.cs`, `CPU.cs`, and `JoyPad.cs`.

### Mesen2

- Upstream: https://github.com/SourMesen/Mesen2
- Author: Sour
- License: GPL-3.0-or-later
- License file: `src/Nes.Debug.Emulator/AprNes/MESEN2-GPL-3.0-LICENSE.txt`
- Usage in this tree: AprNes source comments cite Mesen2 mapper behavior in multiple mapper implementations. Some comments identify direct ports, for example `Mapper176.cs` describes a full port of Mesen2 `Waixing/Fk23C.h`, and `Emu2413.cs` notes that its C# port was made from Mesen2's copy.

Given those explicit source comments, the NuGet package license expression is intentionally conservative and includes `GPL-3.0-or-later` while this vendored AprNes tree remains as-is.

### emu2413

- Upstream: https://github.com/digital-sound-antiques/emu2413
- Author: Mitsutaka Okazaki
- License: MIT
- License file: `src/Nes.Debug.Emulator/AprNes/EMU2413-LICENSE.txt`
- Usage in this tree: `src/Nes.Debug.Emulator/AprNes/NesCore/Mapper/Emu2413.cs` provides the YM2413/OPLL synthesis engine used by VRC7 mapper support.

### NESdev Wiki

- Upstream: https://www.nesdev.org/wiki/Nesdev_Wiki
- Usage in this tree: NES hardware behavior reference material used by AprNes and common emulator development work.
