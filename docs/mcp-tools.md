# MCP Tools

All addresses are 16-bit NES CPU addresses. Address strings accept `0xC000`, `$C000`, or `C000`.

## `load_rom`

Loads an iNES ROM.

```json
{ "path": "/absolute/path/to/game.nes" }
```

## `reset`

Resets the loaded ROM to its reset vector.

```json
{}
```

## `step_instruction`

Runs one or more CPU instructions.

```json
{ "count": 1 }
```

## `run_frame`

Runs one or more PPU frames.

```json
{ "count": 1 }
```

## `set_controller`

Sets currently held buttons. Valid buttons are `a`, `b`, `select`, `start`, `up`, `down`, `left`, and `right`.

```json
{ "buttons": ["right", "a"] }
```

Pass an empty array to release every button.

## `press_buttons`

Holds buttons for a bounded number of frames, then releases them.

```json
{ "buttons": ["start"], "frameCount": 2 }
```

## `continue_until_break`

Runs until a breakpoint is reached or the instruction limit is exhausted.

```json
{ "maxInstructions": 1000000 }
```

## `set_breakpoint`

Sets an execution breakpoint.

```json
{ "address": "0xC000" }
```

## `clear_breakpoint`

Clears a breakpoint by id.

```json
{ "breakpointId": "bp-1" }
```

## `list_breakpoints`

Lists all breakpoints.

```json
{}
```

## `get_state`

Returns load status, mapper metadata, current PC, and total frame count.

```json
{}
```

## `read_registers`

Reads 6502 CPU registers.

```json
{}
```

## `read_memory`

Reads a bounded CPU memory range.

```json
{ "address": "0x8000", "length": 16 }
```

## `write_memory`

Writes bytes to CPU memory.

```json
{ "address": "0x0000", "bytes": [1, 2, 3] }
```

## `disassemble`

Disassembles a bounded number of instructions.

```json
{ "address": "0xC000", "instructionCount": 16 }
```

## `capture_screen`

Returns the current 256x240 framebuffer as inline PNG image content.

```json
{}
```
