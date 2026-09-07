# -*- coding: utf-8 -*-
"""Result figures for a single-span U-channel culvert (four shell plates), four-panel layout.
Reads a FEMDesignDumper CSV extract and writes PNGs. Geometry, plate order/names, fixed colour
scales and contour levels below are tuned to this model; treat as a template for others.
Paths via env vars: FDD_EXTRACT (input extract dir) and FDD_FIGDIR (output dir)."""
import csv, os, math, sys
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.tri import Triangulation
from matplotlib.collections import PolyCollection
from matplotlib.patches import Patch, Rectangle
import matplotlib.font_manager as fm

plt.rcParams.update({
    "font.family": "DejaVu Sans", "font.size": 12,
    "axes.titlesize": 13, "figure.dpi": 170, "savefig.dpi": 170,
})

EXTRACT = os.environ.get("FDD_EXTRACT", os.path.join(os.getcwd(), "extract"))
OUT = os.environ.get("FDD_FIGDIR", os.path.join(os.getcwd(), "figures"))
os.makedirs(OUT, exist_ok=True)

def rd(name):
    with open(os.path.join(EXTRACT, name), encoding="utf-8-sig", newline="") as f:
        return list(csv.DictReader(f))
def clean(s): return s.lstrip("\ufeff\u200b").strip()

# ---- geometry ----
NODE = {int(r["NodeId"]): (float(r["X"]), float(r["Y"]), float(r["Z"])) for r in rd("FemNode.csv")}
ELEM = {}                      # (plate, elemId) -> [nodeids]
for r in rd("FemShell.csv"):
    plate = ".".join(r["Id"].split(".")[:2])
    ns = [int(r["Node1"]), int(r["Node2"]), int(r["Node3"]), int(r["Node4"])]
    ELEM[(plate, int(r["ElementId"]))] = [n for n in ns if n != 0 and n in NODE]
SUPPORT = set()
for fn in ("PointSupportReaction.csv", "LineSupportReaction.csv"):
    for r in rd(fn):
        try: SUPPORT.add(int(r["NodeId"]))
        except Exception: pass

PLATES = ["P.4", "P.3", "P.2", "P.1"]                       # top -> bottom
PNAME = {"P.4": "Kjørebanedekke", "P.3": "Innervegg", "P.2": "Bunnplate", "P.1": "Yttervegg"}
# natural (vertical-in-panel) axis per plate: 1=y (slabs), 2=z (walls) - detected by range
def plate_axis(plate):
    ns = set(n for (p, e), nl in ELEM.items() if p == plate for n in nl)
    ys = [NODE[n][1] for n in ns]; zs = [NODE[n][2] for n in ns]
    return 1 if (max(ys) - min(ys)) >= (max(zs) - min(zs)) else 2
AXIS = {p: plate_axis(p) for p in PLATES}
def natural(n, plate): return NODE[n][AXIS[plate]]
def plate_extent(plate):
    ns = set(n for (p, e), nl in ELEM.items() if p == plate for n in nl)
    vs = [natural(n, plate) for n in ns]
    return max(vs) - min(vs)
HRATIO = [plate_extent(p) for p in PLATES]

def uls(c): c = clean(c); return c.startswith("ULSM")
def sls(c): c = clean(c); return c.startswith("SLS")

# ---- envelope builders: per (plate,node) and per (plate,elem) ----
def shell_env():
    """Return node/elem envelopes for Mx,My (signed) and Txz,Tyz (abs), over ULS."""
    nsigned = {}   # (q) -> {(plate,node): [minv,maxv]}
    nabs = {}      # (q) -> {(plate,node): maxabs}
    esigned = {}   # (q) -> {(plate,elem): [minv,maxv]}
    eabs = {}      # (q) -> {(plate,elem): maxabs}
    for q in ("Mx", "My"): nsigned[q] = {}; esigned[q] = {}
    for q in ("Txz", "Tyz"): nabs[q] = {}; eabs[q] = {}
    for r in rd("ShellInternalForce.csv"):
        if not uls(r["CaseIdentifier"]): continue
        plate = ".".join(r["Id"].split(".")[:2]); nd = int(r["NodeId"]); el = int(r["ElementId"])
        for q in ("Mx", "My"):
            v = float(r[q])
            kd = (plate, nd); ke = (plate, el)
            d = nsigned[q].get(kd);  nsigned[q][kd] = [min(d[0], v), max(d[1], v)] if d else [v, v]
            e = esigned[q].get(ke);  esigned[q][ke] = [min(e[0], v), max(e[1], v)] if e else [v, v]
        for q in ("Txz", "Tyz"):
            a = abs(float(r[q])); kd = (plate, nd); ke = (plate, el)
            nabs[q][kd] = max(nabs[q].get(kd, 0.0), a)
            eabs[q][ke] = max(eabs[q].get(ke, 0.0), a)
    return nsigned, nabs, esigned, eabs

def reinf_env():
    nmax = {q: {} for q in ("XBottom", "YBottom", "XTop", "YTop")}
    emax = {q: {} for q in ("XBottom", "YBottom", "XTop", "YTop")}
    for r in rd("RCShellReinforcementRequired.csv"):
        if not uls(r["CaseIdentifier"]): continue
        plate = ".".join(r["Id"].split(".")[:2]); nd = int(r["NodeId"]); el = int(r["ElementId"])
        for q in nmax:
            v = float(r[q]); kd = (plate, nd); ke = (plate, el)
            nmax[q][kd] = max(nmax[q].get(kd, 0.0), v)
            emax[q][ke] = max(emax[q].get(ke, 0.0), v)
    return nmax, emax

def defl_env():
    nmin = {}
    for r in rd("NodalDisplacement.csv"):
        if not sls(r["CaseIdentifier"]): continue
        plate = ".".join(r["Id"].split(".")[:2]); nd = int(r["NodeId"]); ez = float(r["Ez"])
        kd = (plate, nd); nmin[kd] = min(nmin.get(kd, 0.0), ez)
    return nmin

def signed_node(nsigned_q, plate, nd):
    d = nsigned_q.get((plate, nd));
    if d is None: return None
    return d[0] if abs(d[0]) > abs(d[1]) else d[1]

def elem_border(plate, el): return any(n in SUPPORT for n in ELEM[(plate, el)])
def elem_area(plate, el):
    pts = [NODE[n] for n in ELEM[(plate, el)]]
    if len(pts) == 4:
        d1 = np.subtract(pts[2], pts[0]); d2 = np.subtract(pts[3], pts[1])
        return 0.5 * np.linalg.norm(np.cross(d1, d2))
    if len(pts) == 3:
        return 0.5 * np.linalg.norm(np.cross(np.subtract(pts[1], pts[0]), np.subtract(pts[2], pts[0])))
    return 0.0

# ---- support markers on panels ----
# S.3 (0,0,-2.7) & S.4 (0,3.5,-2.7) on bottom plate P.2 (natural=y); S.5 (0,6.12,0) on deck P.4
POINT_SUP = {"P.2": [("S.3", 0.0), ("S.4", 3.5)], "P.4": [("S.5", 6.12)]}
LINE_SUP = {"P.2": "S.1", "P.4": "S.2"}
XMAX = 9.75

def panel_figure(fname, title, cbar_label, value_fn, cmap, vmin, vmax, fill_levels,
                 diverging=False, contour_levels=None, contour_fmt=None, contour_per_plate=None,
                 maxmarks=None, extend="max", contour_color="k", contour_lw=1.0):
    fig = plt.figure(figsize=(12.6, 9.2))
    gs = fig.add_gridspec(4, 2, width_ratios=[40, 1], height_ratios=HRATIO,
                          left=0.13, right=0.9, top=0.9, bottom=0.08, hspace=0.28, wspace=0.04)
    cax = fig.add_subplot(gs[:, 1])
    mappable = None
    for i, plate in enumerate(PLATES):
        ax = fig.add_subplot(gs[i, 0])
        nodes = sorted(set(n for (p, e), nl in ELEM.items() if p == plate for n in nl))
        x = np.array([NODE[n][0] for n in nodes])
        yv = np.array([natural(n, plate) for n in nodes])
        val = np.array([value_fn(plate, n) for n in nodes], dtype=float)
        tri = Triangulation(x, yv)
        cf = ax.tricontourf(tri, val, levels=fill_levels, cmap=cmap, vmin=vmin, vmax=vmax, extend=extend)
        mappable = cf
        clev = contour_per_plate(plate) if contour_per_plate else contour_levels
        if clev is not None and len(clev):
            cs = ax.tricontour(tri, val, levels=clev, colors=contour_color, linewidths=contour_lw)
            if contour_fmt: ax.clabel(cs, fmt=contour_fmt, fontsize=8, inline=True)
        # singularity-bordering elements: hatch overlay
        polys = [[(NODE[n][0], natural(n, plate)) for n in ELEM[(plate, e)]]
                 for (p, e) in ELEM if p == plate and elem_border(plate, e)]
        if polys:
            pc = PolyCollection(polys, facecolors="none", edgecolors="0.15", hatch="////",
                                linewidths=0.0, zorder=3)
            ax.add_collection(pc)
        # supports
        for name, ycoord in POINT_SUP.get(plate, []):
            ax.plot(0, ycoord, marker="^", ms=11, mfc="white", mec="black", mew=1.6, zorder=6, clip_on=False)
            ax.annotate(name, (0, ycoord), textcoords="offset points", xytext=(-4, 6),
                        ha="right", fontsize=9, fontweight="bold")
        if plate in LINE_SUP:
            ax.plot([XMAX, XMAX], [yv.min(), yv.max()], color="black", lw=4, solid_capstyle="butt", zorder=6)
            ax.annotate(LINE_SUP[plate], (XMAX, (yv.min() + yv.max()) / 2), textcoords="offset points",
                        xytext=(8, 0), va="center", fontsize=9, fontweight="bold")
        ax.set_xlim(-0.2, XMAX + 0.2); ax.set_ylim(yv.min() - 0.1, yv.max() + 0.1)
        ax.set_aspect("auto"); ax.set_yticks([round(yv.min(), 1), round(yv.max(), 1)])
        ax.tick_params(labelsize=9)
        ax.set_ylabel(f"{plate}\n{PNAME[plate]}", rotation=0, ha="right", va="center", fontsize=10, labelpad=28)
        vlabel = "y [m]" if AXIS[plate] == 1 else "z [m]"
        ax.text(0.005, 0.5, vlabel, transform=ax.transAxes, rotation=90, va="center", ha="right", fontsize=8, color="0.4")
        if i < 3: ax.set_xticklabels([])
    ax.set_xlabel("Brulengde  x [m]   (x = 0: lagerende / fri ende,   x = 9.75: fast ende)", fontsize=11)
    # max marks
    if maxmarks:
        for (plate, xc, yc, txt) in maxmarks:
            axp = fig.axes[[p for p in PLATES].index(plate) + 1]  # +1: cax is axes[0]
            axp.plot(xc, yc, marker="x", ms=10, mec="black", mew=2.2, zorder=8)
            axp.annotate(txt, (xc, yc), textcoords="offset points", xytext=(6, 6), fontsize=9,
                         fontweight="bold", zorder=8,
                         bbox=dict(boxstyle="round,pad=0.15", fc="white", ec="0.3", alpha=0.85))
    cb = fig.colorbar(mappable, cax=cax, extend=extend)
    cb.set_label(cbar_label, fontsize=11)
    fig.suptitle(title, fontsize=15, fontweight="bold", x=0.13, ha="left")
    path = os.path.join(OUT, fname)
    fig.savefig(path, facecolor="white", bbox_inches="tight", pad_inches=0.15)
    plt.close(fig)
    print("wrote", fname)
    return path


def zone_figure(fname, title, classifier, classes):
    """classes: list of (label, facecolor, hatch|None). classifier(plate, elem) -> class index."""
    fig = plt.figure(figsize=(12.6, 9.6))
    gs = fig.add_gridspec(4, 1, height_ratios=HRATIO, left=0.13, right=0.97, top=0.9, bottom=0.15, hspace=0.28)
    area_cls = [0.0] * len(classes); area_tot = 0.0
    perplate = {p: [0.0] * len(classes) for p in PLATES}
    for i, plate in enumerate(PLATES):
        ax = fig.add_subplot(gs[i, 0])
        buckets = [[] for _ in classes]
        yv_all = []
        for (p, e) in ELEM:
            if p != plate: continue
            ci = classifier(plate, e)
            poly = [(NODE[n][0], natural(n, plate)) for n in ELEM[(plate, e)]]
            buckets[ci].append(poly)
            a = elem_area(plate, e); area_cls[ci] += a; area_tot += a; perplate[plate][ci] += a
            yv_all += [q[1] for q in poly]
        for ci, (label, color, hatch) in enumerate(classes):
            if not buckets[ci]: continue
            pc = PolyCollection(buckets[ci], facecolors=color, edgecolors="white", linewidths=0.2)
            if hatch: pc.set_hatch(hatch); pc.set_edgecolor("0.2")
            ax.add_collection(pc)
        for name, ycoord in POINT_SUP.get(plate, []):
            ax.plot(0, ycoord, marker="^", ms=11, mfc="white", mec="black", mew=1.6, zorder=6, clip_on=False)
            ax.annotate(name, (0, ycoord), textcoords="offset points", xytext=(-4, 6), ha="right", fontsize=9, fontweight="bold")
        if plate in LINE_SUP:
            ax.plot([XMAX, XMAX], [min(yv_all), max(yv_all)], color="black", lw=4, solid_capstyle="butt", zorder=6)
            ax.annotate(LINE_SUP[plate], (XMAX, (min(yv_all) + max(yv_all)) / 2), textcoords="offset points",
                        xytext=(8, 0), va="center", fontsize=9, fontweight="bold")
        ax.set_xlim(-0.2, XMAX + 0.2); ax.set_ylim(min(yv_all) - 0.1, max(yv_all) + 0.1)
        ax.set_yticks([round(min(yv_all), 1), round(max(yv_all), 1)]); ax.tick_params(labelsize=9)
        ax.set_ylabel(f"{plate}\n{PNAME[plate]}", rotation=0, ha="right", va="center", fontsize=10, labelpad=28)
        vlabel = "y [m]" if AXIS[plate] == 1 else "z [m]"
        ax.text(0.005, 0.5, vlabel, transform=ax.transAxes, rotation=90, va="center", ha="right", fontsize=8, color="0.4")
        if i < 3: ax.set_xticklabels([])
    ax.set_xlabel("Brulengde  x [m]   (x = 0: lagerende / fri ende,   x = 9.75: fast ende)", fontsize=11)
    handles = [Patch(facecolor=c, edgecolor="0.3", hatch=h, label=f"{lab}   {100*area_cls[ci]/area_tot:.1f} %")
               for ci, (lab, c, h) in enumerate(classes)]
    fig.legend(handles=handles, loc="lower center", ncol=len(classes), fontsize=10, frameon=False, bbox_to_anchor=(0.5, 0.015))
    fig.suptitle(title, fontsize=15, fontweight="bold", x=0.13, ha="left")
    path = os.path.join(OUT, fname)
    fig.savefig(path, facecolor="white", bbox_inches="tight", pad_inches=0.15)
    plt.close(fig)
    print("wrote", fname, "(arealandeler)")
    for p in PLATES:
        tot = sum(perplate[p])
        if tot > 0:
            print("   ", p, "  ".join(f"{classes[ci][0][:14]}={100*perplate[p][ci]/tot:.1f}%" for ci in range(len(classes))))
    return path
