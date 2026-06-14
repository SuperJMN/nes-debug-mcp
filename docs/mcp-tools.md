# MCP Tools

All CPU addresses are 16-bit NES CPU addresses. Address strings accept `0xC000`, `$C000`, or `C000`.
PPU tile dump tools use PPU addresses.

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

## Input

- `set_controller`: sets currently held NES controller buttons.
- `set_joypad`: Game Boy-compatible alias for `set_controller`.
- `press_buttons`: holds buttons for a bounded number of frames, then releases them.

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
- `trace_until_write`: runs until an address is written or the limit is reached.
- `dump_oam`: dumps 64 OAM sprite entries.
- `read_ppu_state`: reads compact PPU register/counter state.
- `dump_tilemap`: dumps a 32x30 nametable from PPU memory.
  ```json
  { "address": "0x2000" }
  ```
- `dump_tileset`: dumps pattern table tile data from PPU memory.
  ```json
  { "address": "0x0000", "tileCount": 512 }
  ```
- `capture_screen`: returns the current 256x240 framebuffer as inline PNG image content.
