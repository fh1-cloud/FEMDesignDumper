using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FEMDesignDumper
{
    /// <summary>One shell element as a 3D polygon (node coordinates) plus a scalar value.</summary>
    internal sealed class Element3D
    {
        public double[][] Xyz;   // Xyz[i] = { x, y, z }
        public double Value;
    }

    /// <summary>A whole-structure isometric figure: every plate's elements in one 3D view.</summary>
    internal sealed class IsoFigure
    {
        public string Title;
        public string Subtitle;
        public string Unit;
        public List<Element3D> Elements = new List<Element3D>();
        public double AzDeg = 45;   // rotation about the vertical (Z) axis
        public double ElDeg = 30;   // tilt down
        public double ColorMax;
        public double[] PeakXyz;
        public double PeakValue;
        public string PeakLabel;
        public bool PeakBordersSupport;
        public List<KeyValuePair<string, double[]>> PlateLabels = new List<KeyValuePair<string, double[]>>();
    }

    /// <summary>
    /// Renders an <see cref="IsoFigure"/> as an axonometric ("isometric") SVG: the full
    /// structure in one view, elements painter-sorted by depth and coloured by value, with a
    /// colour bar, an orientation triad, faint plate labels, and the global peak annotated.
    /// </summary>
    internal static class IsoWriter
    {
        private static readonly (double t, int r, int g, int b)[] Stops =
        {
            (0.00,  33,  60, 130),
            (0.25,  32, 150, 175),
            (0.50,  60, 175,  90),
            (0.75, 240, 200,  50),
            (1.00, 200,  40,  40),
        };

        private static (int r, int g, int b) Color(double v, double vmax)
        {
            double t = vmax <= 0 ? 0 : Math.Max(0.0, Math.Min(1.0, v / vmax));
            for (int i = 0; i < Stops.Length - 1; i++)
            {
                var a = Stops[i]; var b = Stops[i + 1];
                if (t >= a.t && t <= b.t)
                {
                    double f = b.t == a.t ? 0 : (t - a.t) / (b.t - a.t);
                    return ((int)Math.Round(a.r + (b.r - a.r) * f),
                            (int)Math.Round(a.g + (b.g - a.g) * f),
                            (int)Math.Round(a.b + (b.b - a.b) * f));
                }
            }
            var last = Stops[Stops.Length - 1];
            return (last.r, last.g, last.b);
        }

        private static string F(double n) => n.ToString("0.##", CultureInfo.InvariantCulture);
        private static string N0(double n) => n.ToString("#,0", CultureInfo.InvariantCulture);

        /// <summary>Axonometric projection: 3D world (x span, y transverse, z up) -> (sx, sy up, depth).</summary>
        private static (double sx, double sy, double depth) Project(double x, double y, double z, double az, double el)
        {
            double ca = Math.Cos(az), sa = Math.Sin(az), ce = Math.Cos(el), se = Math.Sin(el);
            double x1 = x * ca - y * sa;
            double y1 = x * sa + y * ca;
            double z1 = z;
            double sx = x1;
            double sy = y1 * se + z1 * ce;         // screen up (+)
            double depth = y1 * ce - z1 * se;      // into the screen; larger = nearer camera
            return (sx, sy, depth);
        }

        public static void Render(string path, IsoFigure fig)
        {
            const int W = 1120, H = 640;
            const int mL = 30, mR = 150, mT = 78, mB = 40;
            int pw = W - mL - mR, ph = H - mT - mB;
            double az = fig.AzDeg * Math.PI / 180.0, el = fig.ElDeg * Math.PI / 180.0;

            // project all element nodes; track 2D bounds
            double sxmin = double.MaxValue, sxmax = double.MinValue, symin = double.MaxValue, symax = double.MinValue;
            var projPolys = new List<(double[] sx, double[] sy, double depth, double value)>(fig.Elements.Count);
            foreach (var e in fig.Elements)
            {
                int n = e.Xyz.Length;
                var px = new double[n]; var py = new double[n]; double dsum = 0;
                for (int i = 0; i < n; i++)
                {
                    var (sx, sy, d) = Project(e.Xyz[i][0], e.Xyz[i][1], e.Xyz[i][2], az, el);
                    px[i] = sx; py[i] = sy; dsum += d;
                    sxmin = Math.Min(sxmin, sx); sxmax = Math.Max(sxmax, sx);
                    symin = Math.Min(symin, sy); symax = Math.Max(symax, sy);
                }
                projPolys.Add((px, py, dsum / n, e.Value));
            }
            if (sxmax <= sxmin) sxmax = sxmin + 1;
            if (symax <= symin) symax = symin + 1;

            double s = Math.Min(pw / (sxmax - sxmin), ph / (symax - symin));
            double ox = mL + (pw - s * (sxmax - sxmin)) / 2.0;
            double oy = mT + (ph - s * (symax - symin)) / 2.0;
            Func<double, double> TX = sx => ox + (sx - sxmin) * s;
            Func<double, double> TY = sy => oy + (symax - sy) * s;   // flip: up on screen

            double cmax = fig.ColorMax > 0 ? fig.ColorMax
                : (fig.Elements.Count > 0 ? fig.Elements.Max(e => e.Value) : 1);
            if (cmax <= 0) cmax = 1;

            var sb = new StringBuilder();
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{W}\" height=\"{H}\" font-family=\"Segoe UI, Arial, sans-serif\">\n");
            sb.Append($"<rect width=\"{W}\" height=\"{H}\" fill=\"#ffffff\"/>\n");
            sb.Append($"<text x=\"{mL}\" y=\"34\" font-size=\"19\" font-weight=\"700\" fill=\"#1a1a1a\">{Esc(fig.Title)}</text>\n");
            if (!string.IsNullOrEmpty(fig.Subtitle))
                sb.Append($"<text x=\"{mL}\" y=\"56\" font-size=\"13\" fill=\"#555\">{Esc(fig.Subtitle)}</text>\n");

            // painter's algorithm: far (small depth) first
            foreach (var p in projPolys.OrderBy(p => p.depth))
            {
                var (r, g, b) = Color(p.value, cmax);
                var pts = new StringBuilder();
                for (int i = 0; i < p.sx.Length; i++)
                {
                    if (i > 0) pts.Append(' ');
                    pts.Append(F(TX(p.sx[i]))).Append(',').Append(F(TY(p.sy[i])));
                }
                sb.Append($"<polygon points=\"{pts}\" fill=\"rgb({r},{g},{b})\" stroke=\"#ffffff\" stroke-width=\"0.35\"/>\n");
            }

            // faint plate labels at plate centroids
            foreach (var pl in fig.PlateLabels)
            {
                var (sx, sy, _) = Project(pl.Value[0], pl.Value[1], pl.Value[2], az, el);
                sb.Append($"<text x=\"{F(TX(sx))}\" y=\"{F(TY(sy))}\" font-size=\"12\" font-weight=\"700\" fill=\"#1a1a1a\" opacity=\"0.55\" text-anchor=\"middle\">{Esc(pl.Key)}</text>\n");
            }

            // orientation triad (lower-left)
            DrawTriad(sb, 64, H - 70, s, az, el);

            // peak annotation
            if (fig.PeakXyz != null)
            {
                var (sx, sy, _) = Project(fig.PeakXyz[0], fig.PeakXyz[1], fig.PeakXyz[2], az, el);
                double mx = TX(sx), my = TY(sy);
                bool below = my < mT + 100;
                double leadY2 = below ? my + 30 : my - 34;
                double ly = below ? my + 40 : my - 38;
                double lx = mx + 40;
                if (lx + 210 > W - mR + 20) lx = mx - 254;
                string bd = fig.PeakBordersSupport ? " · ved opplegg" : "";
                sb.Append($"<circle cx=\"{F(mx)}\" cy=\"{F(my)}\" r=\"7\" fill=\"none\" stroke=\"#111\" stroke-width=\"2\"/>\n");
                sb.Append($"<line x1=\"{F(mx)}\" y1=\"{F(my)}\" x2=\"{F(lx + 16)}\" y2=\"{F(leadY2)}\" stroke=\"#111\" stroke-width=\"1.2\"/>\n");
                sb.Append($"<rect x=\"{F(lx - 4)}\" y=\"{F(ly - 15)}\" width=\"210\" height=\"40\" rx=\"4\" fill=\"#111\" opacity=\"0.88\"/>\n");
                sb.Append($"<text x=\"{F(lx)}\" y=\"{F(ly)}\" font-size=\"13\" font-weight=\"700\" fill=\"#fff\">Topp: {N0(fig.PeakValue)} {Esc(fig.Unit)}</text>\n");
                sb.Append($"<text x=\"{F(lx)}\" y=\"{F(ly + 16)}\" font-size=\"11\" fill=\"#ddd\">{Esc(fig.PeakLabel)}{bd}</text>\n");
            }

            // colour bar
            int cbx = W - mR + 40, cby = mT + 6, cbw = 20, cbh = ph - 20;
            sb.Append("<defs><linearGradient id=\"cb\" x1=\"0\" y1=\"1\" x2=\"0\" y2=\"0\">\n");
            for (int i = 0; i <= 100; i += 5)
            {
                var (r, g, b) = Color(cmax * i / 100.0, cmax);
                sb.Append($"<stop offset=\"{i}%\" stop-color=\"rgb({r},{g},{b})\"/>\n");
            }
            sb.Append("</linearGradient></defs>\n");
            sb.Append($"<rect x=\"{cbx}\" y=\"{cby}\" width=\"{cbw}\" height=\"{cbh}\" fill=\"url(#cb)\" stroke=\"#333\" stroke-width=\"0.5\"/>\n");
            for (int i = 0; i <= 5; i++)
            {
                double v = cmax * i / 5.0;
                double yy = cby + cbh - cbh * i / 5.0;
                sb.Append($"<line x1=\"{cbx + cbw}\" y1=\"{F(yy)}\" x2=\"{cbx + cbw + 4}\" y2=\"{F(yy)}\" stroke=\"#333\"/>\n");
                sb.Append($"<text x=\"{cbx + cbw + 7}\" y=\"{F(yy + 4)}\" font-size=\"11\" fill=\"#333\">{N0(v)}</text>\n");
            }
            sb.Append($"<text x=\"{cbx}\" y=\"{cby - 8}\" font-size=\"11\" fill=\"#333\">{Esc(fig.Unit)}</text>\n");
            sb.Append("</svg>\n");

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static void DrawTriad(StringBuilder sb, double cx, double cy, double worldScale, double az, double el)
        {
            double len = 40; // screen px per axis arrow
            var origin = Project(0, 0, 0, az, el);
            (string lbl, double x, double y, double z, string col)[] ax =
            {
                ("x", 1, 0, 0, "#c0392b"),
                ("y", 0, 1, 0, "#27ae60"),
                ("z", 0, 0, 1, "#2c3e99"),
            };
            foreach (var a in ax)
            {
                var p = Project(a.x, a.y, a.z, az, el);
                double dx = p.sx - origin.sx, dy = p.sy - origin.sy;
                double nrm = Math.Sqrt(dx * dx + dy * dy); if (nrm == 0) nrm = 1;
                double ex = cx + dx / nrm * len, ey = cy - dy / nrm * len;
                sb.Append($"<line x1=\"{F(cx)}\" y1=\"{F(cy)}\" x2=\"{F(ex)}\" y2=\"{F(ey)}\" stroke=\"{a.col}\" stroke-width=\"2\"/>\n");
                sb.Append($"<text x=\"{F(ex + (ex - cx) * 0.12)}\" y=\"{F(ey + (ey - cy) * 0.12 + 4)}\" font-size=\"12\" font-weight=\"700\" fill=\"{a.col}\" text-anchor=\"middle\">{a.lbl}</text>\n");
            }
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
