using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;

namespace C3DProTools.ProfileTool
{
    public sealed class OperationReport
    {
        public int Done;
        public int Skipped;
        public List<string> Notes = new();

        public string Summary(string verb)
        {
            string s = $"{verb}: {Done}" + (Skipped > 0 ? $", skipped: {Skipped}" : "");
            if (Notes.Count > 0)
                s += Environment.NewLine + string.Join(Environment.NewLine, Notes.Take(25))
                   + (Notes.Count > 25 ? $"{Environment.NewLine}... and {Notes.Count - 25} more" : "");
            return s;
        }
    }

    public sealed class CreateOptions
    {
        public ObjectId SurfaceId;
        public string SurfaceName;
        public ObjectId StyleId;
        public ObjectId LabelSetId;
        /// <summary>Null = current layer.</summary>
        public ObjectId LayerId;
        /// <summary>Tokens: {alignment}, {surface}.</summary>
        public string NameTemplate = "{alignment}_{surface}";
        public bool SkipExisting = true;
    }

    public static class ProfileOperations
    {
        /// <summary>Erases the given profiles, one transaction, each failure reported and skipped.</summary>
        public static OperationReport Delete(Document doc, IEnumerable<ProfileRow> rows)
        {
            var report = new OperationReport();
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var row in rows)
                {
                    try
                    {
                        if (row.ProfileId.IsNull || row.ProfileId.IsErased) { report.Skipped++; continue; }
                        var p = (Profile)tr.GetObject(row.ProfileId, OpenMode.ForWrite);
                        p.Erase();
                        report.Done++;
                    }
                    catch (Exception ex)
                    {
                        report.Skipped++;
                        report.Notes.Add($"{row.Alignment} / {row.Profile}: {ex.Message}");
                    }
                }
                tr.Commit();
            }
            return report;
        }

        /// <summary>
        /// Creates a surface profile from one surface on every alignment. Alignments the surface does not cover
        /// get no profile (the empty one Civil 3D makes is erased again).
        /// </summary>
        public static OperationReport CreateOnAll(Document doc, ScanResult scan, CreateOptions o)
        {
            var report = new OperationReport();
            var db = doc.Database;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                ObjectId layerId = o.LayerId.IsNull ? db.Clayer : o.LayerId;

                foreach (ObjectId aid in scan.AlignmentIds)
                {
                    if (aid.IsErased || tr.GetObject(aid, OpenMode.ForRead) is not Alignment al) continue;

                    if (o.SkipExisting && scan.Rows.Any(r => r.AlignmentId == aid && r.IsFrom(o.SurfaceName, true)))
                    {
                        report.Skipped++;
                        continue;
                    }

                    var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId pid in al.GetProfileIds())
                        if (tr.GetObject(pid, OpenMode.ForRead) is Profile ep) taken.Add(ep.Name);

                    string name = UniqueName(MakeName(o.NameTemplate, al.Name, o.SurfaceName), taken);
                    try
                    {
                        ObjectId pid = Profile.CreateFromSurface(name, aid, o.SurfaceId, layerId, o.StyleId, o.LabelSetId);
                        var p = (Profile)tr.GetObject(pid, OpenMode.ForRead);
                        if (p.Entities.Count == 0)
                        {
                            p.UpgradeOpen();
                            p.Erase();
                            report.Skipped++;
                            report.Notes.Add($"{al.Name}: surface does not cover it, no profile");
                            continue;
                        }
                        report.Done++;
                    }
                    catch (Exception ex)
                    {
                        report.Skipped++;
                        report.Notes.Add($"{al.Name}: {ex.Message}");
                    }
                }
                tr.Commit();
            }
            return report;
        }

        private static string MakeName(string template, string alignment, string surface)
        {
            string t = string.IsNullOrWhiteSpace(template) ? "{alignment}_{surface}" : template;
            return t.Replace("{alignment}", alignment, StringComparison.OrdinalIgnoreCase)
                    .Replace("{surface}", surface, StringComparison.OrdinalIgnoreCase);
        }

        private static string UniqueName(string name, HashSet<string> taken)
        {
            if (!taken.Contains(name)) return name;
            for (int i = 1; ; i++)
            {
                string n = $"{name} ({i})";
                if (!taken.Contains(n)) return n;
            }
        }
    }
}
