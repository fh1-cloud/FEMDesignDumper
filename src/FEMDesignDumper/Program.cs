using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using FemDesign;
using FemDesign.Calculate;
using FemDesign.Results;

namespace FEMDesignDumper
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            CliOptions opts = CliOptions.Parse(args, out string error);
            if (error != null)
            {
                Console.Error.WriteLine("Error: " + error);
                Console.Error.WriteLine();
                Console.Error.WriteLine("Run with --help for usage.");
                return 1;
            }

            if (opts.ShowHelp)
            {
                Console.WriteLine(CliOptions.HelpText());
                return 0;
            }

            if (opts.ListTypes)
            {
                Console.WriteLine("Available result types (* = in default set):");
                foreach (ResultKind k in Registry)
                    Console.WriteLine("  {0} {1}", k.InDefault ? "*" : " ", k.Name);
                return 0;
            }

            try
            {
                return Run(opts);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Fatal: " + ex.Message);
                Console.Error.WriteLine(ex.StackTrace);
                return 2;
            }
        }

        private static int Run(CliOptions opts)
        {
            Directory.CreateDirectory(opts.OutputDir);

            List<ResultKind> selected = SelectResultKinds(opts, out string selectError);
            if (selectError != null)
            {
                Console.Error.WriteLine("Error: " + selectError);
                return 1;
            }

            var units = new UnitResults(); // kN, m, mm displacement, MPa, deg, mm sections
            var resultReports = new List<Dictionary<string, object>>();
            Dictionary<string, object> modelSummary = null;

            Log(opts, $"Opening model: {opts.ModelPath}");
            Log(opts, $"Mode: calc={opts.Calc.ToString().ToLowerInvariant()}, gui={(opts.Gui ? "on" : "off")}");

            // Give FemDesign.Core its own temp scratch dir (generated scripts, logs,
            // intermediate result lists, a re-serialized model copy). Otherwise it defaults
            // to ".\FEM-Design API" under the current directory, dropping model data there.
            // tempOutputDir: true removes it on Dispose.
            string scratchDir = Path.Combine(Path.GetTempPath(), "FEMDesignDumper",
                Guid.NewGuid().ToString("N"));

            using (var connection = new FemDesignConnection(
                fdInstallationDir: opts.FdInstallDir,
                minimized: !opts.Gui,
                keepOpen: opts.KeepOpen,
                outputDir: scratchDir,
                tempOutputDir: true,
                verbosity: opts.Quiet ? Verbosity.None : Verbosity.Normal))
            {
                if (!opts.Quiet)
                    connection.OnOutput = msg => Console.Error.WriteLine("  [FD] " + msg);

                connection.Open(opts.ModelPath);

                if (opts.Calc == CalcMode.Static)
                {
                    Log(opts, "Running static analysis (cases + combinations)...");
                    connection.RunAnalysis(Analysis.StaticAnalysis());
                }
                else if (opts.Calc == CalcMode.Freq)
                {
                    Log(opts, $"Running eigenfrequency analysis ({opts.FreqShapes} shapes)...");
                    // Explicit args pick the (int, int, bool, bool, bool, double) overload
                    // unambiguously.
                    connection.RunAnalysis(Analysis.Eigenfrequencies(opts.FreqShapes, 0, true, true, true, -0.01));
                }

                modelSummary = BuildModelSummary(connection);
                if (opts.WriteJson || opts.WriteCsv)
                    ResultWriter.WriteJson(Path.Combine(opts.OutputDir, "model_summary.json"), modelSummary);

                foreach (ResultKind kind in selected)
                {
                    var report = new Dictionary<string, object> { ["type"] = kind.Name };
                    var files = new List<string>();
                    try
                    {
                        IReadOnlyList<object> rows = kind.Fetch(connection, units);
                        report["status"] = rows.Count > 0 ? "ok" : "empty";
                        report["rows"] = rows.Count;

                        if (rows.Count > 0)
                        {
                            if (opts.WriteJson)
                            {
                                string p = kind.Name + ".json";
                                ResultWriter.WriteJson(Path.Combine(opts.OutputDir, p), rows);
                                files.Add(p);
                            }
                            if (opts.WriteCsv)
                            {
                                string p = kind.Name + ".csv";
                                ResultWriter.WriteCsv(Path.Combine(opts.OutputDir, p), rows);
                                files.Add(p);
                            }
                        }
                        Log(opts, $"  {kind.Name}: {report["status"]} ({rows.Count} rows)");
                    }
                    catch (Exception ex)
                    {
                        report["status"] = "error";
                        report["rows"] = 0;
                        report["message"] = ex.Message;
                        Log(opts, $"  {kind.Name}: error - {ex.Message}");
                    }
                    report["files"] = files;
                    resultReports.Add(report);
                }
            }

            var manifest = new Dictionary<string, object>
            {
                ["tool"] = "FEMDesignDumper",
                ["version"] = "0.1.0",
                ["timestampUtc"] = DateTime.UtcNow.ToString("o"),
                ["model"] = opts.ModelPath,
                ["outputDir"] = opts.OutputDir,
                ["calcMode"] = opts.Calc.ToString().ToLowerInvariant(),
                ["units"] = new Dictionary<string, string>
                {
                    ["length"] = units.Length.ToString(),
                    ["displacement"] = units.Displacement.ToString(),
                    ["force"] = units.Force.ToString(),
                    ["stress"] = units.Stress.ToString(),
                    ["angle"] = units.Angle.ToString(),
                    ["sectionalData"] = units.SectionalData.ToString(),
                    ["mass"] = units.Mass.ToString(),
                },
                ["modelSummary"] = modelSummary,
                ["results"] = resultReports,
            };
            string manifestPath = Path.Combine(opts.OutputDir, "manifest.json");
            ResultWriter.WriteJson(manifestPath, manifest);

            int okCount = resultReports.Count(r => (string)r["status"] == "ok");
            int totalRows = resultReports.Sum(r => System.Convert.ToInt32(r["rows"]));
            Console.WriteLine();
            Console.WriteLine($"Done. {okCount}/{resultReports.Count} result types with data, {totalRows} rows total.");
            Console.WriteLine($"Output: {opts.OutputDir}");
            Console.WriteLine($"Manifest: {manifestPath}");

            if (okCount == 0 && opts.Calc == CalcMode.None)
                Console.WriteLine("Note: no results found. The model may need analysis first - try --calc static.");

            return 0;
        }

        private static Dictionary<string, object> BuildModelSummary(FemDesignConnection connection)
        {
            var summary = new Dictionary<string, object>();
            try
            {
                Model model = connection.GetModel();
                var ents = model?.Entities;

                summary["bars"] = ents?.Bars?.Count ?? 0;
                summary["slabs"] = ents?.Slabs?.Count ?? 0;

                var loads = ents?.Loads;
                summary["loadCases"] = loads?.LoadCases?.Select(l => l.Name).ToList()
                                       ?? new List<string>();
                summary["loadCombinations"] = loads?.LoadCombinations?.Select(l => l.Name).ToList()
                                              ?? new List<string>();

                var sup = ents?.Supports;
                summary["pointSupports"] = sup?.PointSupport?.Count ?? 0;
                summary["lineSupports"] = sup?.LineSupport?.Count ?? 0;
                summary["surfaceSupports"] = sup?.SurfaceSupport?.Count ?? 0;
            }
            catch (Exception ex)
            {
                summary["error"] = ex.Message;
            }
            return summary;
        }

        private static List<ResultKind> SelectResultKinds(CliOptions opts, out string error)
        {
            error = null;

            if (opts.ResultTypes.Count == 0)
                return Registry.Where(k => k.InDefault).ToList();

            if (opts.ResultTypes.Count == 1 &&
                string.Equals(opts.ResultTypes[0], "all", StringComparison.OrdinalIgnoreCase))
                return Registry.ToList();

            var byName = Registry.ToDictionary(k => k.Name, StringComparer.OrdinalIgnoreCase);
            var chosen = new List<ResultKind>();
            var unknown = new List<string>();
            foreach (string name in opts.ResultTypes)
            {
                if (byName.TryGetValue(name, out ResultKind k))
                    chosen.Add(k);
                else
                    unknown.Add(name);
            }

            if (unknown.Count > 0)
            {
                error = "Unknown result type(s): " + string.Join(", ", unknown) +
                        ". Use --results list to see valid names.";
                return null;
            }
            return chosen;
        }

        private static void Log(CliOptions opts, string message)
        {
            if (!opts.Quiet)
                Console.Error.WriteLine(message);
        }

        /// <summary>
        /// A named result type paired with the generic GetResults call that fetches it.
        /// Order is preserved and used for the --results list output.
        /// </summary>
        private sealed class ResultKind
        {
            public string Name;
            public bool InDefault;
            public Func<FemDesignConnection, UnitResults, IReadOnlyList<object>> Fetch;

            public static ResultKind Of<T>(string name, bool inDefault) where T : IResult
            {
                return new ResultKind
                {
                    Name = name,
                    InDefault = inDefault,
                    Fetch = (c, u) => c.GetResults<T>(u).Cast<object>().ToList(),
                };
            }
        }

        private static readonly List<ResultKind> Registry = new List<ResultKind>
        {
            ResultKind.Of<NodalDisplacement>("NodalDisplacement", true),
            ResultKind.Of<PointSupportReaction>("PointSupportReaction", true),
            ResultKind.Of<LineSupportReaction>("LineSupportReaction", true),
            ResultKind.Of<LineSupportResultant>("LineSupportResultant", false),
            ResultKind.Of<SurfaceSupportReaction>("SurfaceSupportReaction", false),
            ResultKind.Of<BarInternalForce>("BarInternalForce", true),
            ResultKind.Of<BarEndForce>("BarEndForce", false),
            ResultKind.Of<BarDisplacement>("BarDisplacement", true),
            ResultKind.Of<BarStress>("BarStress", false),
            ResultKind.Of<ShellInternalForce>("ShellInternalForce", true),
            ResultKind.Of<ShellDisplacement>("ShellDisplacement", true),
            ResultKind.Of<ShellStress>("ShellStress", false),
            ResultKind.Of<ShellDerivedForce>("ShellDerivedForce", false),
            ResultKind.Of<RCShellReinforcementRequired>("RCShellReinforcementRequired", false),
            ResultKind.Of<BarSteelUtilization>("BarSteelUtilization", false),
            ResultKind.Of<BarTimberUtilization>("BarTimberUtilization", false),
            ResultKind.Of<EigenFrequencies>("EigenFrequencies", false),
            ResultKind.Of<FemNode>("FemNode", false),
            ResultKind.Of<FemShell>("FemShell", false),
        };
    }
}
