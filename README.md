# Nes.Mcp

`Nes.Mcp` is a cross-platform .NET MCP server for inspecting and controlling NES ROMs in iNES format.

The default backend is a pure managed, vendored copy of the MIT-licensed [ADNES](https://github.com/enusbaum/ADNES) emulator core, wrapped with a synchronous debug session. The MCP server exposes CPU stepping, frame execution, deterministic controller input timelines, breakpoints/watchpoints, CPU memory reads/writes, PPU/OAM inspection, symbols, lightweight disassembly, savestates, write tracing, screen-region probes, and PNG screen capture.

An experimental AprNes backend is available for broader mapper coverage. By default `Nes.Mcp` runs in `auto` mode: ADNES is used for mappers 0-3, and AprNes is used for other mappers. The AprNes backend currently supports load/reset/step/frame/controller/memory/PPU/OAM/screen/tilemap workflows, while savestates, continue/break/watch execution stops, and write tracing still return `not_supported`.

## Build

Requirements:

- .NET 10 SDK

From the repo root:

```bash
dotnet build nes-debug-mcp.slnx
```

## Run

From the repo root:

```bash
dotnet run --project src/Nes.Debug.Mcp/Nes.Debug.Mcp.csproj
```

To force the experimental AprNes backend for every ROM:

```bash
NES_MCP_EMULATOR_BACKEND=aprnes dotnet run --project src/Nes.Debug.Mcp/Nes.Debug.Mcp.csproj
```

Valid backend values are `auto`, `adnes`, and `aprnes`.

## Connect An MCP Client

Use stdio transport. For development against this local checkout:

```json
{
  "mcpServers": {
    "nes_debug": {
      "command": "dotnet",
      "args": [
        "run",
        "--no-restore",
        "--project",
        "/home/jmn/Repos/NesMcp/src/Nes.Debug.Mcp/Nes.Debug.Mcp.csproj"
      ],
      "startup_timeout_sec": 60,
      "tool_timeout_sec": 60
    }
  }
}
```

Once the tool is packed and published, the path-independent form should be:

```json
{
  "mcpServers": {
    "nes_debug": {
      "command": "dnx",
      "args": ["Nes.Mcp", "--yes"]
    }
  }
}
```

## Tools

Implemented tools:

- `load_rom`
- `save_state`
- `load_state`
- `reset`
- `step_instruction`
- `step_over`
- `step_out`
- `run_frame`
- `set_controller`
- `set_joypad`
- `press_buttons`
- `run_input_timeline`
- `continue_until_break`
- `run_until_condition`
- `set_breakpoint`
- `clear_breakpoint`
- `list_breakpoints`
- `set_watchpoint`
- `set_watchpoint_range`
- `clear_watchpoint`
- `list_watchpoints`
- `get_state`
- `read_registers`
- `read_memory`
- `write_memory`
- `disassemble`
- `load_symbols`
- `resolve_symbol`
- `read_symbol`
- `dump_oam`
- `read_ppu_state`
- `capture_screen`
- `find_last_writer`
- `find_last_writers`
- `trace_until_write`
- `trace_until_write_range`
- `read_screen_region`
- `dump_tilemap`
- `dump_tileset`

See [docs/mcp-tools.md](docs/mcp-tools.md) for schemas and examples.

## Current Limitations

- The default `auto` backend uses ADNES for NROM, MMC1, UxROM, and CNROM, then falls back to AprNes for broader mapper coverage, including MMC3.
- The experimental AprNes backend does not yet implement the full debug-control surface.
- Debug stepping, conditional breakpoints, and watchpoints are implemented in the managed session loop.
- Disassembly is intentionally small and currently covers the opcodes most useful for first smoke/debug work; unknown opcodes are returned as `.db`.
- Symbol parsing is intentionally simple: `BANK:ADDR Name` and `ADDR Name` lines with `;` or `#` comments.
- Savestates capture CPU registers, CPU RAM, PPU state, OAM, pattern tables, nametables/palette memory, and framebuffer. Mapper-specific bank-register state is not yet a stable cross-mapper format, so savestates are best treated as frame-boundary debug snapshots.

## Validate

```bash
dotnet test nes-debug-mcp.slnx -m:1
git diff --check
```

## Third-Party Code

The emulator core under `src/Nes.Debug.Emulator/Adnes/` is vendored from ADNES by Eric Nusbaum and is MIT licensed. See [src/Nes.Debug.Emulator/ADNES-LICENSE.txt](src/Nes.Debug.Emulator/ADNES-LICENSE.txt).

The emulator core under `src/Nes.Debug.Emulator/AprNes/NesCore/` is vendored from [AprNes](https://github.com/erspicu/AprNes) and is WTFPL licensed. See [src/Nes.Debug.Emulator/AprNes/APRNES-LICENSE.txt](src/Nes.Debug.Emulator/AprNes/APRNES-LICENSE.txt).
