# Nes.Mcp

`Nes.Mcp` is a cross-platform .NET MCP server for inspecting and controlling NES ROMs in iNES format.

The MCP server exposes CPU stepping, frame execution, deterministic controller input timelines, breakpoints/watchpoints, CPU memory reads/writes, authoritative PPU/OAM inspection, continuous PPU-register tracing, correlated screen/RAM/PPU observation, symbols, lightweight disassembly, savestates, screen-region probes, and PNG screen capture.

By default `Nes.Mcp` runs in `auto` mode: the vendored MIT-licensed [ADNES](https://github.com/enusbaum/ADNES) backend is used for mappers 0-3, and the vendored [AprNes](https://github.com/erspicu/AprNes) backend is used for broader mapper coverage, including MMC3. AprNes now implements the MCP debug workflows exposed by the tool surface, including savestates, continue/break/watch execution stops, conditional runs, last-writer queries, and write tracing.

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

To force the AprNes backend for every ROM:

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
        "/absolute/path/to/NesMcp/src/Nes.Debug.Mcp/Nes.Debug.Mcp.csproj"
      ],
      "startup_timeout_sec": 60,
      "tool_timeout_sec": 60
    }
  }
}
```

After changing or updating the local checkout, restart the MCP client so it terminates and relaunches the stdio server. The `dnx` form uses the published NuGet package; use the local `dotnet run` form when validating source changes that have not been released as a package yet.

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
- `trace_ppu_register_writes`
- `read_screen_region`
- `observe_screen`
- `observe_execution`
- `dump_nametables`
- `dump_tilemap`
- `dump_tileset`

See [docs/mcp-tools.md](docs/mcp-tools.md) for schemas and examples.

## Current Limitations

- The default `auto` backend uses ADNES for NROM, MMC1, UxROM, and CNROM, then falls back to AprNes for broader mapper coverage, including MMC3.
- Debug stepping, conditional breakpoints, and watchpoints are implemented in the managed session loop around each backend.
- Disassembly is intentionally small and currently covers the opcodes most useful for first smoke/debug work; unknown opcodes are returned as `.db`.
- Symbol parsing is intentionally simple: `BANK:ADDR Name` and `ADDR Name` lines with `;` or `#` comments.
- Savestates are debugger snapshots, not a stable long-term archival format. They should be treated as version-bound to the current backend implementation.

## Validate

```bash
dotnet test nes-debug-mcp.slnx -m:1
git diff --check
```

## Third-Party Code

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for attribution, license notes, and acknowledgements for vendored emulator code and credited emulator reference work.

The main local license files are:

- [src/Nes.Debug.Emulator/ADNES-LICENSE.txt](src/Nes.Debug.Emulator/ADNES-LICENSE.txt)
- [src/Nes.Debug.Emulator/AprNes/APRNES-LICENSE.txt](src/Nes.Debug.Emulator/AprNes/APRNES-LICENSE.txt)
- [src/Nes.Debug.Emulator/AprNes/TRICNES-LICENSE.txt](src/Nes.Debug.Emulator/AprNes/TRICNES-LICENSE.txt)
- [src/Nes.Debug.Emulator/AprNes/MESEN2-GPL-3.0-LICENSE.txt](src/Nes.Debug.Emulator/AprNes/MESEN2-GPL-3.0-LICENSE.txt)
- [src/Nes.Debug.Emulator/AprNes/EMU2413-LICENSE.txt](src/Nes.Debug.Emulator/AprNes/EMU2413-LICENSE.txt)
