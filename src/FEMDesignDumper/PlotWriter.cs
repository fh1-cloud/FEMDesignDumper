using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FEMDesignDumper
{
    /// <summary>One shell element projected to the plate's 2D plane, with a scalar value.</summary>
    internal sealed class PlotElement
    {
        public double[] U;
        public double[] V;
        public double Value;
    }

    /// <summary>Everything needed to render one plate/quantity figure.</summary>
    internal sealed class PlateFigure
    {
        public string Title;
        public string Subtitle;
        public string Unit;
        public string UAxisLabel = "u [m]";
        public string VAxisLabel = "v [m]";
        public List<PlotElement> Elements = new List<PlotElement>();
        public double PeakU, PeakV, PeakValue;
        public string PeakLabel;
        public bool PeakBordersSupport;
        public double ColorMax;   // colour-scale cap (>= 0). Peak annotation still shows the true value.
    }

    /// <summary>
    /// Renders a <see cref="PlateFigure"/> to a standalone SVG: element-filled colour map,
    /// axes, colour bar, and an annotated peak. Pure string building, no dependencies.
    /// </summary>
    internal static class PlotWriter
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

        private static IEnumerable<double> NiceTicks(double min, double max, int target)
        {
            double range = max - min;
            if (range <= 0) { yield return min; yield break; }
            double raw = range / Math.Max(1, target);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double norm = raw / mag;
            double step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
            double start = Math.Ceiling(min / step) * step;
            for (double t = start; t <= max + step * 1e-6; t += step) yield return t;
        }

        public static void Render(string path, PlateFigure fig)
        {
            const int W = 1000, H = 470;
            const int mL = 70, mR = 150, mT = 78, mB = 60;
            int pw = W - mL - mR, ph = H - mT - mB;

            double umin = double.MaxValue, umax = double.MinValue, vmin = double.MaxValue, vmax = double.MinValue;
            foreach (var el in fig.Elements)
                for (int i = 0; i < el.U.Length; i++)
                {
                    umin = Math.Min(umin, el.U[i]); umax = Math.Max(umax, el.U[i]);
                    vmin = Math.Min(vmin, el.V[i]); vmax = Math.Max(vmax, el.V[i]);
                }
            if (umax <= umin) umax = umin + 1;
            if (vmax <= vmin) vmax = vmin + 1;

            double s = Math.Min(pw / (umax - umin), ph / (vmax - vmin));
            double ox = mL + (pw - s * (umax - umin)) / 2.0;
            double oy = mT + (ph - s * (vmax - vmin)) / 2.0;
            Func<double, double> PX = u => ox + (u - umin) * s;
            Func<double, double> PY = v => oy + (vmax - v) * s;   // flip: larger v is up

            double cmax = fig.ColorMax > 0 ? fig.ColorMax : (fig.Elements.Count > 0 ? fig.Elements.Max(e => e.Value) : 1);
            if (cmax <= 0) cmax = 1;

            var sb = new StringBuilder();
            sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{W}\" height=\"{H}\" font-family=\"Segoe UI, Arial, sans-serif\">\n");
            sb.Append($"<rect width=\"{W}\" height=\"{H}\" fill=\"#ffffff\"/>\n");
            sb.Append($"<text x=\"{mL}\" y=\"34\" font-size=\"19\" font-weight=\"700\" fill=\"#1a1a1a\">{Esc(fig.Title)}</text>\n");
            if (!string.IsNullOrEmpty(fig.Subtitle))
                sb.Append($"<text x=\"{mL}\" y=\"56\" font-size=\"13\" fill=\"#555\">{Esc(fig.Subtitle)}</text>\n");

            foreach (var el in fig.Elements)
            {
                var (r, g, b) = Color(el.Value, cmax);
                var pts = new StringBuilder();
                for (int i = 0; i < el.U.Length; i++)
                {
                    if (i > 0) pts.Append(' ');
                    pts.Append(F(PX(el.U[i]))).Append(',').Append(F(PY(el.V[i])));
                }
                sb.Append($"<polygon points=\"{pts}\" fill=\"rgb({r},{g},{b})\" stroke=\"#ffffff\" stroke-width=\"0.3\"/>\n");
            }

            // frame
            sb.Append($"<rect x=\"{F(PX(umin))}\" y=\"{F(PY(vmax))}\" width=\"{F((umax - umin) * s)}\" height=\"{F((vmax - vmin) * s)}\" fill=\"none\" stroke=\"#333\" stroke-width=\"1\"/>\n");
            // u ticks
            foreach (double ut in NiceTicks(umin, umax, 8))
            {
                sb.Append($"<line x1=\"{F(PX(ut))}\" y1=\"{F(PY(vmin))}\" x2=\"{F(PX(ut))}\" y2=\"{F(PY(vmin) + 5)}\" stroke=\"#333\"/>\n");
                sb.Append($"<text x=\"{F(PX(ut))}\" y=\"{F(PY(vmin) + 19)}\" font-size=\"11\" fill=\"#333\" text-anchor=\"middle\">{F(ut)}</text>\n");
            }
            // v ticks
            foreach (double vt in NiceTicks(vmin, vmax, 5))
            {
                sb.Append($"<line x1=\"{F(PX(umin) - 5)}\" y1=\"{F(PY(vt))}\" x2=\"{F(PX(umin))}\" y2=\"{F(PY(vt))}\" stroke=\"#333\"/>\n");
                sb.Append($"<text x=\"{F(PX(umin) - 9)}\" y=\"{F(PY(vt) + 4)}\" font-size=\"11\" fill=\"#333\" text-anchor=\"end\">{F(vt)}</text>\n");
            }
            sb.Append($"<text x=\"{F((PX(umin) + PX(umax)) / 2)}\" y=\"{H - 18}\" font-size=\"12\" fill=\"#333\" text-anchor=\"middle\">{Esc(fig.UAxisLabel)}</text>\n");
            double vmid = (PY(vmin) + PY(vmax)) / 2;
            sb.Append($"<text x=\"22\" y=\"{F(vmid)}\" font-size=\"12\" fill=\"#333\" text-anchor=\"middle\" transform=\"rotate(-90 22 {F(vmid)})\">{Esc(fig.VAxisLabel)}</text>\n");

            // peak marker + adaptive label
            double mx = PX(fig.PeakU), my = PY(fig.PeakV);
            bool below = my < mT + 100;
            double leadY2 = below ? my + 30 : my - 34;
            double ly = below ? my + 40 : my - 38;
            double lx = mx + 40;
            if (lx + 200 > W - mR + 20) lx = mx - 244;
            string border = fig.PeakBordersSupport ? " · ved opplegg" : "";
            sb.Append($"<circle cx=\"{F(mx)}\" cy=\"{F(my)}\" r=\"7\" fill=\"none\" stroke=\"#111\" stroke-width=\"2\"/>\n");
            sb.Append($"<line x1=\"{F(mx)}\" y1=\"{F(my)}\" x2=\"{F(lx + 16)}\" y2=\"{F(leadY2)}\" stroke=\"#111\" stroke-width=\"1.2\"/>\n");
            sb.Append($"<rect x=\"{F(lx - 4)}\" y=\"{F(ly - 15)}\" width=\"200\" height=\"40\" rx=\"4\" fill=\"#111\" opacity=\"0.88\"/>\n");
            sb.Append($"<text x=\"{F(lx)}\" y=\"{F(ly)}\" font-size=\"13\" font-weight=\"700\" fill=\"#fff\">Topp: {N0(fig.PeakValue)} {Esc(fig.Unit)}</text>\n");
            sb.Append($"<text x=\"{F(lx)}\" y=\"{F(ly + 16)}\" font-size=\"11\" fill=\"#ddd\">u={F(fig.PeakU)}, v={F(fig.PeakV)} m · {Esc(fig.PeakLabel)}{border}</text>\n");

            // colour bar
            int cbx = W - mR + 34, cby = mT + 6, cbw = 20, cbh = ph - 20;
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

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }
    }
}
