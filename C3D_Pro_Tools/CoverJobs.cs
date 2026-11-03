using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace C3DProTools
{
    /// <summary>
    /// One route in the Profile &amp; Cover Designer: existing pipes in route order (picked, from a selection or from a
    /// whole network) or a path new pipes are drawn along. Holds the check / optimise results until it is applied.
    /// </summary>
    internal sealed class CoverJob
    {
        public string Name = "";
        public string Source = "";
        public bool Include = true;

        /// <summary>Existing route: its pipes in route order and whether the route travels each from start to end.</summary>
        public readonly List<(ObjectId id, bool forward)> Pipes = new List<(ObjectId, bool)>();
        public ObjectId NetworkId;
        /// <summary>New route: the path the pipes are drawn along.</summary>
        public PathGeometry Path;
        public bool IsNew => Path != null;

        public CoverRoute Route;
        public PipeRunBuilder Builder;
        public List<RunStep> Steps;
        public bool Reversed;
        public CoverOptions Options;
        public List<CoverRow> Current, Proposed;
        public CoverSolution Solution;
        public CoverApplyResult Result;
        public bool Applied;
        public string Status = "Not checked", Error;

        public int PipeCount => IsNew ? 0 : Pipes.Count;
        public double Length => Route?.Length ?? Path?.Length ?? 0;
        public int CurrentFailing => Current?.Count(r => r.Failed) ?? 0;
        public int ProposedFailing => Proposed?.Count(r => r.Failed) ?? 0;

        /// <summary>Clears everything computed, after an option or the route itself changed.</summary>
        public void Reset()
        {
            Route = null; Builder = null; Steps = null; Current = Proposed = null; Solution = null; Error = null;
            if (!Applied) Status = "Not checked";
        }
    }

    /// <summary>Finds, prepares, solves and applies <see cref="CoverJob"/>s.</summary>
    internal static class CoverJobs
    {
        public const double CenterToleranceDefault = 0.05;

        // ---------------- Finding routes ----------------

        /// <summary>Every route of a network: pipes paired with their straightest continuation at each joint, so parallel ducts stay apart.</summary>
        public static List<CoverJob> FromNetwork(Transaction tr, ObjectId networkId, double gap, double centerTolerance, bool gravity)
        {
            Network network = (Network)tr.GetObject(networkId, OpenMode.ForRead);
            List<ObjectId> ids = network.GetPipeIds().Cast<ObjectId>().ToList();
            return FromPipes(tr, ids, gap, centerTolerance, gravity, $"Network {network.Name}");
        }

        /// <summary>The selected pipes split into routes (per network); only selected pipes are used.</summary>
        public static List<CoverJob> FromPipes(Transaction tr, List<ObjectId> pipeIds, double gap, double centerTolerance, bool gravity, string source)
        {
            List<CoverJob> jobs = new List<CoverJob>();
            foreach (IGrouping<ObjectId, ObjectId> net in pipeIds.Where(id => tr.GetObject(id, OpenMode.ForRead) is Pipe).GroupBy(id => ((Pipe)tr.GetObject(id, OpenMode.ForRead)).NetworkId))
            {
                PipeRunBuilder builder = PipeRunBuilder.Read(tr, net, gap, centerTolerance);
                foreach (List<RunStep> steps in builder.Decompose(gravity))
                {
                    CoverJob job = new CoverJob { Source = source, NetworkId = net.Key };
                    foreach (RunStep s in steps) job.Pipes.Add((builder.Pipes[s.Pipe].Id, s.Forward));
                    job.Name = RouteName(builder, steps);
                    jobs.Add(job);
                }
            }
            // Longest first: main lines before their branches.
            return jobs.OrderByDescending(j => j.Pipes.Count).ToList();
        }

        /// <summary>The straightest connected route between two picked pipes of one network, or null with the reason.</summary>
        public static CoverJob FromPick(Transaction tr, ObjectId startId, ObjectId endId, double gap, double centerTolerance, bool gravity, out string error)
        {
            error = null;
            Pipe start = (Pipe)tr.GetObject(startId, OpenMode.ForRead), end = (Pipe)tr.GetObject(endId, OpenMode.ForRead);
            if (start.NetworkId != end.NetworkId) { error = "The start and end pipes belong to different networks."; return null; }
            Network network = (Network)tr.GetObject(start.NetworkId, OpenMode.ForRead);
            PipeRunBuilder builder = PipeRunBuilder.Read(tr, network.GetPipeIds().Cast<ObjectId>(), gap, centerTolerance);
            List<RunStep> steps = builder.FindStraightestPath(builder.IndexOf(startId), builder.IndexOf(endId), gravity);
            if (steps == null)
            {
                error = $"No connected route was found between {start.Name} and {end.Name} (pipes count as connected through structures or within {gap:0.##} m of each other).";
                return null;
            }
            CoverJob job = new CoverJob { Source = "Picked", NetworkId = network.ObjectId, Name = RouteName(builder, steps) };
            foreach (RunStep s in steps) job.Pipes.Add((builder.Pipes[s.Pipe].Id, s.Forward));
            return job;
        }

        public static CoverJob FromPath(PathGeometry path, int index) =>
            new CoverJob { Source = path.Name, Path = path, Name = $"New route {index}: {path.Name}, {path.Length:0.0} m" };

        private static string RouteName(PipeRunBuilder builder, List<RunStep> steps) =>
            steps.Count == 1 ? builder.Pipes[steps[0].Pipe].Name
            : $"{builder.Pipes[steps[0].Pipe].Name} → {builder.Pipes[steps[steps.Count - 1].Pipe].Name} ({steps.Count} pipes)";

        // ---------------- Preparing and checking ----------------

        /// <summary>How the routes are read and laid out, besides the cover limits.</summary>
        internal sealed class Setup
        {
            public double Spacing = 1.0, Gap = 1.0, CenterTolerance = CenterToleranceDefault;
            /// <summary>0 = auto (majority of the pipes), 1 = route direction, 2 = reversed. Gravity only.</summary>
            public int FlowDirection;
            public CoverRoute.PathLayout Layout;
            /// <summary>Network new pipes go into (existing structures there are reused at the path ends); null = a new network.</summary>
            public ObjectId TargetNetworkId;
            public bool ReversePaths;
            /// <summary>Plan outlines of the cover zones (paving): read once per batch with <see cref="ReadZonePolygons"/>.</summary>
            public List<List<(double X, double Y)>> ZonePolygons = new List<List<(double X, double Y)>>();
        }

        /// <summary>
        /// The closed polylines in model space on the rules' zone layers (comma-separated names, * and ? allowed), as plan
        /// outlines (vertices; arc segments are taken by their chords).
        /// </summary>
        public static List<List<(double X, double Y)>> ReadZonePolygons(Transaction tr, Database db, CoverRules rules)
        {
            List<List<(double X, double Y)>> polygons = new List<List<(double X, double Y)>>();
            string[] patterns = (rules?.ZoneLayers ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            if (patterns.Length == 0) return polygons;
            System.Text.RegularExpressions.Regex[] layers = patterns
                .Select(p => new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Polyline pl) || !pl.Closed || pl.NumberOfVertices < 3) continue;
                if (!layers.Any(l => l.IsMatch(pl.Layer))) continue;
                List<(double X, double Y)> outline = new List<(double X, double Y)>();
                for (int i = 0; i < pl.NumberOfVertices; i++) { Point2d v = pl.GetPoint2dAt(i); outline.Add((v.X, v.Y)); }
                polygons.Add(outline);
            }
            return polygons;
        }

        /// <summary>Reads the job's route from the drawing (or lays out its path), finds crossing pipes and checks the current cover.</summary>
        public static void Prepare(Transaction tr, CivilDocument doc, CoverJob job, Autodesk.Civil.DatabaseServices.Surface surface, CoverOptions o, Setup setup,
            HashSet<ObjectId> batchPipes)
        {
            job.Reset();
            if (job.IsNew)
            {
                PathGeometry path = setup.ReversePaths ? job.Path.Reversed() : job.Path;
                CoverRoute.PathLayout layout = setup.Layout;
                layout.StartStructure = layout.EndStructure = ObjectId.Null;
                if (!setup.TargetNetworkId.IsNull)
                {
                    PathSegment first = path.Segments[0], last = path.Segments[path.Segments.Count - 1];
                    layout.StartStructure = StructureAt(tr, setup.TargetNetworkId, first.P0);
                    layout.EndStructure = StructureAt(tr, setup.TargetNetworkId, last.P1);
                }
                job.Route = CoverRoute.BuildFromPath(tr, path, surface, setup.Spacing, layout);
                job.Route.NetworkId = setup.TargetNetworkId;
            }
            else
            {
                List<ObjectId> ids = job.Pipes.Select(p => p.id).Where(id => !id.IsErased && id.IsValid).ToList();
                if (ids.Count != job.Pipes.Count) { job.Error = "Some of the route's pipes no longer exist; add the route again."; job.Status = "Error"; return; }
                job.Builder = PipeRunBuilder.Read(tr, ids, setup.Gap, setup.CenterTolerance);
                job.Steps = job.Pipes.Select(p => new RunStep { Pipe = job.Builder.IndexOf(p.id), Forward = p.forward }).ToList();
                if (o.Mode == CoverMode.Gravity)
                {
                    int forward = job.Steps.Count(s => s.Forward);
                    job.Reversed = setup.FlowDirection == 2 || (setup.FlowDirection == 0 && job.Steps.Count - forward > forward);
                    if (job.Reversed) job.Steps = PipeRunBuilder.Reverse(job.Steps);
                }
                job.Route = CoverRoute.Build(tr, job.Builder, job.Steps, surface, setup.Spacing);
            }

            if (job.Route.MissingGround == job.Route.Records.Count) { job.Error = "The surface does not cover the route."; job.Status = "Error"; return; }

            // The route's own utility class sets the clearances; a dry route of an unknown kind counts as power (dry).
            CoverRules rules = o.Rules ?? new CoverRules();
            string networkName = !job.Route.NetworkId.IsNull && tr.GetObject(job.Route.NetworkId, OpenMode.ForRead) is Network routeNet ? routeNet.Name : "";
            job.Route.Class = rules.Classify(networkName, false);
            if (job.Route.Class == UtilityClass.Unknown && o.Mode == CoverMode.Dry) job.Route.Class = UtilityClass.DryPower;
            if (o.CrossingClearance > 0 || rules.UseClearanceMatrix)
            {
                HashSet<ObjectId> exclude = new HashSet<ObjectId>(batchPipes);
                foreach ((ObjectId id, bool _) in job.Pipes) exclude.Add(id);
                job.Route.FindCrossings(tr, doc, exclude, rules, o.CrossingClearance);
            }
            if (setup.ZonePolygons.Count > 0)
                job.Route.AddZones(job.Route.Records.ConvertAll(r => (r.Plan.X, r.Plan.Y)), setup.ZonePolygons, rules.ZoneMinCover, rules.ZoneLabel);

            job.Options = o.ForRoute(double.NaN, double.NaN);
            // Gravity routes run in the flow direction; "start" and "end" level choices stay with the route as picked.
            if (job.Reversed) { bool f = job.Options.StartFixed; job.Options.StartFixed = job.Options.EndFixed; job.Options.EndFixed = f; }
            // New pipes have no level of their own to keep.
            if (job.IsNew) job.Options.StartFixed = job.Options.EndFixed = false;

            if (!job.IsNew)
            {
                job.Current = CoverEvaluation.Evaluate(job.Route, CurrentPipes(job.Route), job.Options);
                job.Status = job.CurrentFailing == 0 ? "Current cover OK" : $"Current: {job.CurrentFailing} of {job.Current.Count} failing";
            }
            else job.Status = "Laid out";
        }

        public static List<ProfilePipe> CurrentPipes(CoverRoute route) =>
            route.Pipes.ConvertAll(p => new ProfilePipe { Section = p.Section, S0 = p.S0, S1 = p.S1, Z0 = p.Z0, Z1 = p.Z1, Name = p.Name, Source = p });

        /// <summary>An existing structure of the network whose plan footprint holds the point, or null.</summary>
        private static ObjectId StructureAt(Transaction tr, ObjectId networkId, Point2d p)
        {
            if (!(tr.GetObject(networkId, OpenMode.ForRead) is Network net)) return ObjectId.Null;
            ObjectId best = ObjectId.Null;
            double bestDist = double.MaxValue;
            foreach (ObjectId id in net.GetStructureIds())
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Structure st)) continue;
                double d = new Point2d(st.Location.X, st.Location.Y).GetDistanceTo(p);
                bool inside = d <= 0.05;
                if (!inside)
                {
                    try
                    {
                        Extents3d e = ((Entity)st).GeometricExtents;
                        inside = p.X >= e.MinPoint.X - 0.01 && p.X <= e.MaxPoint.X + 0.01 && p.Y >= e.MinPoint.Y - 0.01 && p.Y <= e.MaxPoint.Y + 0.01;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { }
                }
                if (inside && d < bestDist) { bestDist = d; best = id; }
            }
            return best;
        }

        // ---------------- Solving ----------------

        /// <summary>
        /// Solves every prepared job. On gravity networks a route that ends in a structure another route leaves from is solved
        /// after it, and must arrive so the main line's outgoing pipe meets the junction rule and minimum drop (no backfall
        /// into the main line); deeper than the maximum drop only with a backdrop, as the rules allow. Pure data only, so it
        /// can run off the UI thread; <see cref="CoverOptions.Cancel"/> stops it (OperationCanceledException).
        /// </summary>
        public static void SolveAll(List<CoverJob> jobs)
        {
            List<CoverJob> ready = jobs.Where(j => j.Include && j.Route != null && j.Error == null).ToList();
            HashSet<CoverJob> done = new HashSet<CoverJob>();
            bool gravity = ready.Count > 0 && ready[0].Options.Mode == CoverMode.Gravity;

            // The job leaving each structure (its first pipe there), for the junction rule.
            Dictionary<ObjectId, (CoverJob job, int pipe)> leaving = new Dictionary<ObjectId, (CoverJob, int)>();
            if (gravity)
                foreach (CoverJob j in ready.Where(j => !j.IsNew))
                    for (int i = 0; i < j.Steps.Count; i++)
                    {
                        RunHub hub = j.Builder.HubAt(j.Steps[i], true);
                        if (hub != null && !leaving.ContainsKey(hub.Id)) leaving[hub.Id] = (j, i);
                    }

            void Solve(CoverJob job, HashSet<CoverJob> visiting)
            {
                if (done.Contains(job)) return;
                CoverRules rules = job.Options.Rules ?? new CoverRules();
                double endMin = double.NaN, endMax = double.NaN, backdropMax = double.NaN;
                string mainName = null;
                if (gravity && !job.IsNew)
                {
                    RunHub exit = job.Builder.HubAt(job.Steps[job.Steps.Count - 1], false);
                    if (exit != null && leaving.TryGetValue(exit.Id, out var main) && main.job != job && !visiting.Contains(main.job))
                    {
                        visiting.Add(job);
                        Solve(main.job, visiting);
                        visiting.Remove(job);
                        (double zOut, double dOut, double sOut) = Outgoing(main.job, main.pipe);
                        if (!double.IsNaN(zOut))
                        {
                            // The branch is the inlet: it arrives high enough for the main line's outlet to meet the rule.
                            double dIn = job.Route.Pipes[job.Route.Pipes.Count - 1].InnerHeight;
                            double turn = Math.Abs(Math.IEEERemainder(main.job.Route.PlanBearingAt(sOut) - job.Route.PlanBearingAt(job.Route.Length), 2 * Math.PI)) * 180 / Math.PI;
                            endMin = CoverRules.MinInletCentre(rules.JunctionFor(CoverMode.Gravity), zOut, dIn, dOut, rules.MinDropFor(CoverMode.Gravity, turn));
                            if (double.IsNaN(endMin)) endMin = zOut;
                            // The drop into the main line is limited like any other drop at a manhole.
                            if (rules.Backdrop != BackdropMode.UnrestrictedDrop && job.Options.MaxDrop > 0) endMax = endMin + job.Options.MaxDrop;
                            if (rules.Backdrop == BackdropMode.CostedBackdropWithFlag) backdropMax = endMin + rules.MaxBackdropDrop;
                            mainName = main.job.Name;
                        }
                    }
                }
                done.Add(job);
                job.Options.EndMinZ = endMin;
                job.Options.EndMaxZ = endMax;
                bool backdropAtJunction = false;
                try
                {
                    job.Solution = new CoverSolver(job.Route, job.Options).Solve();
                    if ((job.Solution == null || job.Solution.Relaxation > 1e-9) && !double.IsNaN(backdropMax))
                    {
                        // No layout arrives within the standard drop: a backdrop into the main line, as the rules allow.
                        job.Options.EndMaxZ = backdropMax;
                        CoverSolution deeper = new CoverSolver(job.Route, job.Options).Solve();
                        if (deeper != null && (job.Solution == null || deeper.Relaxation < job.Solution.Relaxation - 1e-9))
                        {
                            job.Solution = deeper;
                            backdropAtJunction = true;
                        }
                        else job.Options.EndMaxZ = endMax;
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException)) { job.Solution = null; job.Error = ex.Message; }
                if (job.Solution == null)
                {
                    job.Proposed = null;
                    job.Status = "No layout";
                    job.Error = job.Error ?? "No layout meets the slope, length and drop limits on this route, even with the cover limits widened by 10 m. " +
                                "Check the fixed start / end levels, the flow direction and any crossing pipes.";
                    return;
                }
                NameProposed(job.Route, job.Solution);
                job.Proposed = CoverEvaluation.Evaluate(job.Route, job.Solution.Pipes, job.Options);
                int now = job.Route.Pipes.Count;
                job.Status = job.IsNew
                    ? $"{job.Solution.Pipes.Count} new pipe(s), {job.ProposedFailing} failing"
                    : $"Proposed {job.Solution.Pipes.Count} pipe(s) (now {now}), {job.ProposedFailing} failing";
                if (job.Solution.Relaxation > 0.001) job.Status += $"; up to {job.Solution.Relaxation:0.00} m past the cover limits where nothing fits";
                if (backdropAtJunction)
                {
                    ProfilePipe last = job.Solution.Pipes[job.Solution.Pipes.Count - 1];
                    job.Status += $"; backdrop manhole required where it joins {mainName} (it arrives {last.Z1 - endMin:0.00} m above the matched level)";
                }
                int backdrops = job.Proposed.Count(r => r.Flags.Exists(f => f.StartsWith("Backdrop")));
                int slabs = job.Proposed.Sum(r => r.Flags.Count(f => f.StartsWith("Protection slab")));
                if (backdrops > 0) job.Status += $"; {backdrops} backdrop manhole(s)";
                if (slabs > 0) job.Status += $"; {slabs} crossing(s) need a protection slab / HDD";
                if (job.Route.Proximities.Count > 0) job.Status += $"; {job.Route.Proximities.Count} pipe(s) alongside closer than the horizontal clearance";
            }

            foreach (CoverJob job in ready)
            {
                job.Options.Cancel.ThrowIfCancellationRequested();
                Solve(job, new HashSet<CoverJob>());
            }
        }

        /// <summary>Centreline, inner height and station of the solved pipe leaving the structure at step <paramref name="pipe"/> of a solved job (NaN when unsolved).</summary>
        private static (double z, double d, double s) Outgoing(CoverJob job, int pipe)
        {
            if (job.Solution == null) return (double.NaN, 0, 0);
            RoutePipe rp = job.Route.Pipes[pipe];
            ProfilePipe p = job.Solution.Pipes.Find(x => x.Section == rp.Section && x.S0 <= rp.S0 + 1e-6 && x.S1 >= rp.S0 - 1e-6);
            return p == null ? (double.NaN, 0, 0) : (p.ZAt(rp.S0), p.Source.InnerHeight, rp.S0);
        }

        /// <summary>Names the proposed pipes the way they will be applied: reused originals keep their names, extras are new copies.</summary>
        public static void NameProposed(CoverRoute route, CoverSolution solution)
        {
            int n = 0;
            foreach (RouteSection sec in route.Sections)
            {
                List<ProfilePipe> segments = solution.Pipes.FindAll(p => p.Section == sec.Index);
                for (int i = 0; i < segments.Count; i++)
                    segments[i].Name = route.IsNew ? $"New pipe {++n}"
                        : i < sec.Pipes.Count ? sec.Pipes[i].Name : $"New pipe (copy of {segments[i].Source.Name})";
            }
        }

        // ---------------- Applying ----------------

        /// <summary>Applies one solved existing route; returns false (with the job's error set) when it cannot be changed.</summary>
        /// <param name="structureTemplateId">A structure to copy for new manholes when the chosen part cannot be built; null = none.</param>
        public static bool ApplyExisting(Transaction tr, CoverJob job, ObjectId structureFamilyId, ObjectId structureSizeId, Editor ed, ObjectId structureTemplateId = default)
        {
            if (job.Route.IsReference) { job.Error = "The route's network is a data reference; open the drawing that owns it to change it."; job.Status = "Not applied"; return false; }
            job.Result = CoverApply.Apply(tr, job.Route, job.Solution, job.Options, structureFamilyId, structureSizeId, ed, structureTemplateId);
            job.Applied = true;
            job.Status = $"Applied: {job.Result.Reused} re-levelled, {job.Result.Created} added, {job.Result.Erased} removed, {job.Result.NewStructures} new structure(s)" +
                         (job.Result.Copied > 0 ? $"; {job.Result.Copied} made as copies of existing parts (family or catalog missing)" : "");
            return true;
        }

        public static void ApplyNew(Transaction tr, CoverJob job, DrawTarget target, Editor ed)
        {
            job.Result = CoverDraw.Apply(tr, job.Route, job.Solution, job.Options, target, ed);
            job.Applied = true;
            job.Status = $"Drawn: {job.Result.Created} pipe(s), {job.Result.NewStructures} structure(s)" + (job.Result.Failed > 0 ? $", {job.Result.Failed} failed" : "") +
                         (job.Result.Copied > 0 ? $"; {job.Result.Copied} made as copies of existing parts" : "");
        }

        /// <summary>The applied route read back from the drawing, with its cover checked there (what was really built).</summary>
        public static List<CoverRow> CheckBuilt(Transaction tr, CoverJob job, Autodesk.Civil.DatabaseServices.Surface surface, Setup setup,
            out CoverRoute built, out PipeRunBuilder builder, out List<RunStep> steps)
        {
            built = null; builder = null; steps = null;
            if (job.Result == null || job.Result.PipeIds.Count == 0) return null;
            builder = PipeRunBuilder.Read(tr, job.Result.PipeIds, setup.Gap, setup.CenterTolerance);
            steps = builder.FindStraightestPath(builder.IndexOf(job.Result.PipeIds[0]), builder.IndexOf(job.Result.PipeIds[job.Result.PipeIds.Count - 1]), job.Options.Mode == CoverMode.Gravity);
            if (steps == null) return null;
            built = CoverRoute.Build(tr, builder, steps, surface, setup.Spacing);
            return CoverEvaluation.Evaluate(built, CurrentPipes(built), job.Options);
        }
    }
}
