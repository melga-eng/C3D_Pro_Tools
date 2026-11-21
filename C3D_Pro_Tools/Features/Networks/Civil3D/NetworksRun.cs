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
using Surface = Autodesk.Civil.DatabaseServices.Surface;

#if !PUBLIC   // full edition only (see Edition.cs)
[assembly: CommandClass(typeof(C3DProTools.Features.Networks.Civil3D.NetworksRun))]
#endif

// C3DPRONET: the network steps run from the command line with no dialog, using the "networks" section of C3DPro.project.json
// beside the drawing. Same pattern as C3DPROPV: a preset run for the user and a headless drawing test.
//   Report  reads only: what Rims, Fit and Repair would change, per network
//   Rims    sets structure rims to the surface (plus aboveSurface)
//   Fit     trims or extends straight pipe ends to their structures' chosen face, connecting loose ends inside a structure
//   Repair  rebuilds pipes that dropped out of their network's member list
//   Duplicates deletes true copies inside each network (same place, levels, family and size); copies with labels or profile views stay
//   Cover   checks cover, slope and crossing clearances along every route (reads only)
//   All     Repair, Duplicates, Rims, Fit, then the Cover check
// Each writing step is one transaction (one UNDO); data-shortcut objects are never changed.
namespace C3DProTools.Features.Networks.Civil3D
{
    internal sealed class NetworksSettings
    {
        /// <summary>Network names to include; wildcards * and ?, several separated by commas.</summary>
        public string Networks { get; set; } = "*";
        /// <summary>Surface for rims; empty: Rims is skipped.</summary>
        public string Surface { get; set; } = "";
        public double AboveSurface { get; set; } = 0;
        /// <summary>True: rims that already match are also linked to the surface (automatic surface adjustment off stays off).</summary>
        public bool FollowSurface { get; set; } = false;
        /// <summary>InnerWall, OuterWall or Center.</summary>
        public string FitFace { get; set; } = "InnerWall";
        public bool ConnectLooseEnds { get; set; } = true;
        /// <summary>Both above 0: fit to a box of this length x width (m) at each structure instead of its own size.</summary>
        public double FitBoxLength { get; set; } = 0;
        public double FitBoxWidth { get; set; } = 0;
        /// <summary>Ends that move less than this (m) are left alone.</summary>
        public double FitMinMove { get; set; } = 0.001;
        /// <summary>An end that would move further than this (m) is not fitted: its structure connection is wrong.</summary>
        public double FitMaxMove { get; set; } = NetworkEditOps.MaxFitMove;
        /// <summary>A rim that would change by more than this (m) is left alone and listed (equipment, wrong surface).</summary>
        public double RimMaxChange { get; set; } = 1.0;
        /// <summary>Cover check (reads only): surface (empty: <see cref="Surface"/>), limits to the top of the encasement (m), slope (%), crossing clearance (m).</summary>
        public string CoverSurface { get; set; } = "";
        public double CoverMin { get; set; } = 0.60;
        public double CoverMax { get; set; } = 1.50;
        public double CoverMaxSlope { get; set; } = 7.0;
        public double CoverClearance { get; set; } = 0.30;
        public double CoverSpacing { get; set; } = 1.0;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        };

        public static NetworksSettings Load(string drawingPath, out string problem, out string source)
        {
            problem = null;
            string text = Features.Shared.Contracts.SettingsSource.Text(drawingPath, out string file);
            source = file;
            try
            {
                using JsonDocument d = JsonDocument.Parse(text);
                foreach (JsonProperty p in d.RootElement.EnumerateObject())
                    if (string.Equals(p.Name, "networks", StringComparison.OrdinalIgnoreCase))
                    {
                        source = file;
                        return p.Value.Deserialize<NetworksSettings>(Json) ?? new NetworksSettings();
                    }
                source = $"defaults: {file} has no \"networks\" section";
                return new NetworksSettings();
            }
            catch (System.Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                problem = $"The C3D Pro settings could not be read: {ex.Message}";
                return new NetworksSettings();
            }
        }

        // Patterns with a leading ! exclude: "ICT-*,!*-EQUIP" is every ICT network except the equipment ones.
        public bool Includes(string network) => NamePatterns.Matches(Networks, network);
    }

    public sealed class NetworksRun
    {
        [CommandMethod("C3DPRO", "C3DPRONET", CommandFlags.Modal)]
        public void Run()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            var kw = new PromptKeywordOptions("\nNetworks") { AllowNone = true };
            // Each option its own short form: R is Report, I Rims, P Repair (Report, Rims and Repair all start with R).
            foreach ((string k, string shown) in new[] { ("Report", "Report"), ("Rims", "rIms"), ("Fit", "Fit"), ("Repair", "rePair"), ("Duplicates", "Duplicates"), ("Cover", "Cover"), ("All", "All") })
                kw.Keywords.Add(k, shown, shown);
            kw.Keywords.Default = "Report";
            PromptResult kr = ed.GetKeywords(kw);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            string mode = string.IsNullOrEmpty(kr.StringResult) ? "Report" : kr.StringResult;

            NetworksSettings s = NetworksSettings.Load(doc.Name, out string problem, out string source);
            if (problem != null) { ed.WriteMessage($"\n{problem}\n"); return; }
            PromptResult nr = ed.GetString(new PromptStringOptions($"\nNetworks (wildcards, comma separated) <{s.Networks}>: ") { AllowSpaces = true });
            if (nr.Status != PromptStatus.OK) return;
            if (!string.IsNullOrWhiteSpace(nr.StringResult)) s.Networks = nr.StringResult.Trim();

            string nameNote, typed = s.Networks;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                var names = new List<string>();
                foreach (ObjectId id in CivilDocument.GetCivilDocument(doc.Database).GetPipeNetworkIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Network net) names.Add(net.Name);
                s.Networks = NamePatterns.Widen(s.Networks, names, "network", out nameNote);
                tr.Commit();
            }
            if (nameNote != null && !PresetPrompt.Continue(ed, nameNote, mode != "Report" && mode != "Cover")) return;
            ed.WriteMessage("\n" + RunReport.Write(Execute(doc, s, mode, source, typed, nameNote), "C3DPRONET", mode) + "\n");
        }

        /// <summary>Runs one step (or Report / All) on the networks the settings name; returns the report.</summary>
        internal static string Execute(Document doc, NetworksSettings s, string mode, string source, string typed = null, string nameNote = null)
        {
            var log = new StringBuilder();
            log.AppendLine($"C3DPRONET {mode} on {Path.GetFileName(doc.Name)}, networks \"{typed ?? s.Networks}\", settings from {source}");
            if (nameNote != null) log.AppendLine(nameNote);
            try
            {
                if (ConduitCivil3DAccess.Metres(doc.Database.Insunits) != 1.0)
                    throw new InvalidOperationException($"the drawing units are {doc.Database.Insunits}; C3DPRONET works in metre drawings only. Nothing was changed.");
                bool all = mode == "All";
                // Report and All run every step (Report as a preview); a single step runs only itself.
                bool Runs(string step) => mode == "Report" || all || mode == step;
                if (Runs("Repair")) Repair(doc, s, all || mode == "Repair", log);
                if (Runs("Duplicates")) Duplicates(doc, s, all || mode == "Duplicates", log);
                if (Runs("Rims")) Rims(doc, s, all || mode == "Rims", log);
                if (Runs("Fit")) Fit(doc, s, all || mode == "Fit", log);
                if (Runs("Cover")) Cover(doc, s, log);
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error("C3DPRONET failed", ex);
                log.AppendLine($"C3DPRONET failed: {ex.Message}");
            }
            return log.ToString();
        }

        /// <summary>The matched networks that are not data references, with their gravity pipes.</summary>
        private static List<(ObjectId id, string name, List<ObjectId> pipes)> Networks(Transaction tr, Database db, NetworksSettings s, out int references)
        {
            references = 0;
            var list = new List<(ObjectId, string, List<ObjectId>)>();
            foreach (ObjectId nid in CivilDocument.GetCivilDocument(db).GetPipeNetworkIds())
            {
                if (tr.GetObject(nid, OpenMode.ForRead) is not Network net || !s.Includes(net.Name)) continue;
                bool reference = false;
                try { reference = net.IsReferenceObject; } catch (System.Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                if (reference) { references++; continue; }
                list.Add((nid, net.Name, net.GetPipeIds().Live().ToList()));
            }
            return list;
        }

        private static void Repair(Document doc, NetworksSettings s, bool write, StringBuilder log)
        {
            Database db = doc.Database;
            using (write ? doc.LockDocument() : null)
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var names = new HashSet<ObjectId>(Networks(tr, db, s, out _).Select(n => n.id));
                List<OrphanPipe> orphans = NetworkRepair.Find(tr, db).Where(o => names.Contains(o.NetworkId)).ToList();
                log.AppendLine($"Repair: {orphans.Count} pipes not in their network's member list" +
                               (orphans.Count > 0 ? $" ({string.Join(", ", orphans.Take(8).Select(o => $"{o.Name} in {o.Network}"))})" : "") + ".");
                // A rebuilt pipe is a new object: labels on the old one would be erased with it. Labelled orphans are left alone.
                var labelled = new HashSet<ObjectId>(AnnotLabels.Scan(tr, db).Where(l => !l.PartId.IsNull).Select(l => l.PartId));
                List<OrphanPipe> skipped = orphans.Where(o => labelled.Contains(o.Id)).ToList();
                if (skipped.Count > 0)
                    log.AppendLine($"Repair: {skipped.Count} of them carry labels and are left alone (rebuilding would erase the labels): " +
                                   string.Join(", ", skipped.Take(10).Select(o => o.Name)) + ".");
                orphans = orphans.Except(skipped).ToList();
                if (!write || orphans.Count == 0) { tr.Abort(); return; }
                var notes = new List<string>();
                int repaired = NetworkRepair.Repair(tr, orphans, notes);
                tr.Commit();
                log.AppendLine($"Repair: rebuilt {repaired} of {orphans.Count}.");
                foreach (string n in notes.Take(15)) log.AppendLine($"  {n}");
            }
        }

        private static void Duplicates(Document doc, NetworksSettings s, bool write, StringBuilder log)
        {
            Database db = doc.Database;
            CivilDocument civil = CivilDocument.GetCivilDocument(db);
            using (write ? doc.LockDocument() : null)
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var nets = Networks(tr, db, s, out _);
                Dictionary<ObjectId, int> labels = NetworkOrganizer.LabelCounts(tr, db);
                int copies = 0, used = 0, deleted = 0, reconnected = 0, failed = 0;
                var lines = new List<string>();
                foreach (var n in nets)
                {
                    var plan = NetworkOrganizer.Plan(tr, db, new[] { n.id }, n.id, labels);
                    var idOf = plan.ToDictionary(p => p.row.Part, p => p.id);
                    var rows = plan.Where(p => p.row.Action == MergeAction.DeleteCopy && p.row.Keeper != null).ToList();
                    if (rows.Count == 0) continue;
                    // A copy that is labelled or drawn in a profile view is left alone: deleting it would take those with it.
                    var inUse = rows.Where(p => p.row.Part.Uses > 0).ToList();
                    var delete = rows.Except(inUse).ToDictionary(p => p.id, p => idOf[p.row.Keeper]);
                    copies += rows.Count; used += inUse.Count;
                    lines.Add($"  {n.name}: {rows.Count} copies, {delete.Count} {(write ? "deleted" : "to delete")}" +
                              (inUse.Count > 0 ? $", {inUse.Count} labelled or in a profile view left alone ({string.Join(", ", inUse.Take(8).Select(p => p.row.Part.Name))})" : ""));
                    foreach (var p in rows.Except(inUse)) lines.Add($"    {(p.row.Part.IsPipe ? "pipe" : "structure")} {p.row.Part.Name}: copy of {p.row.Keeper.Name}");
                    if (!write || delete.Count == 0) continue;
                    using Transaction one = tr.TransactionManager.StartTransaction();
                    try
                    {
                        MergeResult r = NetworkOrganizer.Merge(one, civil, new List<ObjectId> { n.id }, n.id, null, new List<ObjectId>(), delete, false, false, doc.Editor);
                        // A pipe left disconnected from the structure that stays: the whole network's step is undone.
                        if (r.ReconnectFailed > 0)
                        {
                            one.Abort();
                            failed++;
                            lines.Add($"    {n.name}: nothing deleted: {r.ReconnectFailed} pipes could not be moved onto the structure that stays.");
                            foreach (string l in r.Log) lines.Add($"    {l}");
                            continue;
                        }
                        one.Commit();
                        deleted += r.Deleted; reconnected += r.Reconnected;
                        foreach (string l in r.Log) lines.Add($"    {l}");
                    }
                    catch (System.Exception ex)
                    {
                        one.Abort();
                        failed++;
                        lines.Add($"    {n.name}: nothing deleted ({ex.Message}).");
                    }
                }
                log.AppendLine($"Duplicates: {copies} copies in {nets.Count} networks" +
                               (used > 0 ? $"; {used} labelled or in a profile view are left alone" : "") +
                               (write ? $"; deleted {deleted}, pipes moved to the structure that stays {reconnected}, networks failed {failed}." : "."));
                foreach (string l in lines) log.AppendLine(l);
                if (write) tr.Commit(); else tr.Abort();
            }
        }

        /// <summary>Checks the current cover, slopes and crossing clearances of every route of the matched networks. Changes nothing.</summary>
        private static void Cover(Document doc, NetworksSettings s, StringBuilder log)
        {
            string surfaceName = string.IsNullOrWhiteSpace(s.CoverSurface) ? s.Surface : s.CoverSurface;
            if (string.IsNullOrWhiteSpace(surfaceName)) { log.AppendLine("Cover: no surface set (networks.coverSurface or networks.surface); skipped."); return; }
            Database db = doc.Database;
            CivilDocument civil = CivilDocument.GetCivilDocument(db);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Surface surface = null;
                foreach (ObjectId id in civil.GetSurfaceIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Surface sf && string.Equals(sf.Name, surfaceName.Trim(), StringComparison.OrdinalIgnoreCase)) surface = sf;
                if (surface == null) { log.AppendLine($"Cover: the surface \"{surfaceName}\" is not in the drawing; skipped."); tr.Abort(); return; }

                // The project limits from the settings file; the design rules (zones, clearance matrix, bends) as saved in the Cover Designer.
                CoverOptimizerSettings saved = CoverOptimizerSettings.Load();
                var o = new CoverOptions
                {
                    Mode = CoverMode.Dry, Reference = CoverReference.OutsideTop,
                    MinCover = s.CoverMin, MaxCover = s.CoverMax, MinLength = saved.MinLength, MaxLength = saved.MaxLength,
                    MinSlope = saved.DryMinSlope / 100.0, MaxSlope = s.CoverMaxSlope / 100.0, MaxSlopeChange = saved.MaxSlopeChange / 100.0,
                    MaxDrop = saved.MaxDrop, StartFixed = true, CrossingClearance = s.CoverClearance, Rules = saved.Rules ?? new CoverRules(),
                };
                var setup = new CoverJobs.Setup { Spacing = s.CoverSpacing > 0 ? s.CoverSpacing : 1.0, Gap = saved.GapLimit > 0 ? saved.GapLimit : 1.0 };
                setup.ZonePolygons = CoverJobs.ReadZonePolygons(tr, db, o.Rules);

                // Pavement from surfaces (the project's pavement section): its cover and duct type by stretch; unpaved has its own cover.
                Pavement.PavementSettings pav = Pavement.PavementSettings.Load(doc.Name, out string pavProblem);
                if (pavProblem != null) log.AppendLine($"Cover: {pavProblem}");
                List<Func<double, double, bool>> pavTests = null;
                if (pav.Any)
                {
                    var missing = new List<string>();
                    pavTests = Pavement.PavementCivil3D.Tests(tr, civil, pav, missing, edgeBands: pav.CoverUsesEdgeBand);
                    if (missing.Count > 0) log.AppendLine($"Cover: pavement surfaces not in the drawing, left out: {string.Join(", ", missing)}.");
                    if (pav.Unpaved.MinCover > 0) o.MinCover = pav.Unpaved.MinCover;
                }
                var sizeOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int wrongType = 0;

                var nets = Networks(tr, db, s, out _);
                int routes = 0, pipes = 0, errors = 0;
                var failing = new List<string>();
                foreach (var n in nets)
                {
                    if (n.pipes.Count == 0) continue;
                    List<CoverJob> jobs;
                    try { jobs = CoverJobs.FromNetwork(tr, n.id, setup.Gap, setup.CenterTolerance, false); }
                    catch (System.Exception ex) { errors++; failing.Add($"  {n.name}: routes could not be read ({ex.Message})"); continue; }
                    // Only each route's own pipes are left out of its crossings (Prepare adds them): other routes of the same network count.
                    var batch = new HashSet<ObjectId>();
                    foreach (CoverJob job in jobs)
                    {
                        routes++;
                        try { CoverJobs.Prepare(tr, civil, job, surface, o, setup, batch); }
                        catch (System.Exception ex) { errors++; failing.Add($"  {n.name} {job.Name}: {ex.Message}"); continue; }
                        if (job.Error != null) { errors++; failing.Add($"  {n.name} {job.Name}: {job.Error}"); continue; }
                        if (job.Current == null) continue;
                        if (pavTests != null) wrongType += ApplyPavement(tr, job, pav, pavTests, sizeOf);
                        pipes += job.Current.Count;
                        foreach (CoverRow r in job.Current.Where(r => r.Failed))
                            failing.Add($"  {n.name} {r.Pipe.Name}: cover {r.MinCover:0.00}–{r.MaxCover:0.00} m; {r.Status}");
                    }
                }
                tr.Abort();
                string limits = pav.Any
                    ? string.Join(", ", pav.Zones.Select(z => $"{z.Name} {z.MinCover:0.00} m{(z.DuctType.Length > 0 ? " type " + z.DuctType : "")}{(z.EdgeBand > 0 ? $" ({z.EdgeBand:0.##} m edge band excepted)" : "")}")) +
                      $", {pav.Unpaved.Name} {o.MinCover:0.00} m{(pav.Unpaved.DuctType.Length > 0 ? " type " + pav.Unpaved.DuctType : "")}; max {s.CoverMax:0.00} m"
                    : $"{s.CoverMin:0.00}–{s.CoverMax:0.00} m";
                log.AppendLine($"Cover check ({limits} to the top of the encasement on {surface.Name}, slope up to {s.CoverMaxSlope:0.#} %, " +
                               $"crossings {s.CoverClearance:0.00} m clear): {routes} routes, {pipes} pipes, {failing.Count - errors} pipes fail" +
                               (pavTests != null ? $" ({wrongType} with the wrong duct type for their pavement)" : "") +
                               (errors > 0 ? $", {errors} routes could not be checked" : "") + ". Changes nothing.");
                // What fails most, numbers taken out so the same rule groups together.
                var reasons = failing.Select(f => f.IndexOf(" m; ", StringComparison.Ordinal) is int at && at > 0 ? f.Substring(at + 4) : null).Where(r => r != null)
                    .SelectMany(r => r.Split("; ")).Select(r => Regex.Replace(r, @"-?\d+(\.\d+)?", "#"))
                    .GroupBy(r => r).OrderByDescending(g => g.Count()).Take(12);
                foreach (var g in reasons) log.AppendLine($"  {g.Count(),5} x {g.Key}");
                foreach (string f in failing) log.AppendLine(f);
            }
        }

        /// <summary>
        /// Gives a checked route its pavement stretches (each with its own minimum cover), checks it again, and fails each duct
        /// whose type (A or B, from its name or size) is not the one its pavement asks for. Returns how many ducts have the wrong type.
        /// </summary>
        private static int ApplyPavement(Transaction tr, CoverJob job, Pavement.PavementSettings pav, List<Func<double, double, bool>> tests, Dictionary<string, string> sizeOf)
        {
            CoverRoute route = job.Route;
            var stations = route.Records.Select(r => r.S).ToList();
            var kinds = route.Records.Select(r => PavementZones.KindAt(tests, r.Plan.X, r.Plan.Y)).ToList();
            // The kind anywhere along the route: the plan position between its two checked stations, by straight interpolation.
            int KindAtStation(double st)
            {
                var recs = route.Records;
                int hi = recs.FindIndex(r => r.S >= st);
                if (hi <= 0) return kinds[Math.Max(0, hi)];
                RouteRecord a = recs[hi - 1], b = recs[hi];
                double t = b.S - a.S > 1e-9 ? (st - a.S) / (b.S - a.S) : 0;
                return PavementZones.KindAt(tests, a.Plan.X + (b.Plan.X - a.Plan.X) * t, a.Plan.Y + (b.Plan.Y - a.Plan.Y) * t);
            }
            foreach (PavementRun run in PavementZones.PavedStretches(stations, kinds, KindAtStation))
                route.Zones.Add(new CoverZone
                {
                    S0 = run.S0, S1 = run.S1, MinCover = pav.Of(run.Kind).MinCover, Label = pav.Of(run.Kind).Name,
                    MaxCover = PavementZones.ZoneMax(pav.Of(run.Kind), job.Options.MaxCover),
                });
            job.Current = CoverEvaluation.Evaluate(route, CoverJobs.CurrentPipes(route), job.Options);

            int wrong = 0;
            foreach (CoverRow row in job.Current)
            {
                string name = row.Pipe.Name ?? "";
                if (!sizeOf.TryGetValue(name, out string size))
                {
                    size = "";
                    foreach ((ObjectId id, bool _) in job.Pipes)
                        if (tr.GetObject(id, OpenMode.ForRead) is Pipe p && p.Name == name) { try { size = p.PartSizeName ?? ""; } catch (System.Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { } break; }
                    sizeOf[name] = size;
                }
                string type = PavementZones.DuctTypeOf(name, size);
                // Only the A / B families are checked (a 1C micro duct has no such rule).
                if (type != "A" && type != "B") continue;
                PavementZone strictest = PavementZones.KindsBetween(stations, kinds, row.Pipe.S0, row.Pipe.S1)
                    .Select(k => pav.Of(k)).OrderByDescending(z => z.MinCover).FirstOrDefault();
                var check = PavementZones.CheckType(type, strictest);
                if (check == null) continue;
                if (check.Value.fails)
                {
                    // "OK" (or "OK; <notes>") becomes the issue (with the notes); other problems follow it.
                    row.Status = row.Status.StartsWith("OK", StringComparison.Ordinal) ? check.Value.text + row.Status.Substring(2) : check.Value.text + "; " + row.Status;
                    row.Failed = true;
                    wrong++;
                }
                else row.Status += "; " + check.Value.text;
            }
            return wrong;
        }

        private static void Rims(Document doc, NetworksSettings s, bool write, StringBuilder log)
        {
            if (string.IsNullOrWhiteSpace(s.Surface))
            {
                log.AppendLine("Rims: no surface set (networks.surface); skipped.");
                return;
            }
            Database db = doc.Database;
            using (write ? doc.LockDocument() : null)
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId surfaceId = ObjectId.Null;
                foreach (ObjectId id in CivilDocument.GetCivilDocument(db).GetSurfaceIds())
                    if (tr.GetObject(id, OpenMode.ForRead) is Surface sf && string.Equals(sf.Name, s.Surface.Trim(), StringComparison.OrdinalIgnoreCase)) surfaceId = id;
                if (surfaceId.IsNull) { log.AppendLine($"Rims: the surface \"{s.Surface}\" is not in the drawing; skipped."); tr.Abort(); return; }

                var nets = Networks(tr, db, s, out int refs);
                List<ObjectId> pipes = nets.SelectMany(n => n.pipes).ToList();
                List<ObjectId> structures = new List<ObjectId>();
                foreach (var n in nets) structures.AddRange(((Network)tr.GetObject(n.id, OpenMode.ForRead)).GetStructureIds().Live());
                List<RimToSurface.Row> plan = RimToSurface.Plan(tr, pipes, structures, (Surface)tr.GetObject(surfaceId, OpenMode.ForRead), s.AboveSurface);
                List<RimToSurface.Row> chosen = plan.Where(r => !r.Reference && (r.Check == RimCheck.Adjust || (r.Check == RimCheck.AlreadyMatches && s.FollowSurface))).ToList();
                List<RimToSurface.Row> tooFar = chosen.Where(r => s.RimMaxChange > 0 && Math.Abs(r.NewRim - r.Rim) > s.RimMaxChange).ToList();
                chosen = chosen.Except(tooFar).ToList();
                if (tooFar.Count > 0)
                    log.AppendLine($"Rims: {tooFar.Count} structures would change by more than {s.RimMaxChange} m and are left alone (equipment or a wrong surface?): " +
                                   string.Join(", ", tooFar.Take(12).Select(r => $"{r.Name} {r.NewRim - r.Rim:+0.00;-0.00}")) + ".");
                log.AppendLine($"Rims: {plan.Count} structures in {nets.Count} networks: {chosen.Count} {(write ? "set" : "to set")} to {s.Surface}" +
                               (s.AboveSurface != 0 ? $" {s.AboveSurface:+0.###;-0.###} m" : "") + "; " +
                               string.Join(", ", plan.GroupBy(r => r.Check).Where(g => g.Key != RimCheck.Adjust).Select(g => $"{g.Count()} {Words(g.Key)}")) +
                               (refs > 0 ? $"; {refs} reference networks left out" : "") + ".");
                foreach (RimToSurface.Row r in plan.Where(r => r.Check == RimCheck.BelowSump || r.Check == RimCheck.BelowPipe))
                    log.AppendLine($"  {r.Name} ({r.Network}): {Words(r.Check)}; rim {r.Rim:F3}, ground {r.Ground:F3}, sump {r.Sump:F3}");
                foreach (RimToSurface.Row r in tooFar)
                    log.AppendLine($"  {r.Name} ({r.Network}): left alone, rim {r.Rim:F3} -> {r.NewRim:F3} ({r.NewRim - r.Rim:+0.000;-0.000})");
                foreach (RimToSurface.Row r in chosen.OrderByDescending(r => Math.Abs(r.NewRim - r.Rim)))
                    log.AppendLine($"  {r.Name} ({r.Network}): rim {r.Rim:F3} -> {r.NewRim:F3} ({r.NewRim - r.Rim:+0.000;-0.000})");
                if (!write || chosen.Count == 0) { tr.Abort(); return; }
                RimToSurface.Outcome o = RimToSurface.Apply(tr, chosen, surfaceId, s.AboveSurface, s.FollowSurface, doc.Editor);
                tr.Commit();
                log.AppendLine($"Rims: set {o.Counts.Done}, failed {o.Counts.Failed}, not at target {o.NotAtTarget}, sump control changed {o.SumpControlChanged}.");
            }
        }

        private static string Words(RimCheck c) => c switch
        {
            RimCheck.AlreadyMatches => "already on the surface",
            RimCheck.OffSurface => "outside the surface",
            RimCheck.BelowSump => "would be at or under the sump (left alone)",
            RimCheck.BelowPipe => "a pipe top would stand above the rim (left alone)",
            _ => c.ToString(),
        };

        private static void Fit(Document doc, NetworksSettings s, bool write, StringBuilder log)
        {
            if (!Enum.TryParse(s.FitFace, true, out StructureFace face)) face = StructureFace.InnerWall;
            Database db = doc.Database;
            using (write ? doc.LockDocument() : null)
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var nets = Networks(tr, db, s, out _);
                List<ObjectId> pipes = nets.SelectMany(n => n.pipes).ToList();
                List<ManholeAdjustment> plan = NetworkEditOps.PlanFit(tr, pipes, face, s.ConnectLooseEnds, s.FitBoxLength, s.FitBoxWidth, s.FitMaxMove);
                List<ManholeAdjustment> chosen = plan.Where(a => a.Actionable &&
                    (Math.Abs(a.StartChange) >= s.FitMinMove || Math.Abs(a.EndChange) >= s.FitMinMove || a.ConnectStart || a.ConnectEnd)).ToList();
                // Duct pieces between two joints have no structure at either end: nothing to fit, not a problem.
                int betweenJoints = plan.Count(a => !a.Actionable && a.Note == "Not at a structure");
                int notActionable = plan.Count(a => !a.Actionable) - betweenJoints;
                int already = plan.Count - chosen.Count - betweenJoints - notActionable;
                log.AppendLine($"Fit to {face}{(s.FitBoxLength > 0 && s.FitBoxWidth > 0 ? $" box {s.FitBoxLength}x{s.FitBoxWidth} m" : "")}: " +
                               $"{plan.Count} straight pipes: {chosen.Count} {(write ? "fitted" : "to fit")} " +
                               $"({chosen.Count(a => a.ConnectStart || a.ConnectEnd)} of them connect a loose end), {already} already fit, " +
                               $"{betweenJoints} between joints (no structure, nothing to do), {notActionable} cannot be fitted.");
                foreach (ManholeAdjustment a in chosen.OrderByDescending(a => Math.Max(Math.Abs(a.StartChange), Math.Abs(a.EndChange))))
                    log.AppendLine($"  {a.Name}: start {a.StartChange:+0.000;-0.000} m, end {a.EndChange:+0.000;-0.000} m{(a.ConnectStart || a.ConnectEnd ? ", connects a loose end" : "")}");
                List<ManholeAdjustment> wrongConnection = plan.Where(a => !a.Actionable && a.Note.Contains("check the connection")).ToList();
                if (wrongConnection.Count > 0)
                    log.AppendLine($"Fit: {wrongConnection.Count} pipes are connected to a structure far from their end and are left alone; check: " +
                                   string.Join(", ", wrongConnection.Take(12).Select(a => a.Name)) + ".");
                foreach (var g in plan.Where(a => !a.Actionable && a.Note != "Not at a structure").GroupBy(a => a.Note.Contains("check the connection") ? "connected to a far structure" : a.Note)
                             .OrderByDescending(g => g.Count()).Take(5))
                    log.AppendLine($"  {g.Count()} x cannot fit: {g.Key}");
                if (!write || chosen.Count == 0) { tr.Abort(); return; }
                NetworkEditOps.Counts c = NetworkEditOps.ApplyFit(tr, chosen, doc.Editor);
                tr.Commit();
                log.AppendLine($"Fit: done {c.Done}, failed {c.Failed}, loose ends connected {c.Structures}, ends left unconnected {c.Unconnected}.");
            }
        }
    }
}
