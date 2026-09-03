using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using FemDesign;
using FemDesign.Results;

namespace FEMDesignDumper
{
    /// <summary>
    /// Route B: renders per-plate colour maps of enveloped result quantities (required
    /// reinforcement, shell moments/shear, deflection) straight from the model's mesh and
    /// results, with the true peak annotated. Each planar plate is projected to its own 2D
    /// plane (the near-constant global axis is dropped).
    /// </summary>
    internal static class Plotter
    {
        public static readonly string[] Groups = { "reinf", "moment", "shear", "deflection" };

        private sealed class Elem { public string Plate; public int Id; public int[] Nodes; }

        private static string SurfaceOf(string id)
        {
            var p = id.Split('.');
            return p.Length >= 2 ? p[0] + "." + p[1] : id;
        }

        private static readonly string[] AxisName = { "x", "y", "z" };

        public static List<string> Generate(FemDesignConnection conn, UnitResults units, string outDir,
            List<string> groups, HashSet<string> uls, HashSet<string> sls, string capMode, string viewMode,
            Action<string> log)
        {
            var written = new List<string>();
            string plotsDir = Path.Combine(outDir, "plots");

            var coord = new Dictionary<int, double[]>();
            foreach (var n in conn.GetResults<FemNode>(units))
                coord[n.NodeId] = new[] { n.X, n.Y, n.Z };

            var shells = conn.GetResults<FemShell>(units)
                .Select(s => new Elem
                {
                    Plate = s.Id,
                    Id = s.ElementId,
                    Nodes = new[] { s.Node1, s.Node2, s.Node3, s.Node4 }
                        .Where(nd => nd != 0 && coord.ContainsKey(nd)).ToArray()
                })
                .Where(e => e.Nodes.Length >= 3).ToList();

            if (shells.Count == 0)
            {
                log("  plots: no shell elements found - nothing to plot.");
                return written;
            }

            var support = new HashSet<int>();
            try { foreach (var r in conn.GetResults<PointSupportReaction>(units)) support.Add(r.NodeId); } catch { }
            try { foreach (var r in conn.GetResults<LineSupportReaction>(units)) support.Add(r.NodeId); } catch { }

            var bySurface = shells.GroupBy(e => SurfaceOf(e.Plate))
                                  .ToDictionary(g => g.Key, g => g.ToList());
            var axes = bySurface.ToDictionary(kv => kv.Key, kv => ProjectAxes(kv.Value, coord));
            var allElems = bySurface.Values.SelectMany(x => x).ToList();
            bool doPlate = viewMode == "plate" || viewMode == "both";
            bool doIso = viewMode == "iso" || viewMode == "both";

            // cache source result lists (fetched at most once)
            List<RCShellReinforcementRequired> reinf = null;
            List<ShellInternalForce> shellForce = null;
            List<NodalDisplacement> disp = null;

            foreach (string group in groups)
            {
                if (group == "reinf")
                {
                    if (reinf == null) reinf = conn.GetResults<RCShellReinforcementRequired>(units);
                    var fields = new (string name, Func<RCShellReinforcementRequired, double> sel, string title, string unit)[]
                    {
                        ("XBottom", x => x.XBottom, "nødvendig armering underkant, x′-retning", "mm²/m"),
                        ("YBottom", x => x.YBottom, "nødvendig armering underkant, y′-retning", "mm²/m"),
                        ("XTop",    x => x.XTop,    "nødvendig armering overkant, x′-retning",  "mm²/m"),
                        ("YTop",    x => x.YTop,    "nødvendig armering overkant, y′-retning",  "mm²/m"),
                    };
                    foreach (var f in fields)
                    {
                        var vals = ReduceMaxElement(reinf.Select(x => (x.Id, x.ElementId, x.CaseIdentifier, f.sel(x))), uls);
                        written.AddRange(Emit(plotsDir, bySurface, allElems, axes, coord, support, vals,
                            f.name, f.title, f.unit, EnvNote("maks", uls.Count, "ULS"), capMode, doPlate, doIso, log));
                    }
                }
                else if (group == "moment" || group == "shear")
                {
                    if (shellForce == null) shellForce = conn.GetResults<ShellInternalForce>(units);
                    (string name, Func<ShellInternalForce, double> sel, string title, string unit)[] fields =
                        group == "moment"
                        ? new (string, Func<ShellInternalForce, double>, string, string)[]
                          {
                              ("Mx",  x => x.Mx,  "moment m′x (|maks| ULS)",  "kNm/m"),
                              ("My",  x => x.My,  "moment m′y (|maks| ULS)",  "kNm/m"),
                              ("Mxy", x => x.Mxy, "moment m′xy (|maks| ULS)", "kNm/m"),
                          }
                        : new (string, Func<ShellInternalForce, double>, string, string)[]
                          {
                              ("Txz", x => x.Txz, "skjær v′xz (|maks| ULS)", "kN/m"),
                              ("Tyz", x => x.Tyz, "skjær v′yz (|maks| ULS)", "kN/m"),
                          };
                    foreach (var f in fields)
                    {
                        var vals = ReduceMaxElement(
                            shellForce.Select(x => (x.Id, x.ElementId, x.CaseIdentifier, Math.Abs(f.sel(x)))), uls);
                        written.AddRange(Emit(plotsDir, bySurface, allElems, axes, coord, support, vals,
                            f.name, f.title, f.unit, EnvNote("|maks|", uls.Count, "ULS"), capMode, doPlate, doIso, log));
                    }
                }
                else if (group == "deflection")
                {
                    if (disp == null) disp = conn.GetResults<NodalDisplacement>(units);
                    // node envelope: max downward (-Ez) over SLS
                    var nodeVal = new Dictionary<int, (double v, string c)>();
                    foreach (var d in disp)
                    {
                        string cc = ResultWriter.CleanString(d.CaseIdentifier);
                        if (!sls.Contains(cc)) continue;
                        double sv = -d.Ez;
                        if (!nodeVal.TryGetValue(d.NodeId, out var cur) || sv > cur.v)
                            nodeVal[d.NodeId] = (sv, cc);
                    }
                    var vals = new Dictionary<(string, int), (double, string)>();
                    foreach (var e in shells)
                    {
                        var ns = e.Nodes.Where(nodeVal.ContainsKey).ToList();
                        if (ns.Count == 0) continue;
                        double mean = ns.Average(nd => nodeVal[nd].v);
                        string comb = nodeVal[ns.OrderByDescending(nd => nodeVal[nd].v).First()].c;
                        vals[(e.Plate, e.Id)] = (mean, comb);
                    }
                    written.AddRange(Emit(plotsDir, bySurface, allElems, axes, coord, support, vals,
                        "Ez", "nedbøyning nedover, SLS", "mm", EnvNote("maks", sls.Count, "SLS"), capMode, doPlate, doIso, log));
                }
                else
                {
                    log($"  plots: unknown group '{group}' (use {string.Join("/", Groups)}).");
                }
            }
            return written;
        }

        private static string EnvNote(string kind, int n, string kindName)
            => $"Enveloppe {kind} over {n} {kindName}-kombinasjoner";

        private static List<string> Emit(
            string plotsDir,
            Dictionary<string, List<Elem>> bySurface,
            List<Elem> allElems,
            Dictionary<string, (int u, int v)> axes,
            Dictionary<int, double[]> coord,
            HashSet<int> support,
            Dictionary<(string, int), (double val, string comb)> vals,
            string field, string title, string unit, string envNote, string capMode,
            bool doPlate, bool doIso, Action<string> log)
        {
            var written = new List<string>();
            if (doPlate)
                written.AddRange(RenderSurfaces(plotsDir, bySurface, axes, coord, support, vals,
                    field, title, unit, envNote, capMode, log));
            if (doIso)
            {
                string p = RenderIso(plotsDir, bySurface, allElems, coord, support, vals,
                    field, title, unit, envNote, capMode, log);
                if (p != null) written.Add(p);
            }
            return written;
        }

        private static string RenderIso(
            string plotsDir,
            Dictionary<string, List<Elem>> bySurface,
            List<Elem> allElems,
            Dictionary<int, double[]> coord,
            HashSet<int> support,
            Dictionary<(string, int), (double val, string comb)> vals,
            string field, string title, string unit, string envNote, string capMode, Action<string> log)
        {
            var fig = new IsoFigure
            {
                Title = "Hele konstruksjonen — " + title,
                Subtitle = envNote + " · enhet " + unit,
                Unit = unit,
            };
            var allVals = new List<double>();
            Elem peakElem = null; double peakVal = double.NegativeInfinity; string peakComb = "";

            foreach (var surface in bySurface.Keys.OrderBy(k => k))
            {
                var elems = bySurface[surface];
                var pnodes = elems.SelectMany(e => e.Nodes).Distinct().Where(coord.ContainsKey).ToList();
                if (pnodes.Count > 0)
                {
                    var c = new double[3];
                    foreach (var nd in pnodes) { var xyz = coord[nd]; c[0] += xyz[0]; c[1] += xyz[1]; c[2] += xyz[2]; }
                    c[0] /= pnodes.Count; c[1] /= pnodes.Count; c[2] /= pnodes.Count;
                    fig.PlateLabels.Add(new KeyValuePair<string, double[]>(surface, c));
                }
                foreach (var e in elems)
                {
                    if (!vals.TryGetValue((e.Plate, e.Id), out var vc)) continue;
                    fig.Elements.Add(new Element3D { Xyz = e.Nodes.Select(nd => coord[nd]).ToArray(), Value = vc.val });
                    allVals.Add(vc.val);
                    if (vc.val > peakVal) { peakVal = vc.val; peakElem = e; peakComb = vc.comb; }
                }
            }

            if (fig.Elements.Count == 0 || peakElem == null)
            {
                log($"  plots: iso {field}: no data, skipped.");
                return null;
            }

            double eps = Math.Max(1e-6, Math.Abs(peakVal) * 1e-9);
            var atMax = allElems.Where(e => vals.TryGetValue((e.Plate, e.Id), out var v) && v.val >= peakVal - eps).ToList();
            var borderEl = atMax.FirstOrDefault(e => e.Nodes.Any(support.Contains));
            if (borderEl != null) { peakElem = borderEl; peakComb = vals[(borderEl.Plate, borderEl.Id)].comb; }

            var pc = new double[3];
            foreach (var nd in peakElem.Nodes) { var xyz = coord[nd]; pc[0] += xyz[0]; pc[1] += xyz[1]; pc[2] += xyz[2]; }
            pc[0] /= peakElem.Nodes.Length; pc[1] /= peakElem.Nodes.Length; pc[2] /= peakElem.Nodes.Length;

            fig.PeakXyz = pc;
            fig.PeakValue = peakVal;
            fig.PeakLabel = $"{peakElem.Plate}#{peakElem.Id} ({peakComb})";
            fig.PeakBordersSupport = borderEl != null;
            fig.ColorMax = capMode == "max" ? allVals.Max() : Percentile(allVals, capMode == "p99" ? 99 : 95);

            string path = Path.Combine(plotsDir, $"iso_{field}.svg");
            IsoWriter.Render(path, fig);
            log($"  plot: iso {field}  peak={peakVal:0.#} {unit}{(fig.PeakBordersSupport ? " (ved opplegg)" : "")}");
            return path;
        }

        private static List<string> RenderSurfaces(
            string plotsDir,
            Dictionary<string, List<Elem>> bySurface,
            Dictionary<string, (int u, int v)> axes,
            Dictionary<int, double[]> coord,
            HashSet<int> support,
            Dictionary<(string, int), (double val, string comb)> vals,
            string field, string title, string unit, string envNote, string capMode, Action<string> log)
        {
            var written = new List<string>();
            foreach (var surface in bySurface.Keys.OrderBy(k => k))
            {
                var (uAxis, vAxis) = axes[surface];
                var fig = new PlateFigure
                {
                    Title = $"{surface} — {title}",
                    Subtitle = $"{envNote} · enhet {unit}",
                    Unit = unit,
                    UAxisLabel = AxisName[uAxis] + " [m]",
                    VAxisLabel = AxisName[vAxis] + " [m]",
                };

                var elemVals = new List<double>();
                Elem peakElem = null; double peakVal = double.NegativeInfinity; string peakComb = "";
                foreach (var e in bySurface[surface])
                {
                    if (!vals.TryGetValue((e.Plate, e.Id), out var vc)) continue;
                    fig.Elements.Add(new PlotElement
                    {
                        U = e.Nodes.Select(nd => coord[nd][uAxis]).ToArray(),
                        V = e.Nodes.Select(nd => coord[nd][vAxis]).ToArray(),
                        Value = vc.val
                    });
                    elemVals.Add(vc.val);
                    if (vc.val > peakVal) { peakVal = vc.val; peakElem = e; peakComb = vc.comb; }
                }

                if (fig.Elements.Count == 0 || peakElem == null)
                {
                    log($"  plots: {surface} {field}: no data, skipped.");
                    continue;
                }

                // The peak value can be shared by several elements around one mesh node
                // (reinforcement/forces are reported per node). If any of those elements
                // borders a support the peak is singularity-influenced, so prefer such an
                // element for the annotation and flag it accordingly.
                double eps = Math.Max(1e-6, Math.Abs(peakVal) * 1e-9);
                var atMax = bySurface[surface]
                    .Where(e => vals.TryGetValue((e.Plate, e.Id), out var v) && v.val >= peakVal - eps)
                    .ToList();
                var borderEl = atMax.FirstOrDefault(e => e.Nodes.Any(support.Contains));
                if (borderEl != null) { peakElem = borderEl; peakComb = vals[(borderEl.Plate, borderEl.Id)].comb; }

                fig.PeakU = peakElem.Nodes.Average(nd => coord[nd][uAxis]);
                fig.PeakV = peakElem.Nodes.Average(nd => coord[nd][vAxis]);
                fig.PeakValue = peakVal;
                fig.PeakLabel = $"{peakElem.Plate}#{peakElem.Id} ({peakComb})";
                fig.PeakBordersSupport = borderEl != null;
                fig.ColorMax = capMode == "max"
                    ? elemVals.Max()
                    : Percentile(elemVals, capMode == "p99" ? 99 : 95);

                string path = Path.Combine(plotsDir, $"{surface}_{field}.svg");
                PlotWriter.Render(path, fig);
                written.Add(path);
                log($"  plot: {surface} {field}  peak={peakVal:0.#} {unit}{(fig.PeakBordersSupport ? " (ved opplegg)" : "")}");
            }
            return written;
        }

        private static Dictionary<(string, int), (double, string)> ReduceMaxElement(
            IEnumerable<(string plate, int elem, string caseId, double scalar)> rows, HashSet<string> allowed)
        {
            var acc = new Dictionary<(string, int), (double v, string c)>();
            foreach (var r in rows)
            {
                string cc = ResultWriter.CleanString(r.caseId);
                if (!allowed.Contains(cc)) continue;
                var key = (r.plate, r.elem);
                if (!acc.TryGetValue(key, out var cur) || r.scalar > cur.v)
                    acc[key] = (r.scalar, cc);
            }
            return acc.ToDictionary(kv => kv.Key, kv => ((double, string))(kv.Value.v, kv.Value.c));
        }

        private static (int u, int v) ProjectAxes(List<Elem> elems, Dictionary<int, double[]> coord)
        {
            double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue };
            double[] mx = { double.MinValue, double.MinValue, double.MinValue };
            foreach (var e in elems)
                foreach (var nd in e.Nodes)
                {
                    var c = coord[nd];
                    for (int k = 0; k < 3; k++) { mn[k] = Math.Min(mn[k], c[k]); mx[k] = Math.Max(mx[k], c[k]); }
                }
            double[] range = { mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2] };
            int drop = 0;
            for (int k = 1; k < 3; k++) if (range[k] < range[drop]) drop = k;
            var rem = new List<int>();
            for (int k = 0; k < 3; k++) if (k != drop) rem.Add(k);
            // horizontal axis = larger range
            if (range[rem[0]] >= range[rem[1]]) return (rem[0], rem[1]);
            return (rem[1], rem[0]);
        }

        private static double Percentile(List<double> v, double p)
        {
            if (v.Count == 0) return 0;
            var s = v.OrderBy(x => x).ToList();
            if (s.Count == 1) return s[0];
            double r = p / 100.0 * (s.Count - 1);
            int lo = (int)Math.Floor(r), hi = (int)Math.Ceiling(r);
            if (lo == hi) return s[lo];
            return s[lo] + (s[hi] - s[lo]) * (r - lo);
        }
    }
}
