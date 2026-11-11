using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace C3DProTools.ProfileTool
{
    /// <summary>One profile found on one alignment.</summary>
    public sealed class ProfileRow
    {
        public ObjectId ProfileId;
        public ObjectId AlignmentId;
        public string Alignment;
        public string Profile;
        public ProfileType Type;
        /// <summary>Resolved source surface name; empty when not a surface profile or no match.</summary>
        public string Surface = "";
        /// <summary>How the surface was found: geometry, geometry + name, name only, ambiguous, none.</summary>
        public string Match = "";
        /// <summary>Every surface the profile's elevations agree with (more than one when surfaces overlap, e.g. pasted).</summary>
        public List<string> Candidates = new();
        /// <summary>Surface profile with no data (surface does not cover the alignment).</summary>
        public bool IsEmpty;
        /// <summary>Another profile on the same alignment already comes from the same surface.</summary>
        public bool IsDuplicate;
        public string Note => IsEmpty ? "empty" : IsDuplicate ? "duplicate" : "";
        public bool IsAmbiguous => Match == MatchAmbiguous;
        public bool IsSurfaceProfile => Type == ProfileType.EG;

        public const string MatchAmbiguous = "ambiguous";

        /// <summary>True if this profile comes from the named surface, or could (ambiguous) when includeAmbiguous.</summary>
        public bool IsFrom(string surface, bool includeAmbiguous)
        {
            if (string.Equals(Surface, surface, StringComparison.OrdinalIgnoreCase)) return true;
            return includeAmbiguous && IsAmbiguous && Candidates.Contains(surface, StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class SurfaceInfo
    {
        public ObjectId Id;
        public string Name;
    }

    public sealed class ScanResult
    {
        public List<ProfileRow> Rows = new();
        public List<SurfaceInfo> Surfaces = new();
        public List<ObjectId> AlignmentIds = new();
    }

    /// <summary>
    /// Reads every alignment's profiles. The .NET API does not expose which surface a surface (EG) profile was
    /// sampled from (Profile has no surface property; COM returns null), so the source surface is found by geometry:
    /// the profile's elevation is compared with each surface's elevation under the alignment at sample stations.
    /// When several surfaces agree (one pasted into another), the profile name breaks the tie.
    /// </summary>
    public static class ProfileScanner
    {
        private const int Samples = 15;
        private const double Tolerance = 0.01;   // drawing units (m)
        private const int MinHits = 3;

        public static ScanResult Scan(Database db, CivilDocument civil)
        {
            var result = new ScanResult();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var surfaces = new List<CivilSurface>();
                foreach (ObjectId sid in civil.GetSurfaceIds())
                {
                    if (tr.GetObject(sid, OpenMode.ForRead) is CivilSurface s)
                    {
                        surfaces.Add(s);
                        result.Surfaces.Add(new SurfaceInfo { Id = sid, Name = s.Name });
                    }
                }
                result.Surfaces.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

                foreach (ObjectId aid in civil.GetAlignmentIds())
                {
                    if (tr.GetObject(aid, OpenMode.ForRead) is not Alignment al) continue;
                    result.AlignmentIds.Add(aid);
                    foreach (ObjectId pid in al.GetProfileIds())
                    {
                        if (tr.GetObject(pid, OpenMode.ForRead) is not Profile p) continue;
                        var row = new ProfileRow
                        {
                            ProfileId = pid,
                            AlignmentId = aid,
                            Alignment = al.Name,
                            Profile = p.Name,
                            Type = p.ProfileType,
                        };
                        if (p.ProfileType == ProfileType.EG)
                        {
                            row.IsEmpty = p.Entities.Count == 0;
                            if (row.IsEmpty)
                            {
                                // Nothing to sample; the name is all there is.
                                row.Surface = LongestNameIn(row.Profile, surfaces.Select(s => s.Name)) ?? "";
                                row.Match = row.Surface == "" ? "none" : "name only";
                            }
                            else
                            {
                                ResolveSurface(row, al, p, surfaces);
                            }
                        }
                        result.Rows.Add(row);
                    }
                }
                tr.Commit();
            }
            MarkDuplicates(result.Rows);
            return result;
        }

        /// <summary>Per alignment and surface, every non-empty surface profile after the first is a duplicate.</summary>
        private static void MarkDuplicates(List<ProfileRow> rows)
        {
            var groups = rows.Where(r => r.IsSurfaceProfile && !r.IsEmpty && !r.IsAmbiguous && r.Surface != "")
                             .GroupBy(r => (r.AlignmentId, r.Surface.ToUpperInvariant()));
            foreach (var g in groups)
                foreach (var r in g.OrderBy(r => r.Profile.Length).ThenBy(r => r.Profile, StringComparer.OrdinalIgnoreCase).Skip(1))
                    r.IsDuplicate = true;
        }

        private static void ResolveSurface(ProfileRow row, Alignment al, Profile p, List<CivilSurface> surfaces)
        {
            double s0, s1;
            try { s0 = p.StartingStation; s1 = p.EndingStation; }
            catch { row.Match = "none"; return; }

            // Sample the profile once, then test each surface at the same plan points.
            var pts = new List<(double x, double y, double z)>();
            for (int i = 1; i <= Samples; i++)
            {
                double st = s0 + (s1 - s0) * i / (Samples + 1.0);
                try
                {
                    double z = p.ElevationAt(st);
                    double x = 0, y = 0;
                    al.PointLocation(st, 0, ref x, ref y);
                    pts.Add((x, y, z));
                }
                catch { /* gap in the profile */ }
            }

            foreach (var s in surfaces)
            {
                int tested = 0, hits = 0;
                foreach (var pt in pts)
                {
                    double se;
                    try { se = s.FindElevationAtXY(pt.x, pt.y); }
                    catch { continue; } // outside the surface
                    tested++;
                    if (Math.Abs(se - pt.z) <= Tolerance) hits++;
                }
                if (hits >= Math.Min(MinHits, pts.Count) && hits > 0 && hits >= 0.9 * tested)
                    row.Candidates.Add(s.Name);
            }

            if (row.Candidates.Count == 1)
            {
                row.Surface = row.Candidates[0];
                row.Match = "geometry";
                return;
            }

            if (row.Candidates.Count > 1)
            {
                string byName = LongestNameIn(row.Profile, row.Candidates);
                if (byName != null)
                {
                    row.Surface = byName;
                    row.Match = "geometry + name";
                }
                else
                {
                    row.Surface = string.Join(" / ", row.Candidates);
                    row.Match = ProfileRow.MatchAmbiguous;
                }
                return;
            }

            // No geometric match (surface rebuilt/changed since, or profile out of date): fall back to the name.
            string nameOnly = LongestNameIn(row.Profile, surfaces.Select(s => s.Name));
            if (nameOnly != null)
            {
                row.Surface = nameOnly;
                row.Match = "name only";
            }
            else
            {
                row.Match = "none";
            }
        }

        /// <summary>The longest surface name contained in the profile name, if exactly one is longest.</summary>
        private static string LongestNameIn(string profileName, IEnumerable<string> surfaceNames)
        {
            var hits = surfaceNames
                .Where(n => !string.IsNullOrEmpty(n) && profileName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(n => n.Length)
                .ToList();
            if (hits.Count == 0) return null;
            if (hits.Count > 1 && hits[0].Length == hits[1].Length) return null;
            return hits[0];
        }
    }
}
