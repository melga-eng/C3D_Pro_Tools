using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using C3DProTools.Features.Networks.Civil3D;
using C3DProTools.Features.Sheets.Application;
using C3DProTools.Features.Sheets.Civil3D;
using C3DProTools.Features.Sheets.Contracts;

// Profile DIMs, the drawing side: reads the sheet scene, plans each view with ProfileDimPlanner, and draws the DIMs as vertical
// rotated dimensions in the views. Values are true metres: each DIM's linear factor undoes the view's vertical exaggeration, as
// the hand-drawn DIMs of the project do. Tool DIMs carry XData (C3DPRO_DIM) so a later run replaces them; hand-drawn DIMs are
// never touched.
namespace C3DProTools.ProfileTool
{
    internal sealed class ProfileDimSettings
    {
        public string DimStyle { get; set; } = "KSIA_For_Profile_Standard_1-100";
        public string Layer { get; set; } = "Z-Zz_20_10_55-T-Notes";
        public bool Cover { get; set; } = true;
        public bool Clearance { get; set; } = true;
        public bool CrossingsShownOnly { get; set; } = true;
        public bool AtStructureFaces { get; set; } = true;
        public double MajorGradeChangePct { get; set; } = 2.0;
        public double MergeWithinM { get; set; } = 5.0;
        public double MinSpacingM { get; set; } = 10.0;
        public double MinClearanceM { get; set; } = 0.30;
        public double OffsetM { get; set; } = 2.0;
        /// <summary>No tool DIM within this distance (m along the view) of a hand-drawn vertical DIM; 0 turns the check off.</summary>
        public double SkipNearHandDimM { get; set; } = 1.0;

        public ProfileDimOptions Options() => new ProfileDimOptions
        {
            Cover = Cover, Clearance = Clearance, CrossingsShownOnly = CrossingsShownOnly, OffsetM = OffsetM,
            Rules = new DimRules
            {
                MajorGradeChangePct = MajorGradeChangePct, MergeWithinM = MergeWithinM, MinSpacingM = MinSpacingM,
                AtStructureFaces = AtStructureFaces, MinClearanceM = MinClearanceM,
            },
        };

        private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3DProTools", "ProfileDims.json");

        public static ProfileDimSettings Load()
        {
            try
            {
                if (File.Exists(FilePath)) return JsonSerializer.Deserialize<ProfileDimSettings>(File.ReadAllText(FilePath)) ?? new ProfileDimSettings();
            }
            catch (Exception ex) { C3DProLogger.Warn("Profile DIMs: settings could not be read; defaults are used.", ex); }
            return new ProfileDimSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { C3DProLogger.Warn("Profile DIMs: settings could not be saved.", ex); }
        }
    }

    /// <summary>A vertical DIM already in a view: its station, and whether the tool drew it.</summary>
    internal sealed record ExistingDim(ObjectId Id, double StationM, bool Tool);

    /// <summary>One view's plan with what is already drawn in it.</summary>
    internal sealed class ViewDims
    {
        public ObjectId ViewId;
        public ViewDimPlan Plan;
        public string AlignmentName = "";
        public List<ExistingDim> Existing = new List<ExistingDim>();
        public Dictionary<string, string> PartNames = new Dictionary<string, string>();
        public int Tool => Existing.Count(e => e.Tool);
        public int Hand => Existing.Count(e => !e.Tool);
    }

    internal sealed record DimWriteResult(int Created, int Removed, int SkippedNearHand, List<string> Problems);

    internal static class ProfileDimsCivil3D
    {
        public const string XDataApp = "C3DPRO_DIM";

        /// <summary>Reads the drawing (read-only) and plans every profile view.</summary>
        public static List<ViewDims> Plan(Document doc, ProfileDimSettings settings, out List<OperationIssue> sceneIssues, ProfileRoleRules roleRules = null)
        {
            SheetScene scene = new SheetSceneCivil3D(roleRules: roleRules).Read(new SheetSceneRequest { Drawing = DrawingRefs3D.Identity(doc) }, CancellationToken.None);
            sceneIssues = scene.Issues.ToList();
            ProfileDimOptions options = settings.Options();
            Database db = doc.Database;
            List<ViewDims> list = new List<ViewDims>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                List<(ObjectId id, Extents3d box)> dims = VerticalDims(tr, db);
                foreach (ProfileViewSnapshot view in scene.ProfileViews.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (!DrawingRefs3D.TryResolve(db, view.Ref, out ObjectId viewId)) continue;
                    ViewDims vd = new ViewDims
                    {
                        ViewId = viewId, Plan = ProfileDimPlanner.PlanView(scene, view, options),
                        AlignmentName = scene.Alignments.FirstOrDefault(a => a.Ref == view.Alignment)?.Name ?? "",
                    };
                    if (tr.GetObject(viewId, OpenMode.ForRead) is ProfileView pv) vd.Existing = ExistingIn(tr, pv, dims);
                    foreach (ViewPart p in view.DrawnParts)
                        if (DrawingRefs3D.TryResolve(db, p.Part, out ObjectId pid) && tr.GetObject(pid, OpenMode.ForRead) is Part part)
                            vd.PartNames[p.Part.Handle] = part.Name;
                    list.Add(vd);
                }
                tr.Commit();
            }
            return list;
        }

        public static string SourceName(ViewDims vd, PlannedDim d) =>
            d.Reason == DimReasons.StructureFace && vd.PartNames.TryGetValue(d.SourceKey ?? "", out string n) ? n : d.SourceKey ?? "";

        /// <summary>The model's vertical rotated dimensions, with their extents.</summary>
        private static List<(ObjectId, Extents3d)> VerticalDims(Transaction tr, Database db)
        {
            List<(ObjectId, Extents3d)> list = new List<(ObjectId, Extents3d)>();
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                if (id.ObjectClass.DxfName != "DIMENSION") continue;
                if (!(tr.GetObject(id, OpenMode.ForRead) is RotatedDimension d)) continue;
                if (Math.Abs(Math.Sin(d.Rotation)) < 0.999) continue;
                list.Add((id, new Extents3d(d.XLine1Point, d.XLine1Point)));
            }
            return list;
        }

        private static List<ExistingDim> ExistingIn(Transaction tr, ProfileView pv, List<(ObjectId id, Extents3d box)> dims)
        {
            List<ExistingDim> found = new List<ExistingDim>();
            Extents3d box;
            try { box = pv.GeometricExtents; } catch (Exception) { return found; }
            string handle = pv.Handle.ToString();
            foreach ((ObjectId id, Extents3d at) in dims)
            {
                RotatedDimension d = (RotatedDimension)tr.GetObject(id, OpenMode.ForRead);
                string tagView = TagView(d);
                bool tool = tagView != null;
                if (tool && tagView != handle) continue;
                Point3d p = at.MinPoint;
                if (!tool && (p.X < box.MinPoint.X || p.X > box.MaxPoint.X || p.Y < box.MinPoint.Y || p.Y > box.MaxPoint.Y)) continue;
                double s = 0, z = 0;
                try { if (!pv.FindStationAndElevationAtXY(p.X, p.Y, ref s, ref z)) continue; }
                catch (Exception) { continue; }
                found.Add(new ExistingDim(id, s, tool));
            }
            return found;
        }

        private static string TagView(Autodesk.AutoCAD.DatabaseServices.DBObject o)
        {
            ResultBuffer rb = o.GetXDataForApplication(XDataApp);
            if (rb == null) return null;
            using (rb)
            {
                TypedValue[] v = rb.AsArray();
                return v.Length > 1 && v[1].TypeCode == (short)DxfCode.ExtendedDataAsciiString ? v[1].Value as string : "";
            }
        }

        /// <summary>Erases the tool DIMs of the views (hand-drawn DIMs stay).</summary>
        public static int Remove(Document doc, IEnumerable<ViewDims> views)
        {
            int removed = 0;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ViewDims vd in views)
                    foreach (ExistingDim e in vd.Existing.Where(e => e.Tool && !e.Id.IsErased))
                    {
                        tr.GetObject(e.Id, OpenMode.ForWrite).Erase();
                        removed++;
                    }
                tr.Commit();
            }
            return removed;
        }

        /// <summary>Draws the planned DIMs of the views, replacing the tool DIMs of an earlier run.</summary>
        public static DimWriteResult Write(Document doc, IEnumerable<ViewDims> views, ProfileDimSettings settings)
        {
            Database db = doc.Database;
            List<string> problems = new List<string>();
            int created = 0, removed = 0, skipped = 0;
            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DimStyleTable styles = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
                if (string.IsNullOrWhiteSpace(settings.DimStyle) || !styles.Has(settings.DimStyle))
                    throw new InvalidOperationException($"The dimension style \"{settings.DimStyle}\" is not in this drawing. Pick one of its styles.");
                ObjectId styleId = styles[settings.DimStyle];
                string layer = EnsureLayer(tr, db, settings.Layer);
                EnsureApp(tr, db);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                foreach (ViewDims vd in views)
                {
                    if (vd.ViewId.IsErased || !(tr.GetObject(vd.ViewId, OpenMode.ForRead) is ProfileView pv)) continue;
                    foreach (ExistingDim e in vd.Existing.Where(e => e.Tool && !e.Id.IsErased))
                    {
                        tr.GetObject(e.Id, OpenMode.ForWrite).Erase();
                        removed++;
                    }
                    List<double> hand = vd.Existing.Where(e => !e.Tool).Select(e => e.StationM).ToList();
                    foreach (PlacedDim placed in vd.Plan.Dims)
                    {
                        PlannedDim d = placed.Dim;
                        if (settings.SkipNearHandDimM > 0 && hand.Any(s => Math.Abs(s - d.StationM) <= settings.SkipNearHandDimM)) { skipped++; continue; }
                        double x1 = 0, y1 = 0, x2 = 0, y2 = 0, xo = 0, yo = 0;
                        if (!pv.FindXYAtStationAndElevation(d.StationM, d.LowerElevM, ref x1, ref y1)
                            || !pv.FindXYAtStationAndElevation(d.StationM, d.UpperElevM, ref x2, ref y2)
                            || !pv.FindXYAtStationAndElevation(d.StationM + placed.Side * settings.OffsetM, d.UpperElevM, ref xo, ref yo))
                        {
                            problems.Add($"{pv.Name}: station {d.StationM:F2} is outside the view's graph; DIM not drawn.");
                            continue;
                        }
                        // The drawn length over the true one is the vertical exaggeration; the linear factor undoes it.
                        double exaggeration = Math.Abs(y2 - y1) / Math.Max(1e-9, d.UpperElevM - d.LowerElevM);
                        RotatedDimension dim = new RotatedDimension(Math.PI / 2, new Point3d(x1, y1, 0), new Point3d(x2, y2, 0),
                            new Point3d(xo, (y1 + y2) / 2, 0), "", styleId);
                        dim.SetDatabaseDefaults(db);
                        dim.DimensionStyle = styleId;
                        dim.Layer = layer;
                        if (exaggeration > 1e-9) dim.Dimlfac = 1.0 / exaggeration;
                        ms.AppendEntity(dim);
                        tr.AddNewlyCreatedDBObject(dim, true);
                        dim.XData = new ResultBuffer(
                            new TypedValue((int)DxfCode.ExtendedDataRegAppName, XDataApp),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, pv.Handle.ToString()),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, d.Kind.ToString()),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, d.Reason ?? ""),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, d.SourceKey ?? ""),
                            new TypedValue((int)DxfCode.ExtendedDataReal, d.StationM));
                        dim.RecomputeDimensionBlock(true);
                        double shown = dim.Measurement;
                        if (Math.Abs(shown - d.ValueM) > 0.01)
                            problems.Add($"{pv.Name}: DIM at {d.StationM:F2} shows {shown:F3} but the value is {d.ValueM:F3}; check the view's exaggeration.");
                        created++;
                    }
                }
                tr.Commit();
            }
            return new DimWriteResult(created, removed, skipped, problems);
        }

        private static string EnsureLayer(Transaction tr, Database db, string name)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (string.IsNullOrWhiteSpace(name)) return "0";
            if (lt.Has(name)) return name;
            SymbolUtilityServices.ValidateSymbolName(name, false);
            lt.UpgradeOpen();
            LayerTableRecord rec = new LayerTableRecord { Name = name };
            lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            return name;
        }

        private static void EnsureApp(Transaction tr, Database db)
        {
            RegAppTable apps = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (apps.Has(XDataApp)) return;
            apps.UpgradeOpen();
            RegAppTableRecord rec = new RegAppTableRecord { Name = XDataApp };
            apps.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }

        public static List<string> DimStyles(Database db)
        {
            using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            DimStyleTable t = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
            List<string> names = t.Cast<ObjectId>().Select(id => ((DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name).ToList();
            tr.Commit();
            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<string> Layers(Database db)
        {
            using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            LayerTable t = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            List<string> names = t.Cast<ObjectId>().Select(id => ((LayerTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name).ToList();
            tr.Commit();
            return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
