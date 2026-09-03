# FEMDesignDumper

A small Windows command-line tool that opens a [FEM-Design](https://strusoft.com/) model
in the background and dumps its results to CSV and JSON.

It is built for automation: point it at a model file and it writes a predictable set of
files plus a `manifest.json` describing exactly what was produced — easy for a script, a CI
job, or an AI agent to consume.

It drives FEM-Design through StruSoft's official [`FemDesign.Core`](https://www.nuget.org/packages/FemDesign.Core)
API over the real-time pipe connection, so results are identical to what you would get in
the GUI.

## What it does

1. Launches FEM-Design headlessly (no window; via `FD_NOGUI`).
2. Opens a `.str` or `.struxml` model.
3. Optionally runs static or eigenfrequency analysis.
4. Extracts a model summary and a curated set of result tables.
5. Writes everything to an output folder and exits.

## Requirements

- **FEM-Design 25** installed (default: `C:\Program Files\StruSoft\FEM-Design 25\`).
  A valid FEM-Design licence is required — the tool automates the installed application.
- **.NET SDK** (to build) — the project targets **.NET Framework 4.8**, which the
  `FemDesign.Core` package requires. The .NET 4.8 targeting pack must be present
  (it ships with Visual Studio and recent Windows SDKs).

The FEM-Design version must match the `FemDesign.Core` version (both 25.x here). If you
upgrade FEM-Design, bump the package version in the `.csproj` to match.

## Build

```bash
dotnet build -c Release
```

The executable is produced at:

```
src/FEMDesignDumper/bin/Release/net48/FEMDesignDumper.exe
```

## Usage

```
FEMDesignDumper --model <path> [options]
```

| Option | Description |
| --- | --- |
| `-m, --model <path>` | **Required.** FEM-Design model (`.str` or `.struxml`). |
| `-o, --out <dir>` | Output directory. Default: `<model>_dump` next to the model. |
| `--calc <mode>` | `none` (default), `static`, or `freq`. |
| `--freq-shapes <n>` | Number of eigenshapes for `--calc freq`. Default: 5. |
| `-r, --results <list>` | Comma-separated result types, or `all`, or `list`. Default: curated set. |
| `-f, --format <list>` | `csv`, `json`, or both. Default: both. |
| `--fd-dir <path>` | FEM-Design install directory. Default: auto-detect. |
| `--gui` | Show the FEM-Design window instead of running headless. |
| `--keep-open` | Leave FEM-Design running after the dump. |
| `-q, --quiet` | Suppress FEM-Design log output. |
| `-h, --help` | Show help. |

### Examples

Dump results already stored in a calculated `.str`:

```bash
FEMDesignDumper -m C:\models\frame.str
```

Calculate a `.struxml` first, then dump:

```bash
FEMDesignDumper -m C:\models\frame.struxml --calc static
```

Only bar forces and reactions, CSV only:

```bash
FEMDesignDumper -m C:\models\frame.str -r BarInternalForce,PointSupportReaction -f csv
```

List every available result type:

```bash
FEMDesignDumper --results list
```

## Output

```
<out>/
  manifest.json         Run metadata + status and row count for every result type.
  model_summary.json    Element counts, load case and load combination names.
  <ResultType>.csv      One flat table per result type (empty tables are skipped).
  <ResultType>.json     Full-fidelity results per result type.
```

`manifest.json` is the entry point for automation: it lists each result type with a
`status` of `ok`, `empty`, or `error`, the row count, and the files written.

Program logs go to **stderr**; the short final summary goes to **stdout**, so you can pipe
or capture them separately.

### Default result set

`NodalDisplacement`, `PointSupportReaction`, `LineSupportReaction`, `BarInternalForce`,
`BarDisplacement`, `ShellInternalForce`, `ShellDisplacement`.

Use `--results all` for the full set (adds bar/shell stresses, end forces, derived forces,
steel/timber utilization, eigenfrequencies), or `--results list` to see them all.

## Notes

- Units default to kN, m, mm (displacement), MPa (stress), degrees.
- `--calc none` dumps whatever results the model already contains; if none are found the
  tool tells you to re-run with `--calc static`.
- Exit codes: `0` success, `1` usage error, `2` runtime error.

## License

MIT
