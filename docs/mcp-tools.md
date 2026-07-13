# MCP Tools

All CPU addresses are 16-bit NES CPU addresses. Address strings accept `0xC000`, `$C000`, or `C000`.
PPU tile dump tools use PPU addresses.
Execution and state results include a `timeline` object with cumulative `frames`, CPU `cycles`, and `instructions` since the last ROM load, reset, or loaded savestate.

## Execution

- `load_rom`: loads an iNES ROM.
  ```json
  { "path": "/absolute/path/to/game.nes" }
  ```
- `save_state`: saves a managed NES savestate.
  ```json
  { "path": "/tmp/state.nesstate" }
  ```
- `load_state`: loads a managed NES savestate.
  ```json
  { "path": "/tmp/state.nesstate" }
  ```
- `reset`: resets the loaded ROM to its reset vector.
- `step_instruction`: runs one or more CPU instructions.
  ```json
  { "count": 1 }
  ```
- `step_over`: steps over a `JSR`, or steps one instruction when not on `JSR`.
  ```json
  { "maxInstructions": 100000 }
  ```
- `step_out`: runs until the current 6502 subroutine returns.
  ```json
  { "maxInstructions": 100000 }
  ```
- `run_frame`: runs one or more PPU frames.
  ```json
  { "count": 1 }
  ```
- `observe_screen`: atomically runs up to 600 complete frames and returns a SHA-256 identity for every rendered frame plus compact changes from the preceding frame. `changedPixels` and `changedBounds` locate the visible change; `changedTileRows` contains only affected 8x8 tile rows, where bit N in `mask` identifies tile column N.
  ```json
  { "frameCount": 120 }
  ```

  To investigate a transient frame, save state before observing, find the suspicious `frameOffset`, reload the state, run to that offset, then use `read_screen_region`, `dump_tilemap`, `dump_tileset`, and `dump_oam` for exact evidence.
- `observe_execution`: AprNes-only atomic correlated observation. It holds the selected buttons for up to 600 frames and releases them in all exit paths. Each completed frame contains a framebuffer SHA-256 plus compact pixel/tile differences, bounded CPU-RAM probes, and optional full PPU state. The result also contains a bounded continuous PPU-register trace, initial/final hashes for all four logical nametables, breakpoint/stop status, applied limits, and final timeline counters.
  ```json
  {
    "frameCount": 120,
    "buttons": ["right"],
    "memoryProbes": [
      { "address": "$0000", "length": 16 },
      { "address": "$0600", "length": 32 }
    ],
    "includePpuState": true,
    "tracePpuWrites": true,
    "maxPpuEvents": 1000,
    "ppuRegisters": ["PPUCTRL", "PPUSCROLL", "PPUADDR", "PPUDATA"]
  }
  ```

  Limits are 16 probes, 64 bytes per probe, 256 probe bytes per frame, and 2,000 returned PPU events. Probes must stay in side-effect-free CPU RAM `$0000-$1FFF`. `ppuEventsObserved` continues counting after `ppuEvents` reaches `maxPpuEvents`; `ppuTraceTruncated` and `truncated` then become true without ending the frame run. A breakpoint can end the observation early, in which case `framesRun` and the per-frame array contain only completed frames.
- `continue_until_break`: runs until a breakpoint, watchpoint, or instruction limit.
  ```json
  { "maxInstructions": 1000000 }
  ```
- `run_until_condition`: runs until a register or memory condition is true, or a bounded stop condition is reached.
  ```json
  { "condition": "[0x0002] == 0x2A", "maxInstructions": 1000000, "maxFrames": 120 }
  ```

## Input

- `set_controller`: sets currently held NES controller buttons.
- `set_joypad`: Game Boy-compatible alias for `set_controller`.
- `press_buttons`: holds buttons for a bounded number of frames, then releases them.
- `run_input_timeline`: runs a bounded deterministic sequence of complete held-button frame steps atomically, releases all buttons at the end, and can collect optional observations per step.
  ```json
  {
    "steps": [
      { "frames": 60, "buttons": ["right"], "readRegisters": true },
      { "frames": 4, "buttons": ["right", "a"], "capture": true, "readPpuState": true }
    ]
  }
  ```

Valid buttons are `a`, `b`, `select`, `start`, `up`, `down`, `left`, and `right`.

```json
{ "buttons": ["right", "a"] }
```

## Breakpoints And Watchpoints

- `set_breakpoint`: sets an execution breakpoint, optionally conditional.
  ```json
  { "address": "0x8000", "condition": "A == 0x42" }
  ```
- `clear_breakpoint`: clears a breakpoint by id.
- `list_breakpoints`: lists all breakpoints.
- `set_watchpoint`: watches CPU memory reads, writes, or both.
  ```json
  { "address": "0x0002", "mode": "write" }
  ```
- `set_watchpoint_range`: watches a bounded CPU memory range.
  ```json
  { "address": "0x0000", "length": 16, "mode": "write" }
  ```
- `clear_watchpoint`: clears a watchpoint by id.
- `list_watchpoints`: lists all watchpoints.

Conditional breakpoints support one comparison: `<left> <operator> <constant>`.
Left operands are `A`, `X`, `Y`, `SP`, `STATUS`, `PC`, `[addr]`, or `[PC]`.
Operators are `==`, `!=`, `<`, `<=`, `>`, and `>=`.

## Inspection

- `get_state`: returns load status, mapper metadata, current PC, and total frame count.
- `read_registers`: reads 6502 CPU registers.
- `read_memory`: reads a bounded CPU memory range.
- `write_memory`: writes bytes to CPU memory.
- `disassemble`: disassembles a bounded number of instructions.
- `load_symbols`: loads a simple symbol file.
- `resolve_symbol`: resolves a loaded symbol name.
- `read_symbol`: reads CPU memory at a loaded symbol.
- `find_last_writer`: returns the last observed write to an address.
- `find_last_writers`: returns the last observed writes in a bounded address range.
  ```json
  { "address": "0x0000", "length": 16 }
  ```
- `trace_until_write`: runs until an address is written or the limit is reached.
- `trace_until_write_range`: runs until any address in a bounded range is written or the limit is reached, returning the concrete hit address, nearby disassembly, PPU state, and timeline.
  ```json
  { "address": "0x0000", "length": 16, "maxInstructions": 1000000 }
  ```
- `trace_ppu_register_writes`: AprNes-only atomic trace of every selected CPU write to `$2000-$2007` while running up to 600 frames. The default filter is `PPUCTRL`, `PPUSCROLL`, `PPUADDR`, and `PPUDATA`; register names or exact addresses may select any of the eight registers. Input is held for the trace and released afterward.
  ```json
  {
    "frameCount": 2,
    "maxEvents": 1000,
    "registers": ["PPUCTRL", "$2005", "$2006", "PPUDATA"],
    "buttons": ["right"]
  }
  ```

  Events remain in bus-write order and contain the value, writing instruction PC, `frameOffset`, absolute frame, CPU-cycle counter, completed-instruction counter, and immediate `before`/`after` PPU snapshots. Each snapshot includes scanline, dot, VBlank, rendering-active state, and the authoritative `v`, `t`, `x`, and `w` registers. `before` is captured directly before the selected PPU write handler and `after` immediately after it returns; later dot-driven PPU effects are not attributed to that write. `cpuCycle` is the cumulative count of completed AprNes CPU cycles on entry to that write, while `instructionCounter` counts instructions completed before the currently executing write instruction.

  At most 10,000 events are returned. Execution continues to its frame/breakpoint bound after that cap; `eventsObserved` reports the full selected-write count and `truncated` reports that the event payload was capped. `eventCount` is the number actually returned.
- `dump_oam`: dumps 64 OAM sprite entries.
- `read_ppu_state`: reads raw `ppuctrl`, `ppumask`, `ppustatus`, and `oamaddr`; decoded `control`, `mask`, and `status` bits; authoritative loopy scrolling/address state `v`, `t`, `x`, and `w`; scanline/dot, VBlank, rendering state, NMI state, PPU-cycle count, and timeline. `v` is the current VRAM address, `t` is the temporary VRAM address, `x` is fine-X scroll, and `w` is false for the first `$2005/$2006` write and true for the second. The legacy `ppuaddr` field aliases `v`; legacy `ppuscroll` is null because `t` alone is not a valid scroll value. The legacy `cycle` field is the current PPU dot.
- `read_screen_region`: reads deterministic palette-index data and row hashes from a bounded 256x240 screen region. `palette_indices` returns raw `values` automatically for regions up to 1,024 pixels and otherwise returns a compact histogram plus row hashes. Use `palette_indices_raw` to explicitly return every palette index for a larger region, including a full 256x240 frame (61,440 values).
  ```json
  { "x": 0, "y": 0, "width": 16, "height": 16, "format": "palette_indices" }
  ```
- `dump_nametables`: reads the complete `$2000-$2FFF` nametable window once, then reports all four logical nametables (`$2000`, `$2400`, `$2800`, and `$2C00`) from that same snapshot. Every table has SHA-256 identities for the complete 1,024 bytes, 960 tile bytes, and 64 attribute bytes. Set `includeDetails` to true to also return the tile and attribute rows; leave it false for compact comparisons.
  ```json
  { "includeDetails": false }
  ```
- `dump_tilemap`: dumps a complete 32x30 nametable from PPU memory together with its 8x8 attribute table. `rows` contains tile IDs; `attributeAddress` and `attributeRows` contain the 64 physical palette-selection bytes. `address` must be `0x2000`, `0x2400`, `0x2800`, or `0x2C00`.
  ```json
  { "address": "0x2000" }
  ```
- `dump_tileset`: dumps pattern table tile data from PPU memory.
  ```json
  { "address": "0x0000", "tileCount": 512 }
  ```
- `capture_screen`: returns the current 256x240 framebuffer as inline PNG image content.
  ```json
  { "path": "artifacts/frame.png", "includeMetadata": true }
  ```

  When `path` is omitted, the tool returns inline image content. When `path` is provided, it must be a relative `.png` path under the current working directory.

## Replaying A Suspicious Frame

1. Call `save_state` immediately before the suspect sequence.
2. Use `observe_execution` when input, RAM, PPU writes, and visible corruption must be correlated; use `observe_screen` when framebuffer changes alone are enough.
3. Note the suspicious `frameOffset`, hashes, changed bounds, RAM values, and any nearby PPU events.
4. Call `load_state`, replay `frameOffset - 1` complete frames, and use `trace_ppu_register_writes` for the focal frame.
5. Reload once more and stop at the relevant PC or RAM condition when instruction-level inspection is needed. Use `read_ppu_state`, `read_screen_region` with `palette_indices_raw`, `dump_nametables`, `dump_oam`, and `dump_tileset` to collect the exact state.

Savestates make repeated observations deterministic within the same backend and build. These tools and the AprNes integration tests validate NesMcp's instrumentation and contracts; they do not replace a final smoke test in FCEUmm or another independent emulator when emulator-accuracy parity matters.

## Backend Support

`read_ppu_state`, `read_screen_region`, `observe_screen`, and nametable dumps use the capabilities exposed by the active backend. The exact continuous bus-write correlation required by `trace_ppu_register_writes` and `observe_execution` is currently implemented by AprNes. Managed/ADNES sessions return an explicit `not_supported` result for those two tools; set `NES_MCP_EMULATOR_BACKEND=aprnes` when the ROM would otherwise select ADNES.
