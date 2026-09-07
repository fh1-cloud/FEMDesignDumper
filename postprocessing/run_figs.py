# -*- coding: utf-8 -*-
"""Produce the Chapter 5-6 data figures per FIGURSPEC.md."""
import sys
import figspec as F

nsigned, nabs, esigned, eabs = F.shell_env()
nmaxR, emaxR = F.reinf_env()
nminD = F.defl_env()

def fmt_val(v, unit):
    return (f"{v:,.0f}".replace(",", " ")) + " " + unit

def mark_reinf(q, unit="mm²/m", plates=None):
    marks = []
    scope = plates or F.PLATES
    for plate in scope:
        best = None
        for (p, n), v in nmaxR[q].items():
            if p == plate and (best is None or v > best[1]): best = ((p, n), v)
        if best:
            (p, n), v = best
            marks.append((p, F.NODE[n][0], F.natural(n, p), fmt_val(v, unit)))
    return marks

def mark_signed(q, plates, unit="kNm/m"):
    marks = []
    for plate in plates:
        best = None
        for (p, n), d in nsigned[q].items():
            if p != plate: continue
            sv = d[0] if abs(d[0]) > abs(d[1]) else d[1]
            if best is None or abs(sv) > abs(best[1]): best = ((p, n), sv)
        if best:
            (p, n), v = best
            marks.append((p, F.NODE[n][0], F.natural(n, p), fmt_val(v, unit)))
    return marks

def mark_abs(dct, plates, unit="kN/m"):
    marks = []
    for plate in plates:
        best = None
        for (p, n), v in dct.items():
            if p == plate and (best is None or v > best[1]): best = ((p, n), v)
        if best:
            (p, n), v = best
            marks.append((p, F.NODE[n][0], F.natural(n, p), fmt_val(v, unit)))
    return marks

def mark_defl():
    best = None
    for (p, n), v in nminD.items():
        d = -v
        if best is None or d > best[1]: best = ((p, n), d)
    (p, n), v = best
    return [(p, F.NODE[n][0], F.natural(n, p), f"{v:.2f} mm")]

REBAR = {1340: "Ø16c150", 2094: "Ø20c150", 3272: "Ø25c150"}
def vrdc(plate): return [333.0] if plate in ("P.2", "P.4") else [216.0]

FIGS = {}
def reg(k):
    def d(fn): FIGS[k] = fn; return fn
    return d

@reg("5-1")
def f51():
    F.panel_figure("mx_enveloppe_uls.png", "Moment m_x om tverraksen, enveloppe bruddgrense",
        "m_x  [kNm/m]", lambda p, n: (lambda d: (d[0] if abs(d[0]) > abs(d[1]) else d[1]) if d else 0.0)(nsigned["Mx"].get((p, n))),
        cmap="RdBu_r", vmin=-250, vmax=250, fill_levels=list(range(-250, 251, 50)),
        diverging=True, extend="both", maxmarks=mark_signed("Mx", ["P.2", "P.4"]))

@reg("5-2")
def f52():
    F.panel_figure("my_enveloppe_uls.png", "Moment m_y om lengdeaksen, enveloppe bruddgrense",
        "m_y  [kNm/m]", lambda p, n: (lambda d: (d[0] if abs(d[0]) > abs(d[1]) else d[1]) if d else 0.0)(nsigned["My"].get((p, n))),
        cmap="RdBu_r", vmin=-250, vmax=250, fill_levels=list(range(-250, 251, 50)),
        diverging=True, extend="both", maxmarks=mark_signed("My", ["P.4", "P.2"]))

@reg("5-3")
def f53():
    F.panel_figure("vxz_enveloppe_uls.png", "Skjærkraft v_xz, absoluttverdi av enveloppe bruddgrense",
        "|v_xz|  [kN/m]", lambda p, n: nabs["Txz"].get((p, n), 0.0),
        cmap="viridis", vmin=0, vmax=1000, fill_levels=list(range(0, 1001, 100)),
        contour_per_plate=vrdc, contour_fmt="V_Rd,c", contour_color="red", contour_lw=2.2,
        maxmarks=mark_abs(nabs["Txz"], ["P.2", "P.4", "P.1", "P.3"]))

@reg("5-4")
def f54():
    F.panel_figure("vyz_enveloppe_uls.png", "Skjærkraft v_yz, absoluttverdi av enveloppe bruddgrense",
        "|v_yz|  [kN/m]", lambda p, n: nabs["Tyz"].get((p, n), 0.0),
        cmap="viridis", vmin=0, vmax=1000, fill_levels=list(range(0, 1001, 100)),
        contour_per_plate=vrdc, contour_fmt="V_Rd,c", maxmarks=mark_abs(nabs["Tyz"], ["P.2", "P.4", "P.1", "P.3"]))

@reg("5-7")
def f57():
    import numpy as np
    F.panel_figure("nedboyning_sls.png", "Vertikal nedbøyning i bruksgrense karakteristisk",
        "nedbøyning  [mm]", lambda p, n: -nminD.get((p, n), 0.0),
        cmap="viridis", vmin=0, vmax=2.0, fill_levels=[round(x, 2) for x in list(np.arange(0, 2.001, 0.25))],
        contour_levels=[0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75], contour_fmt="%.2f", maxmarks=mark_defl())

def reinf_fig(fname, title, q):
    F.panel_figure(fname, title, "A_s,krav  [mm²/m]", lambda p, n: nmaxR[q].get((p, n), 0.0),
        cmap="viridis", vmin=0, vmax=3500, fill_levels=list(range(0, 3501, 250)),
        contour_levels=[1340, 2094, 3272], contour_fmt=REBAR, maxmarks=mark_reinf(q))

@reg("6-1")
def f61(): reinf_fig("armering_uk_x.png", "Nødvendig armering i underkant, lengderetning", "XBottom")
@reg("6-2")
def f62(): reinf_fig("armering_uk_y.png", "Nødvendig armering i underkant, tverretning", "YBottom")
@reg("6-3")
def f63(): reinf_fig("armering_ok_x.png", "Nødvendig armering i overkant, lengderetning", "XTop")
@reg("6-4")
def f64(): reinf_fig("armering_ok_y.png", "Nødvendig armering i overkant, tverretning", "YTop")

@reg("6-5")
def f65():
    def cls(plate, e):
        v = max(emaxR[q].get((plate, e), 0.0) for q in ("XBottom", "YBottom", "XTop", "YTop"))
        if v <= 1340: return 0
        if v <= 2094: return 1
        if v <= 3272: return 2
        return 3
    F.zone_figure("armering_soner.png",
        "Soner der armeringsbehovet overstiger valgt armering (styrende retning/lag)",
        cls,
        [("Under min.arm. (≤ Ø16c150)", "0.82", None),
         ("Dekket av Ø20c150", "#a6d96a", None),
         ("Krever Ø25c150", "#fdae61", None),
         ("Krever lokal forsterkning", "#d7191c", None)])

@reg("6-6")
def f66():
    def cls(plate, e):
        if F.elem_border(plate, e): return 3
        vrd = 333.0 if plate in ("P.2", "P.4") else 216.0
        ved = max(eabs["Txz"].get((plate, e), 0.0), eabs["Tyz"].get((plate, e), 0.0))
        if ved <= vrd: return 0
        if ved <= 2 * vrd: return 1
        return 2
    F.zone_figure("skjaer_soner.png",
        "Soner der skjærkraften overstiger kapasiteten uten skjærarmering",
        cls,
        [("Under kapasitet (≤ V_Rd,c)", "0.82", None),
         ("Inntil 2·V_Rd,c", "#f6e400", None),
         ("Over 2·V_Rd,c", "#fdae61", None),
         ("Singularitetsutsatt (grenser opplegg)", "#d7191c", "////")])

if __name__ == "__main__":
    keys = sys.argv[1:] or list(FIGS.keys())
    for k in keys:
        if k in FIGS: FIGS[k]()
        else: print("unknown fig", k)
