using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FEMDesignDumper
{
    internal enum CalcMode
    {
        None,
        Static,
        Freq
    }

    /// <summary>
    /// Parsed command-line options for the dumper.
    /// </summary>
    internal sealed class CliOptions
    {
        public string ModelPath;
        public string OutputDir;
        public CalcMode Calc = CalcMode.None;
        public List<string> ResultTypes = new List<string>();   // empty => default set
        public bool WriteCsv = true;
        public bool WriteJson = true;
        public string FdInstallDir;                              // null => auto-detect
        public bool Gui;                                         // false => headless (FD_NOGUI)
        public bool KeepOpen;
        public bool Quiet;
        public int FreqShapes = 5;
        public List<string> Plots = new List<string>();   // empty => no plots
        public string PlotCap = "max";                     // colour-scale cap: max | p95 | p99

        // Control-flow flags: when set, the program prints something and exits 0.
        public bool ShowHelp;
        public bool ListTypes;

        public static CliOptions Parse(string[] args, out string error)
        {
            error = null;
            var o = new CliOptions();

            if (args.Length == 0)
            {
                o.ShowHelp = true;
                return o;
            }

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string key = arg;
                string inlineValue = null;

                if (arg.StartsWith("--"))
                {
                    int eq = arg.IndexOf('=');
                    if (eq >= 0)
                    {
                        key = arg.Substring(0, eq);
                        inlineValue = arg.Substring(eq + 1);
                    }
                }

                // Reads the value for an option, taking it either from --key=value
                // or from the following token.
                string TakeValue()
                {
                    if (inlineValue != null) return inlineValue;
                    if (i + 1 < args.Length) return args[++i];
                    throw new ArgumentException($"Option '{key}' requires a value.");
                }

                try
                {
                    switch (key)
                    {
                        case "-h":
                        case "--help":
                            o.ShowHelp = true;
                            return o;

                        case "--model":
                        case "-m":
                            o.ModelPath = TakeValue();
                            break;

                        case "--out":
                        case "-o":
                            o.OutputDir = TakeValue();
                            break;

                        case "--calc":
                            o.Calc = ParseCalc(TakeValue());
                            break;

                        case "--results":
                        case "-r":
                            {
                                string v = TakeValue();
                                if (string.Equals(v, "list", StringComparison.OrdinalIgnoreCase))
                                    o.ListTypes = true;
                                else if (string.Equals(v, "all", StringComparison.OrdinalIgnoreCase))
                                    o.ResultTypes = new List<string> { "all" };
                                else
                                    o.ResultTypes = v.Split(',')
                                        .Select(s => s.Trim())
                                        .Where(s => s.Length > 0)
                                        .ToList();
                            }
                            break;

                        case "--format":
                        case "-f":
                            {
                                var fmts = TakeValue().Split(',')
                                    .Select(s => s.Trim().ToLowerInvariant())
                                    .Where(s => s.Length > 0)
                                    .ToList();
                                o.WriteCsv = fmts.Contains("csv");
                                o.WriteJson = fmts.Contains("json");
                                if (!o.WriteCsv && !o.WriteJson)
                                    throw new ArgumentException("--format must include 'csv' and/or 'json'.");
                            }
                            break;

                        case "--fd-dir":
                            o.FdInstallDir = TakeValue();
                            break;

                        case "--freq-shapes":
                            {
                                string v = TakeValue();
                                if (!int.TryParse(v, out o.FreqShapes) || o.FreqShapes < 1)
                                    throw new ArgumentException("--freq-shapes must be a positive integer.");
                            }
                            break;

                        case "--plots":
                            {
                                string v = TakeValue();
                                if (string.Equals(v, "all", StringComparison.OrdinalIgnoreCase))
                                    o.Plots = Plotter.Groups.ToList();
                                else if (string.Equals(v, "none", StringComparison.OrdinalIgnoreCase))
                                    o.Plots = new List<string>();
                                else
                                    o.Plots = v.Split(',')
                                        .Select(s => s.Trim().ToLowerInvariant())
                                        .Where(s => s.Length > 0)
                                        .ToList();
                            }
                            break;

                        case "--plot-cap":
                            {
                                string v = TakeValue().ToLowerInvariant();
                                if (v != "max" && v != "p95" && v != "p99")
                                    throw new ArgumentException("--plot-cap must be max | p95 | p99.");
                                o.PlotCap = v;
                            }
                            break;

                        case "--gui":
                            o.Gui = true;
                            break;

                        case "--keep-open":
                            o.KeepOpen = true;
                            break;

                        case "--quiet":
                        case "-q":
                            o.Quiet = true;
                            break;

                        default:
                            error = $"Unknown option: {arg}";
                            return null;
                    }
                }
                catch (ArgumentException ex)
                {
                    error = ex.Message;
                    return null;
                }
            }

            if (o.ShowHelp || o.ListTypes)
                return o;

            if (string.IsNullOrWhiteSpace(o.ModelPath))
            {
                error = "Missing required option --model <path>.";
                return null;
            }

            o.ModelPath = Path.GetFullPath(o.ModelPath);
            if (!File.Exists(o.ModelPath))
            {
                error = $"Model file not found: {o.ModelPath}";
                return null;
            }

            if (string.IsNullOrWhiteSpace(o.OutputDir))
            {
                string dir = Path.GetDirectoryName(o.ModelPath);
                string name = Path.GetFileNameWithoutExtension(o.ModelPath);
                o.OutputDir = Path.Combine(dir, name + "_dump");
            }
            o.OutputDir = Path.GetFullPath(o.OutputDir);

            var badPlots = o.Plots.Where(g => !Plotter.Groups.Contains(g)).ToList();
            if (badPlots.Count > 0)
            {
                error = "Unknown --plots group(s): " + string.Join(", ", badPlots) +
                        ". Valid: " + string.Join(", ", Plotter.Groups) + ", all, none.";
                return null;
            }

            return o;
        }

        private static CalcMode ParseCalc(string v)
        {
            switch ((v ?? "").ToLowerInvariant())
            {
                case "none": return CalcMode.None;
                case "static": return CalcMode.Static;
                case "freq":
                case "frequency":
                case "eigen": return CalcMode.Freq;
                default:
                    throw new ArgumentException($"Invalid --calc value '{v}'. Use none | static | freq.");
            }
        }

        public static string HelpText()
        {
            return
@"FEMDesignDumper - open a FEM-Design model headlessly and dump results.

USAGE:
  FEMDesignDumper --model <path> [options]

REQUIRED:
  -m, --model <path>     FEM-Design model file (.str or .struxml).

OPTIONS:
  -o, --out <dir>        Output directory. Default: <model>_dump next to the model.
      --calc <mode>      none | static | freq. Default: none.
                         none   = dump results already stored in the model.
                         static = run static analysis (all cases + combinations) first.
                         freq   = run eigenfrequency analysis first.
      --freq-shapes <n>  Number of eigenshapes for --calc freq. Default: 5.
  -r, --results <list>   Comma-separated result types, or 'all', or 'list'.
                         Default: a curated set of common results.
                         Example: --results BarInternalForce,NodalDisplacement
  -f, --format <list>    csv,json (either or both). Default: csv,json.
      --plots <list>     Per-plate SVG colour maps with the peak annotated, or 'all'/'none'.
                         Groups: reinf, moment, shear, deflection. Default: none.
                         Written to <out>/plots/<surface>_<field>.svg. Needs shell results.
      --plot-cap <mode>  Colour-scale cap: max | p95 | p99. Default: max. (p95/p99 tame
                         support singularities; the annotated peak is always the true value.)
      --fd-dir <path>    FEM-Design install dir. Default: auto-detect FEM-Design 25.
      --gui              Show the FEM-Design window. Default: headless.
      --keep-open        Leave FEM-Design running after the dump.
  -q, --quiet            Suppress FEM-Design log output.
  -h, --help             Show this help.

OUTPUT:
  <out>/manifest.json        Machine-readable summary of the run and every file written.
  <out>/model_summary.json   Model counts, load case and load combination names.
  <out>/<ResultType>.csv     One flat table per result type.
  <out>/<ResultType>.json    Full-fidelity results per result type.

EXAMPLES:
  FEMDesignDumper -m model.str
  FEMDesignDumper -m model.struxml --calc static
  FEMDesignDumper -m model.str --results BarInternalForce,PointSupportReaction -f csv
  FEMDesignDumper --results list";
        }
    }
}
