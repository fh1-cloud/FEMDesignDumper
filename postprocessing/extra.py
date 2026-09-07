# -*- coding: utf-8 -*-
"""Mesh/SLS/per-case data CSVs + a support-reaction overview figure for the culvert model.
Reaction values in the figure are tuned to this model. Paths via env vars FDD_EXTRACT / FDD_FIGDIR."""
import csv, os, math
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import Rectangle, Polygon, FancyArrow

EXTRACT = os.environ.get("FDD_EXTRACT", os.path.join(os.getcwd(), "extract"))
OUT = os.environ.get("FDD_FIGDIR", os.path.join(os.getcwd(), "figures"))
os.makedirs(OUT, exist_ok=True)
plt.rcParams.update({"font.family": "DejaVu Sans", "font.size": 12})

def rd(name):
    with open(os.path.join(EXTRACT, name), encoding="utf-8-sig", newline="") as f:
        return list(csv.DictReader(f))
def clean(s): return s.lstrip("\ufeff\u200b").strip()
def flate(idp): return ".".join(idp.split(".")[:2])
def num(x): return format(x, ".3f")

NODE = {int(r["NodeId"]): (float(r["X"]), float(r["Y"]), float(r["Z"])) for r in rd("FemNode.csv")}
SHELL = rd("FemShell.csv")
SUPPORT = set()
for fn in ("PointSupportReaction.csv", "LineSupportReaction.csv"):
    for r in rd(fn):
        try: SUPPORT.add(int(r["NodeId"]))
        except Exception: pass

def elem_nodes(r):
    ns = [int(r["Node1"]), int(r["Node2"]), int(r["Node3"]), int(r["Node4"])]
    return [n for n in ns if n != 0 and n in NODE]
def poly_area(ns):
    pts = [np.array(NODE[n]) for n in ns]
    if len(pts) == 4:
        return 0.5 * np.linalg.norm(np.cross(pts[2] - pts[0], pts[3] - pts[1]))
    if len(pts) == 3:
        return 0.5 * np.linalg.norm(np.cross(pts[1] - pts[0], pts[2] - pts[0]))
    return 0.0

def wcsv(name, header, rows):
    p = os.path.join(OUT, name)
    with open(p, "w", encoding="utf-8", newline="") as f:
        w = csv.writer(f); w.writerow(header)
        for r in rows: w.writerow(r)
    print("wrote", name, len(rows), "rows")

# ---- 2.1 mesh_noder.csv ----
wcsv("mesh_noder.csv", ["NodeId", "X[m]", "Y[m]", "Z[m]"],
     [[nid, num(x), num(y), num(z)] for nid, (x, y, z) in sorted(NODE.items())])

# ---- 2.1 mesh_elementer.csv ----
erows = []
for r in SHELL:
    ns = elem_nodes(r)
    if len(ns) < 3: continue
    c = np.mean([NODE[n] for n in ns], axis=0)
    erows.append([int(r["ElementId"]), flate(r["Id"]), ";".join(str(n) for n in ns),
                  num(c[0]), num(c[1]), num(c[2]), num(poly_area(ns))])
erows.sort(key=lambda x: (x[1], x[0]))
wcsv("mesh_elementer.csv", ["ElementId", "Flate", "NodeIds", "Xc[m]", "Yc[m]", "Zc[m]", "Areal[m2]"], erows)

# ---- 2.2 snittkrefter_sls.csv (SLS envelope, same columns as snittkrefter.csv) ----
acc = {}   # (flate,id,elem) -> dict of q -> [maxv,maxc,minv,minc]
for r in rd("ShellInternalForce.csv"):
    ci = clean(r["CaseIdentifier"])
    if not ci.startswith("SLS"): continue
    key = (flate(r["Id"]), r["Id"], int(r["ElementId"]))
    d = acc.setdefault(key, {q: [-1e30, "", 1e30, ""] for q in ("Mx", "My", "Mxy")})
    for q in ("Mx", "My", "Mxy"):
        v = float(r[q]); e = d[q]
        if v > e[0]: e[0], e[1] = v, ci
        if v < e[2]: e[2], e[3] = v, ci
def border(fl, elem): return any(n in SUPPORT for n in next(elem_nodes(r) for r in SHELL if flate(r["Id"]) == fl and int(r["ElementId"]) == elem))
srows = []
# precompute element->nodes for border test
EN = {(flate(r["Id"]), int(r["ElementId"])): elem_nodes(r) for r in SHELL}
for (fl, idp, elem) in sorted(acc, key=lambda k: (k[0], k[2])):
    d = acc[(fl, idp, elem)]
    b = "ja" if any(n in SUPPORT for n in EN.get((fl, elem), [])) else "nei"
    row = [fl, idp, elem]
    for q in ("Mx", "My", "Mxy"):
        e = d[q]; row += [num(e[0]), e[1], num(e[2]), e[3]]
    row.append(b)
    srows.append(row)
wcsv("snittkrefter_sls.csv",
     ["Flate", "Id", "ElementId", "Mx_max[kNm/m]", "Mx_max_komb", "Mx_min[kNm/m]", "Mx_min_komb",
      "My_max[kNm/m]", "My_max_komb", "My_min[kNm/m]", "My_min_komb",
      "Mxy_max[kNm/m]", "Mxy_max_komb", "Mxy_min[kNm/m]", "Mxy_min_komb", "grenser_opplegg"], srows)

# ---- 2.3 punktlager_lasttilfeller.csv ----
CASE_ORDER = ["Egenvekt", "R\u00f8r+Fylling", "Overbygning", "Rekkverk+Kantbjelke",
    "LMX_F1_1", "LMX_F1_2", "LMX_F2_1", "LMX_F2_2", "LMX_F3_1", "LMX_F3_2",
    "UDL_F1_54", "UDL_F1_25", "UDL_F2_54", "UDL_F2_25",
    "LM1_F1_1_300", "LM1_F1_2_300", "LM1_F1_1_200", "LM1_F1_2_200",
    "LM1_F2_1_300", "LM1_F2_2_300", "LM1_F2_1_200", "LM1_F2_2_200"]
corder = {c: i for i, c in enumerate(CASE_ORDER)}
prows = []
for r in rd("PointSupportReaction.csv"):
    ci = clean(r["CaseIdentifier"])
    if ci in corder:
        prows.append([r["Id"], ci, num(float(r["Fz"]))])
prows.sort(key=lambda x: (corder[x[1]], x[0]))
wcsv("punktlager_lasttilfeller.csv", ["Id", "CaseIdentifier", "Fz[kN]"], prows)

# ============================================================
# Figure 5-6 · oppleggskrefter_oversikt.png
# ============================================================
def draw_section(ax):
    grey = "#c9c9c9"; ec = "#555"
    rects = [
        (-0.25, -3.05, 0.5, 0.7),    # bottom slab left part under outer wall
        (-0.25, -3.05, 4.0, 0.7),    # bottom slab full
        (-0.25, -2.35, 0.5, 2.35),   # outer wall P.1
        (3.25, -2.35, 0.5, 2.35),    # inner wall P.3
        (3.25, -0.7, 2.87, 0.7),     # deck P.4
    ]
    for (x, y, w, h) in rects:
        ax.add_patch(Rectangle((x, y), w, h, facecolor=grey, edgecolor=ec, lw=1.2, zorder=2))
    ax.set_xlim(-1.1, 7.2); ax.set_ylim(-4.3, 1.0); ax.set_aspect("equal")
    ax.axis("off")

def ground(ax, x, z, w=0.55):
    ax.plot([x - w/2, x + w/2], [z, z], color="black", lw=2, zorder=5)
    for xx in np.linspace(x - w/2, x + w/2, 7):
        ax.plot([xx, xx - 0.12], [z, z - 0.18], color="black", lw=1, zorder=5)

def point_support(ax, y, ztop, name, sls, uls, gov=False):
    col = "#c0392b" if gov else "black"
    tri = Polygon([(y, ztop), (y - 0.28, ztop - 0.42), (y + 0.28, ztop - 0.42)],
                  closed=True, facecolor="white", edgecolor=col, lw=2, zorder=6)
    ax.add_patch(tri)
    ground(ax, y, ztop - 0.42)
    ax.annotate(f"{name}\n{sls:.0f} / {uls:.0f} kN" + ("\n(dimensjonerende)" if gov else ""),
                (y, ztop - 0.62), ha="center", va="top", fontsize=10.5,
                color=col, fontweight="bold" if gov else "normal", zorder=7)

fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(11, 9.4))

# --- part 1: fri ende ---
draw_section(ax1)
ax1.set_title("Fri ende  (x = 0):  tre punktlagre, kun vertikalt fastholdt.   Verdier: SLS / ULS",
              fontsize=12.5, fontweight="bold", loc="left")
point_support(ax1, 0.0, -3.05, "S.3", 989, 1444)
point_support(ax1, 3.5, -3.05, "S.4", 1328, 1669, gov=True)
point_support(ax1, 6.12, -0.7, "S.5", 478, 629)
ax1.annotate("To oppleggsnivåer: S.3/S.4 ved rennas underkant, S.5 ved dekkets overkant.",
             (-1.05, 0.8), fontsize=10, style="italic", color="0.3")

# --- part 2: fast ende ---
draw_section(ax2)
ax2.set_title("Fast ende  (x = 9.75):  to leddede linjeopplegg, integrerte resultanter (ULS)",
              fontsize=12.5, fontweight="bold", loc="left")
def line_support(ax, y0, y1, z, name, fz, fy):
    ax.add_patch(Rectangle((y0, z - 0.28), y1 - y0, 0.28, facecolor="none",
                           edgecolor="black", hatch="////", lw=1.2, zorder=5))
    ax.plot([y0, y1], [z - 0.28, z - 0.28], color="black", lw=2, zorder=5)
    yc = 0.5 * (y0 + y1)
    ax.annotate("", xy=(yc, z - 0.95), xytext=(yc, z - 0.32),
                arrowprops=dict(arrowstyle="-|>", color="#1f4e99", lw=2.2), zorder=6)
    dirx = -0.75 if fy < 0 else 0.75          # opposing horizontal couple
    ax.annotate("", xy=(yc + dirx, z - 0.14), xytext=(yc, z - 0.14),
                arrowprops=dict(arrowstyle="-|>", color="#2e7d32", lw=2.2), zorder=6)
    ax.annotate(f"{name}\nF_z = {fz:.0f} kN\nF_y = {fy:+.0f} kN", (yc, z - 1.05),
                ha="center", va="top", fontsize=10.5, fontweight="bold", color="#1f2d5a", zorder=7)
line_support(ax2, 0.0, 3.5, -3.05, "S.1", -2033, -553)
line_support(ax2, 3.5, 6.12, -0.7, "S.2", -1039, +553)
ax2.annotate("F_y = ∓553 kN (grønt) er et internt,\nselvbalanserende kreftepar –\ningen ytre horisontallast i modellen.",
             (1.5, -1.15), ha="center", va="center", fontsize=10, style="italic", color="0.3",
             bbox=dict(boxstyle="round,pad=0.35", fc="white", ec="0.7"), zorder=8)

fig.suptitle("Oppleggskrefter i bruddgrense", fontsize=16, fontweight="bold", x=0.09, ha="left")
fig.subplots_adjust(left=0.02, right=0.98, top=0.93, bottom=0.02, hspace=0.42)
p = os.path.join(OUT, "oppleggskrefter_oversikt.png")
fig.savefig(p, facecolor="white", dpi=180, bbox_inches="tight", pad_inches=0.15)
plt.close(fig)
print("wrote oppleggskrefter_oversikt.png")
