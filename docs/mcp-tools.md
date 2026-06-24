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
- `dump_oam`: dumps 64 OAM sprite entries.
- `read_ppu_state`: reads compact PPU register/counter state.
- `read_screen_region`: reads deterministic palette-index data and row hashes from a bounded 256x240 screen region.
  ```json
  { "x": 0, "y": 0, "width": 16, "height": 16, "format": "palette_indices" }
  ```
- `dump_tilemap`: dumps a 32x30 nametable from PPU memory.
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
