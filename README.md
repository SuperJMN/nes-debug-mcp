# Nes.Mcp

`Nes.Mcp` is a cross-platform .NET MCP server for inspecting and controlling NES ROMs in iNES format.

The first backend is a pure managed, vendored copy of the MIT-licensed [ADNES](https://github.com/enusbaum/ADNES) emulator core, wrapped with a synchronous debug session. The MCP server exposes CPU stepping, frame execution, controller input, breakpoints, CPU memory reads/writes, lightweight disassembly, and inline PNG screen capture.

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
- `reset`
- `step_instruction`
- `run_frame`
- `set_controller`
- `press_buttons`
- `continue_until_break`
- `set_breakpoint`
- `clear_breakpoint`
- `list_breakpoints`
- `get_state`
- `read_registers`
- `read_memory`
- `write_memory`
- `disassemble`
- `capture_screen`

See [docs/mcp-tools.md](docs/mcp-tools.md) for schemas and examples.

## Current Limitations

- The initial backend supports the mappers implemented by ADNES: NROM, MMC1, UxROM, and CNROM.
- Debug stepping and breakpoints are implemented in the managed session loop.
- Disassembly is intentionally small and currently covers the opcodes most useful for first smoke/debug work; unknown opcodes are returned as `.db`.
- PPU register and OAM inspection are not exposed yet. Screen capture uses the rendered framebuffer.
- Savestates and watchpoints are not implemented yet.

## Validate

```bash
dotnet test nes-debug-mcp.slnx -m:1
git diff --check
```

## Third-Party Code

The emulator core under `src/Nes.Debug.Emulator/Adnes/` is vendored from ADNES by Eric Nusbaum and is MIT licensed. See [src/Nes.Debug.Emulator/ADNES-LICENSE.txt](src/Nes.Debug.Emulator/ADNES-LICENSE.txt).
