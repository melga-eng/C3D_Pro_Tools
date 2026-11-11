using System;
using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Sheets.Contracts;
using C3DProTools.Features.Sheets.Engines;

// Profile DIMs (docs/sheet-production-contract.md, section 7, "Building DimInput from the scene"): turns one profile view of the
// scene into the DIM engine's input, runs DimStations, and says on which side of its station each DIM line goes. Plain data only;
// the Civil 3D side (ProfileDimsCivil3D) draws the result.
namespace C3DProTools.Features.Sheets.Application
{
    /// <summary>What the Profile DIMs tool plans.</summary>
    internal sealed record ProfileDimOptions
    {
        public DimRules Rules { get; init; } = new DimRules();
        public bool Cover { get; init; } = true;
        public bool Clearance { get; init; } = true;
        /// <summary>Clearance DIMs only for crossing pipes the view draws (a DIM to a pipe that is not shown points at nothing).</summary>
        public bool CrossingsShownOnly { get; init; } = true;
        /// <summary>How far the DIM line sits from its station, in true metres along the view.</summary>
        public double OffsetM { get; init; } = 2.0;
    }

    /// <summary>A planned DIM and the side of its station the DIM line goes: +1 ahead (higher station), −1 back.</summary>
    internal sealed record PlacedDim(PlannedDim Dim, int Side);

    internal sealed record ViewDimPlan(ProfileViewSnapshot View, string TopProfile, string SurfaceProfile,
        IReadOnlyList<PlacedDim> Dims, IReadOnlyList<OperationIssue> Issues);

    internal static class ProfileDimCodes
    {
        public const string NoAlignment = "dims.view.noAlignment";
        public const string NoTop = "dims.view.noTop";
        public const string NoSurface = "dims.view.noSurface";
        public const string NoDuct = "dims.view.noDuct";
        public const string TopFromPipes = "dims.view.topFromPipes";
    }

    internal static class ProfileDimPlanner
    {
        private const double Tol = 1e-6;

        public static ViewDimPlan PlanView(SheetScene scene, ProfileViewSnapshot view, ProfileDimOptions options)
        {
            options ??= new ProfileDimOptions();
            List<OperationIssue> issues = new List<OperationIssue>();
            ViewDimPlan Empty(string top = "", string surface = "") => new ViewDimPlan(view, top, surface, Array.Empty<PlacedDim>(), issues);

            AlignmentSnapshot al = scene.Alignments.FirstOrDefault(a => a.Ref == view.Alignment);
            if (al == null)
            {
                issues.Add(OperationIssue.Error(ProfileDimCodes.NoAlignment, $"The alignment of {view.Name} was not read."));
                return Empty();
            }
            double vs = Math.Min(view.StartStationM, view.EndStationM), ve = Math.Max(view.StartStationM, view.EndStationM);

            List<ViewPart> ownPipes = view.DrawnParts.Where(p => p.IsOwnNetwork && p.Kind == ViewPartKind.Pipe).Select(Forward).ToList();
            List<ViewPart> ownStructures = view.DrawnParts.Where(p => p.IsOwnNetwork && p.Kind == ViewPartKind.Structure).ToList();

            // Duct top: the TOP profile of the view's alignment; without one, the tops of the drawn duct pipes.
            SceneProfile topProfile = Best(al, SceneProfileRole.Top, vs, ve);
            List<StationElevation> top;
            string topName;
            if (topProfile != null)
            {
                top = Clip(topProfile.Runs.OrderByDescending(r => Overlap(r, vs, ve)).First(), vs, ve);
                topName = topProfile.Name;
            }
            else
            {
                top = Clip(PipeTops(ownPipes), vs, ve);
                topName = "";
                if (top.Count >= 2)
                    issues.Add(OperationIssue.Warning(ProfileDimCodes.TopFromPipes, "The alignment has no TOP profile; the duct pipes' outside tops are used."));
            }
            if (top.Count < 2)
            {
                issues.Add(OperationIssue.Error(ProfileDimCodes.NoTop, "No TOP profile and no duct pipes in this view, so there is no duct top to dimension."));
                return Empty();
            }
            double from = top[0].StationM, to = top[^1].StationM;

            bool clearance = options.Clearance;
            if (ownPipes.Count == 0 && clearance)
            {
                clearance = false;
                issues.Add(OperationIssue.Warning(ProfileDimCodes.NoDuct, "The view draws no duct pipes, so the duct's outside bottom is unknown; clearance DIMs are skipped."));
            }

            // Duct bottom: the top less the outside height of the duct pipe at that station (the nearest pipe off the pipes).
            SortedSet<double> stations = new SortedSet<double>(top.Select(p => p.StationM));
            foreach (ViewPart p in ownPipes)
                foreach (double s in new[] { p.StartStationM, p.EndStationM })
                    if (s > from + Tol && s < to - Tol) stations.Add(s);
            List<StationElevation> bottom = new List<StationElevation>();
            foreach (double s in stations)
            {
                if (bottom.Count > 0 && s - bottom[^1].StationM <= Tol) continue;
                bottom.Add(new StationElevation(s, At(top, s) - Math.Max(0.001, HeightAt(ownPipes, s))));
            }

            SceneProfile surfaceProfile = Best(al, SceneProfileRole.Proposed, vs, ve);
            List<IReadOnlyList<StationElevation>> surface = new List<IReadOnlyList<StationElevation>>();
            if (surfaceProfile != null)
                foreach (var run in surfaceProfile.Runs.OrderBy(r => r[0].StationM))
                {
                    List<StationElevation> c = Clip(run, from, to);
                    if (c.Count >= 2 && (surface.Count == 0 || c[0].StationM > surface[^1][^1].StationM - Tol)) surface.Add(c);
                }
            if (options.Cover && surface.Count == 0)
                issues.Add(OperationIssue.Warning(ProfileDimCodes.NoSurface, "No proposed surface profile covers this view, so there are no cover DIMs."));

            List<StructureSpan> structures = ownStructures
                .Select(s => new StructureSpan(s.Part.Handle, Math.Min(s.StartStationM, s.EndStationM), Math.Max(s.StartStationM, s.EndStationM)))
                .ToList();

            List<CrossingPipe> crossings = new List<CrossingPipe>();
            if (clearance)
                foreach (CrossingSnapshot c in scene.Crossings.Where(c => c.ProfileView == view.Ref && (c.ShownInView || !options.CrossingsShownOnly))
                    .OrderBy(c => c.StationM))
                {
                    string key = string.IsNullOrEmpty(c.CrossingNetwork) ? c.Name : $"{c.Name} ({c.CrossingNetwork})";
                    // The same pipe found twice at one station (a path sample on the alignment) is one crossing.
                    if (crossings.Any(x => x.Key == key && Math.Abs(x.StationM - c.StationM) < 0.05)) continue;
                    crossings.Add(new CrossingPipe(key, c.StationM, c.CrossingTopM, c.CrossingBottomM));
                }

            DimInput input = new DimInput
            {
                DuctTop = top, DuctBottom = bottom, ProposedSurface = surface, Structures = structures, Crossings = crossings,
                OwnedFromM = from, OwnedToM = to, OwnsEnd = true,
            };
            DimPlan plan = DimStations.Plan(input, options.Rules);
            issues.AddRange(plan.Issues.Where(i => options.Cover || !i.Code.StartsWith("dims.cover", StringComparison.Ordinal) && i.Code != DimCodes.SurfaceMissing));

            List<PlacedDim> placed = new List<PlacedDim>();
            foreach (PlannedDim d in plan.Dims)
            {
                if (d.Kind == DimKind.Cover && !options.Cover) continue;
                if (d.Kind == DimKind.Clearance && !clearance) continue;
                placed.Add(new PlacedDim(d, SideOf(d, structures, from, to, options.OffsetM)));
            }
            return new ViewDimPlan(view, topName, surfaceProfile?.Name ?? "", placed, issues);
        }

        /// <summary>
        /// The DIM line goes over the duct, never over a structure: back of a structure's start face, ahead of its end face.
        /// Elsewhere it goes ahead, unless that would leave the duct's range.
        /// </summary>
        internal static int SideOf(PlannedDim d, IReadOnlyList<StructureSpan> structures, double from, double to, double offset)
        {
            if (d.Reason == DimReasons.StructureFace)
            {
                StructureSpan s = structures.FirstOrDefault(x => x.Key == d.SourceKey);
                if (s != null)
                {
                    int side = Math.Abs(d.StationM - s.StartStationM) <= Math.Abs(d.StationM - s.EndStationM) ? -1 : 1;
                    if (side < 0 && d.StationM - offset < from - Tol) side = 1;
                    else if (side > 0 && d.StationM + offset > to + Tol) side = -1;
                    return side;
                }
            }
            return d.StationM + offset > to + Tol ? -1 : 1;
        }

        private static ViewPart Forward(ViewPart p) => p.StartStationM <= p.EndStationM ? p
            : p with { StartStationM = p.EndStationM, EndStationM = p.StartStationM, StartTopM = p.EndTopM, StartBottomM = p.EndBottomM, EndTopM = p.StartTopM, EndBottomM = p.StartBottomM };

        /// <summary>The profile of a role that best covers the view: most overlap, then the one named after the alignment.</summary>
        private static SceneProfile Best(AlignmentSnapshot al, SceneProfileRole role, double vs, double ve) =>
            al.Profiles.Where(p => p.Role == role && p.Runs.Count > 0)
                .OrderByDescending(p => Math.Round(p.Runs.Sum(r => Overlap(r, vs, ve)), 3))
                .ThenByDescending(p => p.Name.IndexOf(al.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(p => p.Runs.Any(r => Overlap(r, vs, ve) > Tol));

        private static double Overlap(IReadOnlyList<StationElevation> run, double a, double b) =>
            run.Count < 2 ? 0 : Math.Max(0, Math.Min(b, run[^1].StationM) - Math.Max(a, run[0].StationM));

        /// <summary>The part of a run inside [a, b], with interpolated ends.</summary>
        internal static List<StationElevation> Clip(IReadOnlyList<StationElevation> run, double a, double b)
        {
            List<StationElevation> list = new List<StationElevation>();
            if (run.Count < 2 || run[^1].StationM < a - Tol || run[0].StationM > b + Tol) return list;
            double s0 = Math.Max(a, run[0].StationM), s1 = Math.Min(b, run[^1].StationM);
            if (s1 - s0 <= Tol) return list;
            list.Add(new StationElevation(s0, At(run, s0)));
            foreach (StationElevation p in run)
                if (p.StationM > s0 + Tol && p.StationM < s1 - Tol) list.Add(p);
            list.Add(new StationElevation(s1, At(run, s1)));
            return list;
        }

        internal static double At(IReadOnlyList<StationElevation> line, double s)
        {
            if (s <= line[0].StationM) return line[0].ElevationM;
            if (s >= line[^1].StationM) return line[^1].ElevationM;
            for (int i = 1; i < line.Count; i++)
                if (line[i].StationM >= s)
                {
                    double span = line[i].StationM - line[i - 1].StationM;
                    double t = span <= 0 ? 0 : (s - line[i - 1].StationM) / span;
                    return line[i - 1].ElevationM + (line[i].ElevationM - line[i - 1].ElevationM) * t;
                }
            return line[^1].ElevationM;
        }

        /// <summary>The duct's outside tops along its pipes: where pipes overlap or meet, the higher top.</summary>
        private static List<StationElevation> PipeTops(List<ViewPart> pipes)
        {
            SortedDictionary<double, double> tops = new SortedDictionary<double, double>();
            void Put(double s, double z)
            {
                double key = Math.Round(s, 4);
                tops[key] = tops.TryGetValue(key, out double old) ? Math.Max(old, z) : z;
            }
            foreach (ViewPart p in pipes.Where(p => p.EndStationM - p.StartStationM > Tol))
            {
                Put(p.StartStationM, p.StartTopM);
                Put(p.EndStationM, p.EndTopM);
            }
            return tops.Select(kv => new StationElevation(kv.Key, kv.Value)).ToList();
        }

        /// <summary>The outside height of the duct pipe at a station; off the pipes, that of the nearest one.</summary>
        private static double HeightAt(List<ViewPart> pipes, double s)
        {
            ViewPart best = null;
            double bestGap = double.MaxValue;
            foreach (ViewPart p in pipes)
            {
                double gap = s < p.StartStationM ? p.StartStationM - s : s > p.EndStationM ? s - p.EndStationM : 0;
                if (gap < bestGap) { bestGap = gap; best = p; }
            }
            if (best == null) return 0;
            double len = best.EndStationM - best.StartStationM;
            double t = len <= Tol ? 0 : Math.Clamp((s - best.StartStationM) / len, 0, 1);
            double h0 = best.StartTopM - best.StartBottomM, h1 = best.EndTopM - best.EndBottomM;
            return h0 + (h1 - h0) * t;
        }
    }
}
