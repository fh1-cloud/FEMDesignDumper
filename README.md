# FEMDesignDumper

A small Windows command-line tool that opens a [FEM-Design](https://strusoft.com/) model
in the background and dumps its results to CSV and JSON.

It is built for automation: point it at a model file and it writes a predictable set of
files plus a `manifest.json` describing exactly what was produced — easy for a script, a CI
job, or an AI agent to consume.

It drives FEM-Design through StruSoft's official [`FemDesign.Core`](https://www.nuget.org/packages/FemDesign.Core)
API over the real-time pipe connection, so results are identical to what you would get in
the GUI.

> This README doubles as an **operator's guide** so the full workflow (extraction +
> post-processing into engineering deliverables) can be repeated later without rediscovering
> the details. See [Operating recipe](#operating-recipe-headless-results-extraction) and
> [Data-model reference](#data-model-reference).

## What it does

1. Launches FEM-Design headlessly (no window; via `FD_NOGUI`).
2. Opens a `.str` or `.struxml` model.
3. Optionally runs static or eigenfrequency analysis.
4. Extracts a model summary and a selectable set of result tables.
5. Optionally renders SVG result plots (reinforcement, moments, shear, deflection) with the peak annotated — a whole-structure isometric view by default, or flat per-plate maps.
6. Writes everything to an output folder and exits.

It **reads** results (including saved design results); it does **not** run RC/steel/timber
design itself.

## Requirements

- **FEM-Design 25** installed (default: `C:\Program Files\StruSoft\FEM-Design 25\`), with a
  valid licence. The tool automates the installed application.
- **.NET SDK** to build. The project targets **.NET Framework 4.8** (required by
  `FemDesign.Core`), buildable with the modern `dotnet` CLI when the .NET 4.8 targeting pack
  is present.

The FEM-Design major version must match the `FemDesign.Core` version (both 25.x here). If you
upgrade FEM-Design, bump the package version in
[`src/FEMDesignDumper/FEMDesignDumper.csproj`](src/FEMDesignDumper/FEMDesignDumper.csproj).

## Build

```bash
dotnet build -c Release
```

Executable:

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
| `--plots <list>` | Per-plate SVG colour maps with annotated peak: `reinf`, `moment`, `shear`, `deflection`, or `all`. Default: none. |
| `--plot-cap <mode>` | Colour-scale cap: `max` (default), `p95`, `p99`. Tames support singularities; the peak label always shows the true value. |
| `--plot-view <mode>` | `iso` (default — whole structure in one 3D view), `plate` (one flat map per plate), or `both`. |
| `--iso-view <x,y,z>` | Isometric viewpoint / eye direction (world up = +Z). Default: `-1,-1,1`. |
| `--fd-dir <path>` | FEM-Design install directory. Default: auto-detect. |
| `--gui` | Show the FEM-Design window instead of running headless. |
| `--keep-open` | Leave FEM-Design running after the dump. |
| `-q, --quiet` | Suppress FEM-Design log output. |
| `-h, --help` | Show help. |

### Examples

```bash
# Dump results already stored in a calculated .str (curated set, CSV + JSON)
FEMDesignDumper -m C:\path\to\model.str

# Calculate a .struxml first, then dump
FEMDesignDumper -m C:\path\to\model.struxml --calc static

# Only the types you need, CSV only, quiet, to a chosen folder
FEMDesignDumper -m C:\path\to\model.str --calc none -f csv --quiet ^
  -r ShellInternalForce,PointSupportReaction,LineSupportResultant -o C:\out\extract

# List every available result type
FEMDesignDumper --results list
```

## Result types

`*` = in the default set (used when `--results` is omitted). Use `--results all` for every
type, or name them explicitly.

| Type | Group | Availability |
| --- | --- | --- |
| `NodalDisplacement` * | nodes | static results |
| `PointSupportReaction` * | supports | static results |
| `LineSupportReaction` * | supports (per metre, local axes) | static results |
| `LineSupportResultant` | supports (integrated resultant) | static results |
| `SurfaceSupportReaction` | supports | static results |
| `BarInternalForce` * | bars | static results |
| `BarEndForce` | bars | static results |
| `BarDisplacement` * | bars | static results |
| `BarStress` | bars | static results |
| `ShellInternalForce` * | shells (m′, n, v′) | static results |
| `ShellDisplacement` * | shells | static results |
| `ShellStress` | shells | static results |
| `ShellDerivedForce` | shells | static results |
| `RCShellReinforcementRequired` | RC design | **RC design must have been run** |
| `BarSteelUtilization` | steel design | steel design must have been run |
| `BarTimberUtilization` | timber design | timber design must have been run |
| `EigenFrequencies` | dynamics | eigenfrequency results (`--calc freq`) |
| `FemNode` | mesh | whenever a mesh exists |
| `FemShell` | mesh (element connectivity) | whenever a mesh exists |

Empty types are reported as `empty` in the manifest and produce no file. A type that errors
(e.g. results not present) is caught and reported as `error` — the run continues.

### Result CSV schemas (columns)

One row per node/element/support per load case **and** per load combination (the
`CaseIdentifier` column names it). Key types:

| Type | Columns |
| --- | --- |
| `PointSupportReaction` | `Id,X,Y,Z,NodeId,Fx,Fy,Fz,Mx,My,Mz,Fr,Mr,CaseIdentifier` |
| `LineSupportReaction` | `Id,ElementId,NodeId,Fx,Fy,Fz,Mx,My,Mz,Fr,Mr,CaseIdentifier` (local, per-metre) |
| `LineSupportResultant` | `Id,HalfLength,Fx,Fy,Fz,Mx,My,Mz,CaseIdentifier` (integrated over the support) |
| `ShellInternalForce` | `Id,ElementId,NodeId,Mx,My,Mxy,Nx,Ny,Nxy,Txz,Tyz,CaseIdentifier` |
| `RCShellReinforcementRequired` | `Id,ElementId,NodeId,XBottom,YBottom,XTop,YTop,XMid,YMid,CaseIdentifier` |
| `NodalDisplacement` | `Id,NodeId,Ex,Ey,Ez,Fix,Fiy,Fiz,CaseIdentifier` |
| `BarInternalForce` | `Id,Pos,Fx,Fy,Fz,Mx,My,Mz,CaseIdentifier` |
| `FemNode` | `NodeId,X,Y,Z` |
| `FemShell` | `Id,ElementId,Node1,Node2,Node3,Node4` (Node4 = 0 for triangles) |

For shells, `Mx,My,Mxy` are the plate moments *m′x, m′y, m′xy*; `Nx,Ny,Nxy` the membrane
forces; `Txz,Tyz` the transverse shear *v′xz, v′yz*.

## Output

```
<out>/
  manifest.json        Run metadata + status and row count for every result type.
  model_summary.json   Element counts, load case and load combination names, support counts.
  <ResultType>.csv     One flat table per result type (empty tables skipped).
  <ResultType>.json    Full-fidelity results per result type.
```

`manifest.json` is the entry point for automation: each result type carries a `status`
(`ok`/`empty`/`error`), a row count, and the files written (plus a `plots` list). Program logs
go to **stderr**; the short final summary goes to **stdout**.

## Result plots

`--plots` renders self-contained SVG colour maps — no FEM-Design figure/documentation needed.
Values are enveloped over the model's real ULS (`Ultimate*`) or SLS (`Serviceability*`)
combination **types** and reduced to one value per element (the extreme over the element's
nodes and the combinations), and the **true peak is annotated** (value, location, governing
combination, and whether the peak element borders a support).

`--plot-view` selects the projection:

- **`iso`** (default) — the **whole structure in one axonometric 3D view**
  (`<out>/plots/iso_<field>.svg`), with each plate labelled (`P.1`…), an x/y/z orientation
  triad, and painter-sorted, depth-correct shading. This is the easiest to read; you don't have
  to relate to abstract plate names. The viewpoint is a look-at camera set by `--iso-view x,y,z`
  (eye direction, world up = +Z); default `-1,-1,1`. e.g. `--iso-view 1,-1,1` views from the
  other side.
- **`plate`** — one flat map per plate (`<out>/plots/<surface>_<field>.svg`); each plate is
  projected to its own 2D plane (the near-constant global axis is dropped, the two remaining
  global axes label the plot, so it assumes plates roughly parallel to a global coordinate plane).
- **`both`** — writes both.

| Group | Quantity (field) | Envelope | Unit |
| --- | --- | --- | --- |
| `reinf` | required reinforcement `XBottom, YBottom, XTop, YTop` | max over ULS | mm²/m |
| `moment` | shell moments `Mx, My, Mxy` (\|value\|) | max over ULS | kNm/m |
| `shear` | shell shear `Txz, Tyz` (\|value\|) | max over ULS | kN/m |
| `deflection` | vertical deflection `Ez` (downward) | max over SLS | mm |

**Support singularities**: peak shear/reinforcement often sits on a support-bordering element.
The annotation flags this (`ved opplegg`), and `--plot-cap p95`/`p99` caps the colour scale at
that percentile so one spike doesn't wash out the map — the label still reports the true peak.

Needs `FemNode`, `FemShell` and the quantity's result type present in the model (a calculated
model; reinforcement needs RC design to have been run). The plots fetch what they need
themselves, so a minimal dump is fine:

```bash
# isometric (default): one whole-structure view per field
FEMDesignDumper -m model.str --calc none -r FemNode --plots reinf,shear --plot-cap p95 -o C:\out
# -> C:\out\plots\iso_XBottom.svg ... iso_Tyz.svg

# per-plate flat maps instead (or --plot-view both)
FEMDesignDumper -m model.str --calc none -r FemNode --plots reinf --plot-view plate -o C:\out
# -> C:\out\plots\P.1_XBottom.svg ... P.4_YTop.svg
```

## Data-model reference

- **Units** (fixed by the tool, `new UnitResults()`): length **m**, force **kN**, moment
  **kNm** (per width **kNm/m** for shells), shell shear **kN/m**, displacement **mm**, stress
  **MPa**, angle **deg**, section data **mm**, required reinforcement **mm²/m**. Values are raw
  API values — nothing is converted.
- **Identifiers**: shell `Id` is `P.<plate>.<part>` (e.g. `P.2.1`); the **surface** is the
  first two components (`P.2`). Supports are `S.<n>`. Element keys are `(Id, ElementId)`;
  `ElementId` is global across the mesh.
- **`CaseIdentifier`**: the load case name (e.g. `Egenvekt`) or the load combination name.
  By convention here ULS combos are `ULSM*` and SLS characteristic are `SLS_KAR*` — classify by
  prefix, not by hard-coded lists. The tool strips a leading UTF-8 BOM that FEM-Design prepends
  to the first exported name.
- **Sign convention (reactions)**: global **+Z is up**. Under gravity, vertical reactions come
  out **negative** (`Fz < 0`) — FEM-Design reports the reaction with the same sign as the
  downward load, so `|Fz|` is the force into the support and a *positive* `Fz` means uplift. The
  governing (largest) vertical reaction is the **most negative** `Fz`. For load-balance use
  `|ΣFz|`.
- **Per-element values**: shell forces / reinforcement are reported at the element's nodes
  (several rows per element per case). To get one value per element, take the extreme over the
  element's node rows (and over the combinations for an envelope).
- **Combinations are present without recalculation** when the model was saved from FEM-Design
  with results: the extract's `CaseIdentifier` set then includes every `ULSM*`/`SLS_KAR*`. Verify
  before enveloping (see recipe). Reinforcement (`RCShellReinforcementRequired`) is per ULS
  combination and only present if RC design was run.

## Operating recipe (headless results extraction)

Repeatable workflow for turning a saved model into engineering tables:

1. **Locate the model** and inspect the folder. A large `*.strFEM` next to `*.str` means the
   model is already calculated (results saved) — `--calc none` will work.
2. **Read the model definition from the `.struxml`** (same folder) for context that is not in
   the results — geometry, thickness, material, supports, load-case and load-combination
   definitions. See [Reading the .struxml](#reading-the-struxml).
3. **Extract** the types you need, CSV only, quiet, to a scratch folder:
   ```bash
   FEMDesignDumper -m model.str --calc none -f csv --quiet ^
     -r NodalDisplacement,PointSupportReaction,LineSupportReaction,LineSupportResultant,^
        ShellInternalForce,RCShellReinforcementRequired,FemNode,FemShell
   ```
   If the first launch fails on the licence server, just run it again (see
   [Gotchas](#gotchas)).
4. **Confirm combinations are present**: check that the `CaseIdentifier` column of a result CSV
   contains the `ULSM*`/`SLS_KAR*` names. If only load cases are present, re-run with
   `--calc static` to compute combinations.
5. **Post-process** the long-format CSVs (each row tagged with `CaseIdentifier`) into whatever
   is needed — envelopes over ULS/SLS, per-surface statistics, cross-checks. Pure Python
   (`csv` module) is enough; **pandas is not required**.

### Post-processing building blocks

- **Envelope over combinations**: filter rows to the ULS (or SLS) `CaseIdentifier` set, group
  by support / element / node, take max & min and remember the governing combination name.
- **Elements bordering a support** (singularity screening): support node ids =
  `PointSupportReaction.NodeId` ∪ `LineSupportReaction.NodeId`; an element borders a support if
  any of its `FemShell` connectivity nodes is a support node. Peak shear/reinforcement in such
  elements is usually a mesh singularity — report percentiles (e.g. p95/p99), not just the max.
- **Concrete volume** (cross-check): sum `element_area × plate_thickness` over `FemShell`
  (element area from `FemNode` coordinates: for a quad, `0.5 · |d13 × d24|`).
- **Vertical reaction balance** (cross-check): per case, `ΣFz` = `PointSupportReaction.Fz` +
  `LineSupportResultant.Fz`. For a combination it must equal `Σ γ · (case ΣFz)` using the
  combination factors from the `.struxml` — this confirms nothing is lost and combos are linear.

### Reading the `.struxml`

The `.struxml` is XML in the default namespace `urn:strusoft`. Useful nodes:

- `//material` — concrete grade (e.g. `B45`).
- `//slab` with `slab_part/thickness/@val` — plate names and thicknesses (m).
- `//point_support` / `//line_support` — support names, positions, and restrained motions.
- `//loads/load_case` — the load-case **definitions** (`@name`, `@guid`). Build the
  `guid → name` map **only** from these.
- `//load_combination` — `@name`, `@type` (`ultimate_ordinary`, `serviceability_characteristic`,
  …), with child `load_case` elements carrying `@guid` (reference) and `@gamma` (factor).
  **Do not** build the guid→name map from these children — they have a guid but no name and
  will clobber the map.

## Gotchas

- **Licence server, first launch**: the very first headless launch after a while can fail on
  the licence server. Re-running the exact same command succeeds.
- **stdout vs stderr**: parse the machine summary/manifest from files or stdout; FEM-Design's
  chatter is on stderr (silence it with `--quiet`).
- **Network paths / non-ASCII**: model and output paths on mapped drives and with characters
  like `ø` work; quote them.
- **.NET Framework 4.8 only**: `FemDesign.Core` ships `net48` only; the project targets `net48`
  by necessity.
- **BOM in the first combination name**: FEM-Design prepends a UTF-8 BOM to the first exported
  name; the tool strips a leading BOM from string cells so `ULSM1` matches cleanly.
- **Scratch files**: `FemDesign.Core`'s generated scripts, logs, intermediate result lists and a
  re-serialized copy of the model are written to a temporary directory under
  `%TEMP%\FEMDesignDumper\` and removed on exit — they do **not** land next to the model or in the
  working directory. Only the files under `--out` are the deliverables.

## Extending with new result types

Every result type is one line in the `Registry` in
[`src/FEMDesignDumper/Program.cs`](src/FEMDesignDumper/Program.cs):

```csharp
ResultKind.Of<SomeResultType>("SomeResultType", inDefault: false),
```

`SomeResultType` must be a public `FemDesign.Results.*` type implementing `IResult`. It is then
selectable via `--results SomeResultType`. The generic CSV/JSON writer serializes any such type
by reflection, so no per-type code is needed. To discover available types, browse
`FemDesign.Results` in the `FemDesign.Core` package/source.

## License

MIT
