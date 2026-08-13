# Private ROM corpus qualification

`Nes.Corpus.Qualification` runs a bounded compatibility workflow over a local directory and writes one aggregate JSON object to standard output. It reads direct `.nes` files and `.nes` entries in ZIP archives without modifying or permanently extracting the source corpus.

## Invocation

Build the MCP server and qualification tool in the same configuration, then run:

```console
dotnet tools/Nes.Corpus.Qualification/bin/Release/net10.0/Nes.Corpus.Qualification.dll \
  --root <local-corpus-directory> \
  --server src/Nes.Debug.Mcp/bin/Release/net10.0/Nes.Mcp.dll \
  --expected-total <count> \
  --expect-mapper <header-mapper>=<count> \
  [--expect-mapper <header-mapper>=<count> ...]
```

The expected per-mapper counts are mandatory and must sum to `--expected-total`. A missing or unexpected cohort, a qualification failure, a timeout, a worker/protocol failure, or incomplete independent mapper 0-3 coverage produces a nonzero exit code.

The primary cohort launches each packaged MCP server with `NES_MCP_EMULATOR_BACKEND` absent, even if the qualification parent inherited that variable, and accepts the run only when `get_state` observes AprNes plus safe backend/server versions and its debug-cycle limit. The independent mapper 0-3 recovery smoke remains a separate forced `adnes` launch. The aggregate reports the observed AprNes and ADNES identities, not these internal launch modes.

## Configuration

Optional bounds are:

| Option | Default | Meaning |
| --- | ---: | --- |
| `--wall-timeout-seconds` | 30 | Hard wall limit for each ROM worker and its complete process tree; kill/reap and stream cleanup get one additional bounded second. |
| `--staging-timeout-seconds` | 10 | Cooperative streaming/decompression deadline inside the hard worker wall. |
| `--max-image-bytes` | 8,388,608 | Maximum observed and staged image size. |
| `--max-frames` | 4 | Frame limit for each fixed workflow frame operation. |
| `--max-instructions` | 10,000 | Instruction limit for each fixed workflow instruction operation. |
| `--max-trace-events` | 128 | Maximum returned AprNes PPU trace events. |

The schema also records the fixed workflow envelope: six frame operations, two instruction operations, and the MCP trace engine ceiling of 100,000 instructions per requested trace frame. These make the per-operation bounds auditable without representing them as a single cumulative per-ROM budget. Staging timeout must not exceed the hard wall timeout. Product limits cap frames at 600, instructions at 10,000,000, and trace events at 10,000.

## Aggregate schema

Schema version 2 contains only:

- an explicit `succeeded` gate, true only when AprNes, expected-cohort, and independent ADNES gates all pass;
- discovery, valid, attempted, passed, and failed totals;
- attempted/passed/failed counts by raw iNES/NES 2.0 header mapper;
- closed skipped and failure-category counts, with an optional header mapper on failures;
- total and maximum per-ROM elapsed milliseconds;
- AprNes and ADNES backend/server build versions;
- configured and fixed workflow bounds;
- the expected total and per-header-mapper cohort;
- independent ADNES attempted/passed/failed counts by header mapper 0-3.

Structurally valid trainer and NES 2.0 images count as valid and attempted, but currently fail with `UnsupportedFormat` before an emulator starts. They are never reported as skipped.

It does not contain ROM bytes, names, source or archive paths, captures, hashes, exception text or stacks, child output, arguments, or temporary paths.
