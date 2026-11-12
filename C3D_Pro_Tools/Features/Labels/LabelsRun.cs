using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using C3DProTools.Features.Sheets.Application;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(C3DProTools.Features.Labels.LabelsRun))]

// C3DPROLBL: the label steps run from the command line with no dialog, on the layouts you name, using the "labels" section
// of C3DPro.project.json beside the drawing (and the choices saved in Annotation Studio for what the file does not set).
//   Report   checks the labels shown on the layouts: overlaps, outside the viewport, unmeasured. Changes and draws nothing
//   Arrange  places the labels of the chosen networks clear of each other and inside their viewports (locked labels stay)
//   Reset    puts the labels of the chosen networks back where their style places them (locked labels stay)
// Each writing step is one transaction (one UNDO). Viewport scales and twists are read, never changed.
namespace C3DProTools.Features.Labels
{
    internal sealed class LabelsSettings
    {
        /// <summary>Layouts (sheets) by name; wildcards, comma separated, ! to leave out.</summary>
        public string Layouts { get; set; } = "*";
        /// <summary>Networks whose labels may move; the others stay and count as obstacles.</summary>
        public string Networks { get; set; } = "*";
        public bool Plan { get; set; } = true;
        public bool Profile { get; set; } = false;
        /// <summary>Paper mm; 0 = as saved in Annotation Studio.</summary>
        public double MaxDistance { get; set; } = 0;
        public double Gap { get; set; } = 0;
        public double Margin { get; set; } = 0;
        /// <summary>True: every unlocked label is placed again; false: only labels with a problem move.</summary>
        public bool RearrangeAll { get; set; } = true;
        /// <summary>Label styles ("Kind:Style") always dragged; null = as saved in Annotation Studio.</summary>
        public List<string> DraggedStyles { get; set; }
        public List<string> SlideStyles { get; set; }
        public List<string> KeepClearNetworks { get; set; }
        /// <summary>When set, each viewport's arrangement is written there as a case file (to replay it in the unit tests).</summary>
        public string ExportFolder { get; set; } = "";

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public static LabelsSettings Load(string drawingPath, out string problem, out string source)
        {
            problem = null;
            string file = ProjectSettings.PathFor(drawingPath);
            source = "defaults (no " + ProjectSettings.FileName + " beside the drawing)";
            if (!File.Exists(file)) return new LabelsSettings();
            try
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
                foreach (JsonProperty p in d.RootElement.EnumerateObject())
                    if (string.Equals(p.Name, "labels", StringComparison.OrdinalIgnoreCase))
                    {
                        source = file;
                        return p.Value.Deserialize<LabelsSettings>(Json) ?? new LabelsSettings();
                    }
                source = $"defaults: {file} has no \"labels\" section";
                return new LabelsSettings();
            }
            catch (System.Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                problem = $"{ProjectSettings.FileName} could not be read: {ex.Message}";
                return new LabelsSettings();
            }
        }

        /// <summary>Comma-separated wildcards, a leading ! leaves a name out.</summary>
        public static bool Matches(string patterns, string name)
        {
            string[] parts = (patterns ?? "*").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool Match(string p) => Regex.IsMatch(name ?? "", "^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
            if (parts.Where(p => p.StartsWith("!")).Any(p => Match(p.Substring(1)))) return false;
            string[] include = parts.Where(p => !p.StartsWith("!")).ToArray();
            return include.Length == 0 || include.Any(Match);
        }

        public ArrangeOptions Options(AnnotationSettings saved) => new ArrangeOptions
        {
            MaxDistance = MaxDistance > 0 ? MaxDistance : saved.MaxDistance,
            Gap = Gap > 0 ? Gap : saved.Gap,
            Margin = Margin > 0 ? Margin : saved.Margin,
            RearrangeAll = RearrangeAll,
            SlideOnPipe = saved.SlideNormalLabels,
            AvoidModelText = saved.AvoidModelText,
            Plan = Plan,
            Profile = Profile,
            DraggedStyles = new HashSet<string>(DraggedStyles ?? saved.DraggedStyles ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
            SlideStyles = new HashSet<string>(SlideStyles ?? saved.SlideStyles ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
            KeepClearNetworks = new HashSet<string>(KeepClearNetworks ?? saved.KeepClearNetworks ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
            ExportFolder = string.IsNullOrWhiteSpace(ExportFolder) ? null : ExportFolder,
        };
    }

    public sealed class LabelsRun
    {
        [CommandMethod("C3DPRO", "C3DPROLBL", CommandFlags.Modal)]
        public void Run()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            var kw = new PromptKeywordOptions("\nLabels") { AllowNone = true };
            foreach (string k in new[] { "Report", "Arrange", "Reset" }) kw.Keywords.Add(k);
            kw.Keywords.Default = "Report";
            PromptResult kr = ed.GetKeywords(kw);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            string mode = string.IsNullOrEmpty(kr.StringResult) ? "Report" : kr.StringResult;

            LabelsSettings s = LabelsSettings.Load(doc.Name, out string problem, out string source);
            if (problem != null) { ed.WriteMessage($"\n{problem}\n"); return; }
            PromptResult lr = ed.GetString(new PromptStringOptions($"\nLayouts (wildcards, comma separated) <{s.Layouts}>: ") { AllowSpaces = true });
            if (lr.Status != PromptStatus.OK) return;
            if (!string.IsNullOrWhiteSpace(lr.StringResult)) s.Layouts = lr.StringResult.Trim();
            PromptResult nr = ed.GetString(new PromptStringOptions($"\nNetworks whose labels may move <{s.Networks}>: ") { AllowSpaces = true });
            if (nr.Status != PromptStatus.OK) return;
            if (!string.IsNullOrWhiteSpace(nr.StringResult)) s.Networks = nr.StringResult.Trim();

            var log = new StringBuilder();
            log.AppendLine($"C3DPROLBL {mode} on {Path.GetFileName(doc.Name)}, layouts \"{s.Layouts}\", networks \"{s.Networks}\", settings from {source}");
            try
            {
                Run(doc, s, mode, log);
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error("C3DPROLBL failed", ex);
                log.AppendLine($"C3DPROLBL failed: {ex.Message}");
            }

            string file = Path.Combine(Path.GetTempPath(), $"C3DPROLBL-{mode}.txt");
            try { File.WriteAllText(file, log.ToString()); } catch (IOException) { file = null; }
            string[] summary = log.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0 && l[0] != ' ').ToArray();
            ed.WriteMessage("\n" + string.Join("\n", summary) + (file != null ? $"\nFull report: {file}" : "") + "\n");
            if (mode != "Report") ViewHelper.Regen(doc);
        }

        private static void Run(Document doc, LabelsSettings s, string mode, StringBuilder log)
        {
            bool write = mode != "Report";
            Database db = doc.Database;
            CivilDocument civil = CivilDocument.GetCivilDocument(db);
            using (write ? doc.LockDocument() : null)
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                List<SheetInfo> all = AnnotSheets.Read(tr, db);
                List<SheetInfo> sheets = all.Where(x => LabelsSettings.Matches(s.Layouts, x.Layout)).ToList();
                if (sheets.Count == 0)
                {
                    log.AppendLine($"No layout matches \"{s.Layouts}\" ({all.Count} layouts with viewports: {string.Join(", ", all.Take(12).Select(x => x.Layout))}).");
                    tr.Abort();
                    return;
                }

                List<LabelInfo> labels = AnnotLabels.Scan(tr, db);
                HashSet<ObjectId> movable = Movable(tr, labels, s.Networks);
                double ppu = AnnotLabels.PaperPerUnit(db);

                if (mode == "Reset")
                {
                    List<LabelInfo> shown = AnnotLabels.OnSheets(labels, sheets, s.Plan, s.Profile).Where(l => movable.Contains(l.Id)).ToList();
                    (int reset, int locked, int failed) = AnnotLabels.ResetLocations(tr, shown, false);
                    tr.Commit();
                    log.AppendLine($"Reset: {shown.Count} labels on {sheets.Count} layouts: reset {reset}, locked left alone {locked}, failed {failed}.");
                    return;
                }

                AnnotationSettings saved = AnnotationSettings.Load();
                ArrangeOptions o = s.Options(saved);
                o.Only = movable;
                AnnotScene scene = AnnotScene.Read(tr, db, civil, o.AvoidModelText, o.Profile);
                var total = new ArrangeReport();
                // A check measures each layout on its own; arranging shares what is done, so a label placed on one layout is not moved again.
                var handled = new HashSet<ObjectId>();
                foreach (SheetInfo sheet in sheets)
                {
                    ArrangeReport r = AnnotArranger.Arrange(tr, sheet, labels, scene, o, write ? handled : new HashSet<ObjectId>(), !write, ppu);
                    total.Add(r);
                    log.AppendLine($"  {sheet.Layout}: {sheet.Viewports.Count} viewports, {r.Checked} labels, " +
                                   (write ? $"moved {r.Moved}, without a clear place {r.Unresolved}" : $"with a problem {r.Problems}") +
                                   (r.Unmeasured > 0 ? $", could not be measured {r.Unmeasured}" : ""));
                    foreach (ArrangeIssue i in r.Issues) log.AppendLine($"    {Describe(tr, labels, i.LabelId)}: {i.Text}");
                }
                if (write) tr.Commit(); else tr.Abort();
                log.AppendLine(write
                    ? $"Arrange: {total.Checked} labels on {sheets.Count} layouts: moved {total.Moved}, without a clear place {total.Unresolved} (listed), could not be measured {total.Unmeasured}."
                    : $"Labels: {total.Checked} labels on {sheets.Count} layouts: {total.Problems} with a problem (overlap or outside the viewport), {total.Unmeasured} could not be measured. Changes nothing.");
            }
        }

        /// <summary>"label 2A3F (KSIA_START [NE]) on IC-124-IC3A-06" for the report.</summary>
        private static string Describe(Transaction tr, List<LabelInfo> labels, ObjectId id)
        {
            LabelInfo l = labels.FirstOrDefault(x => x.Id == id);
            if (l == null) return $"label {id.Handle}";
            string part = "";
            try { if (!l.PartId.IsNull && tr.GetObject(l.PartId, OpenMode.ForRead) is Part p) part = " on " + p.Name; }
            catch (System.Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return $"label {id.Handle} ({l.Style}){part}";
        }

        /// <summary>The labels of parts in the matched networks: the ones that may move.</summary>
        private static HashSet<ObjectId> Movable(Transaction tr, List<LabelInfo> labels, string networks)
        {
            var nameOf = new Dictionary<ObjectId, string>();
            var result = new HashSet<ObjectId>();
            foreach (LabelInfo l in labels)
            {
                if (l.PartId.IsNull) continue;
                if (!nameOf.TryGetValue(l.PartId, out string name))
                {
                    try { name = tr.GetObject(l.PartId, OpenMode.ForRead) is Part p && tr.GetObject(p.NetworkId, OpenMode.ForRead) is Network n ? n.Name : ""; }
                    catch (System.Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { name = ""; }
                    nameOf[l.PartId] = name;
                }
                if (name.Length > 0 && LabelsSettings.Matches(networks, name)) result.Add(l.Id);
            }
            return result;
        }
    }
}
