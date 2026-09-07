# Post-processing figure scripts

Python scripts that turn a **FEMDesignDumper CSV extract** into report figures and derived
data tables for a single-span U-channel culvert modelled as four shell plates (`P.1`–`P.4`).
They are a worked template — geometry, plate order/names, fixed colour scales, capacity/rebar
contour levels and the reaction values in the overview figure are tuned to one model; adapt
them for others.

These complement the C# tool (whose own `--plots` produces isometric/per-plate SVGs); this
pipeline produces the "four-panel" PNG report figures, zone plots, and a reaction schematic.

## Requirements

Python 3 with `numpy` and `matplotlib` (`pip install numpy matplotlib`).

## Input

A directory of CSVs produced by FEMDesignDumper, e.g.:

```bash
FEMDesignDumper -m model.str --calc none -f csv -o extract ^
  -r NodalDisplacement,PointSupportReaction,LineSupportReaction,LineSupportResultant,^
     ShellInternalForce,RCShellReinforcementRequired,FemNode,FemShell
# results land in extract\<Type>.csv
```

Paths are taken from environment variables:

- `FDD_EXTRACT` — the extract directory (default `./extract`)
- `FDD_FIGDIR` — the output directory for figures/CSVs (default `./figures`)

```bash
# PowerShell
$env:FDD_EXTRACT="C:\path\to\extract"; $env:FDD_FIGDIR="C:\path\to\out"
```

## Scripts

| Script | What it does |
| --- | --- |
| `figspec.py` | Library: loads the extract, builds per-plate envelopes, and renders the four-panel `panel_figure(...)` and `zone_figure(...)`. |
| `run_figs.py` | Produces the moment / shear / reinforcement / deflection panel figures and the two zone plots. `python run_figs.py` (all) or `python run_figs.py 5-1 6-1 …` (selected). |
| `extra.py` | Writes mesh (`mesh_noder.csv`, `mesh_elementer.csv`), SLS section-force (`snittkrefter_sls.csv`) and per-load-case reaction (`punktlager_lasttilfeller.csv`) CSVs, plus a support-reaction overview figure. |
| `svg_raster.py` | Minimal, cairo-free SVG→PNG rasterizer for previewing the C# tool's SVGs: `python svg_raster.py in.svg out.png`. |

Figures are PNG, white background, ≥1800 px wide, cropped, with fixed rounded colour scales so
they compare across the set; peaks are annotated and support-bordering (singularity) elements
are hatched.
