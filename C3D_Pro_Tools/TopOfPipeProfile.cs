using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;

namespace C3DProTools
{
    internal sealed class TopProfileOptions
    {
        /// <summary>Profile name: the prefix, then the route's first and last pipe (or the view's name for one profile).</summary>
        public string Prefix = "TOP - ";
        /// <summary>One profile along the highest top of all pipes in the view, instead of one per route.</summary>
        public bool Highest;
        public ObjectId StyleId = ObjectId.Null, LabelSetId = ObjectId.Null, LayerId = ObjectId.Null;
    }

    internal sealed class TopProfileResult
    {
        public int Created, Updated;
        public readonly List<string> Names = new List<string>();
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Layout profiles along the outside top of the pipes drawn in a profile view (top of wall or encasement: the part's
    /// outermost height). The pipes are split into routes as for cover (parallel pipes kept apart); each route becomes one
    /// profile on the view's alignment, or one profile follows the highest top of them all. Running it again updates the
    /// profiles of the same name.
    /// </summary>
    internal static class TopOfPipeProfile
    {
        // Pipes whose run along the alignment is less than this share of their length cross it rather than follow it.
        private const double AlongShare = 0.5;
        private const double CurveSampleSpacing = 5.0;
        private const double SameStation = 0.001;

        public static TopProfileResult Create(Transaction tr, CivilDocument doc, ObjectId viewId, TopProfileOptions o)
        {
            TopProfileResult result = new TopProfileResult();
            ProfileView view = (ProfileView)tr.GetObject(viewId, OpenMode.ForRead);
            Alignment al = (Alignment)tr.GetObject(view.AlignmentId, OpenMode.ForRead);
            double s0 = view.StationStart, s1 = view.StationEnd;

            // The gravity pipes drawn in this view that run along its alignment.
            List<ObjectId> pipes = new List<ObjectId>();
            int crossing = 0;
            foreach (ObjectId netId in doc.GetPipeNetworkIds())
            {
                if (!(tr.GetObject(netId, OpenMode.ForRead) is Network net)) continue;
                foreach (ObjectId id in net.GetPipeIds().Live())
                {
                    Pipe p = (Pipe)tr.GetObject(id, OpenMode.ForRead);
                    bool shown;
                    try { shown = p.GetProfileViewsDisplayingMe().Cast<ObjectId>().Contains(viewId); } catch (Exception) { shown = false; }
                    if (!shown) continue;
                    if (!TryStation(al, p.StartPoint, out double a) || !TryStation(al, p.EndPoint, out double b)) continue;
                    double planLength = new Point2d(p.StartPoint.X, p.StartPoint.Y).GetDistanceTo(new Point2d(p.EndPoint.X, p.EndPoint.Y));
                    if (planLength > 1e-6 && Math.Abs(b - a) < AlongShare * planLength) { crossing++; continue; }
                    pipes.Add(id);
                }
            }
            if (crossing > 0) result.Notes.Add($"{view.Name}: {crossing} pipe(s) crossing the alignment left out.");
            if (pipes.Count == 0) { result.Notes.Add($"{view.Name}: no pipes along the alignment are drawn in this view."); return result; }

            List<CoverJob> routes = CoverJobs.FromPipes(tr, pipes, 1.0, CoverJobs.CenterToleranceDefault, false, view.Name);
            List<(string name, List<(double s, double z)> line)> lines = new List<(string, List<(double, double)>)>();
            foreach (CoverJob route in routes)
            {
                List<(double s, double z)> line = new List<(double, double)>();
                foreach ((ObjectId id, bool _) in route.Pipes) AddPipe(tr, al, (Pipe)tr.GetObject(id, OpenMode.ForRead), line);
                line = Clean(line, s0, s1);
                if (line.Count < 2) continue;
                string first = ((Pipe)tr.GetObject(route.Pipes[0].id, OpenMode.ForRead)).Name;
                string last = ((Pipe)tr.GetObject(route.Pipes[route.Pipes.Count - 1].id, OpenMode.ForRead)).Name;
                lines.Add((route.Pipes.Count == 1 ? first : $"{first} to {last}", line));
            }
            if (lines.Count == 0) { result.Notes.Add($"{view.Name}: the pipes lie outside the view's stations."); return result; }

            if (o.Highest) lines = new List<(string, List<(double, double)>)> { (view.Name, Highest(lines.Select(l => l.line).ToList())) };

            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ObjectId> existing = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId pid in al.GetProfileIds())
                if (tr.GetObject(pid, OpenMode.ForRead) is Profile pr) existing[pr.Name] = pid;
            foreach ((string name, List<(double s, double z)> line) in lines)
            {
                string profileName = (o.Prefix ?? "") + name;
                for (int i = 2; taken.Contains(profileName); i++) profileName = $"{o.Prefix}{name} ({i})";
                taken.Add(profileName);
                if (existing.TryGetValue(profileName, out ObjectId oldId))
                {
                    if (tr.GetObject(oldId, OpenMode.ForWrite) is Profile old && old.ProfileType == ProfileType.FG && Replace(old, line))
                    {
                        result.Updated++;
                        result.Names.Add(profileName);
                    }
                    else result.Notes.Add($"{profileName}: a profile of that name exists and could not be updated.");
                    continue;
                }
                ObjectId layer = o.LayerId.IsNull ? al.LayerId : o.LayerId;
                ObjectId style = o.StyleId.IsNull ? doc.Styles.ProfileStyles.Cast<ObjectId>().FirstOrDefault() : o.StyleId;
                ObjectId labels = o.LabelSetId.IsNull ? doc.Styles.LabelSetStyles.ProfileLabelSetStyles.Cast<ObjectId>().FirstOrDefault() : o.LabelSetId;
                Profile profile = (Profile)tr.GetObject(Profile.CreateByLayout(profileName, al.ObjectId, layer, style, labels), OpenMode.ForWrite);
                foreach ((double s, double z) in line) profile.PVIs.AddPVI(s, z);
                result.Created++;
                result.Names.Add(profileName);
            }
            return result;
        }

        /// <summary>
        /// Gives an existing layout profile the new points, keeping the profile itself (its bands and labels stay attached).
        /// Civil 3D never removes a profile's first or last PVI, so those two are moved to the new ends instead.
        /// </summary>
        private static bool Replace(Profile profile, List<(double s, double z)> line)
        {
            try
            {
                ProfilePVICollection pvis = profile.PVIs;
                while (pvis.Count > 2) pvis.RemoveAt(1);
                if (pvis.Count < 2) return false;
                ProfilePVI first = pvis[0], last = pvis[1];
                (double s, double z) a = line[0], b = line[line.Count - 1];
                // Move the end that does not pass the other first.
                if (a.s < last.RawStation) { first.RawStation = a.s; last.RawStation = b.s; }
                else { last.RawStation = b.s; first.RawStation = a.s; }
                first.Elevation = a.z;
                last.Elevation = b.z;
                for (int i = 1; i < line.Count - 1; i++) pvis.AddPVI(line[i].s, line[i].z);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool TryStation(Alignment al, Point3d p, out double station)
        {
            station = 0;
            double offset = 0;
            try { al.StationOffset(p.X, p.Y, ref station, ref offset); return true; }
            catch (Exception) { return false; }
        }

        /// <summary>The pipe's outside top as (station, elevation): its ends, and points along it when it is curved.</summary>
        private static void AddPipe(Transaction tr, Alignment al, Pipe p, List<(double s, double z)> line)
        {
            double half = CoverRoute.OuterHeightOf(p) / 2.0;
            ArcInfo? arc = null;
            if (p.SubEntityType == PipeSubEntityType.Curved && PipeArcGeometry.TryGetGravityArc(p, out ArcInfo a)) arc = a;
            List<Point3d> points = arc.HasValue ? PipeArcGeometry.SamplePath(p.StartPoint, p.EndPoint, arc, CurveSampleSpacing, 200) : new List<Point3d> { p.StartPoint, p.EndPoint };
            foreach (Point3d q in points)
                if (TryStation(al, q, out double s)) line.Add((s, q.Z + half));
        }

        /// <summary>In station order inside the view; at one station (a drop at a structure) the higher top; straight runs merged.</summary>
        private static List<(double s, double z)> Clean(List<(double s, double z)> line, double s0, double s1)
        {
            List<(double s, double z)> sorted = line.Where(x => x.s >= s0 - SameStation && x.s <= s1 + SameStation)
                .Select(x => (Math.Max(s0, Math.Min(s1, x.s)), x.z)).OrderBy(x => x.Item1).ToList();
            List<(double s, double z)> merged = new List<(double, double)>();
            foreach ((double s, double z) in sorted)
            {
                if (merged.Count > 0 && s - merged[merged.Count - 1].s < SameStation)
                {
                    if (z > merged[merged.Count - 1].z) merged[merged.Count - 1] = (merged[merged.Count - 1].s, z);
                    continue;
                }
                merged.Add((s, z));
            }
            // Drop points that lie on the straight line between their neighbours.
            for (int i = merged.Count - 2; i >= 1; i--)
            {
                (double sa, double za) = merged[i - 1];
                (double sb, double zb) = merged[i + 1];
                double expected = za + (zb - za) * (merged[i].s - sa) / (sb - sa);
                if (Math.Abs(expected - merged[i].z) < 0.0005) merged.RemoveAt(i);
            }
            return merged;
        }

        /// <summary>At every station any route has, the highest top among the routes covering it.</summary>
        private static List<(double s, double z)> Highest(List<List<(double s, double z)>> lines)
        {
            double At(List<(double s, double z)> l, double s)
            {
                if (s < l[0].s - SameStation || s > l[l.Count - 1].s + SameStation) return double.NaN;
                for (int i = 1; i < l.Count; i++)
                    if (s <= l[i].s + SameStation)
                    {
                        double t = l[i].s - l[i - 1].s < 1e-9 ? 0 : (s - l[i - 1].s) / (l[i].s - l[i - 1].s);
                        return l[i - 1].z + (l[i].z - l[i - 1].z) * Math.Max(0, Math.Min(1, t));
                    }
                return l[l.Count - 1].z;
            }
            List<(double s, double z)> all = new List<(double, double)>();
            foreach (double s in lines.SelectMany(l => l.Select(x => x.s)).Distinct().OrderBy(x => x))
            {
                double z = lines.Select(l => At(l, s)).Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.NaN).Max();
                if (!double.IsNaN(z)) all.Add((s, z));
            }
            return Clean(all, double.MinValue, double.MaxValue);
        }
    }
}
