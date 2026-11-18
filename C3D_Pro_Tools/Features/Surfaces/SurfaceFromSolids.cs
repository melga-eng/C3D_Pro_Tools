using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.BoundaryRepresentation;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.Settings;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;
using Face = Autodesk.AutoCAD.DatabaseServices.Face;
using BrepFace = Autodesk.AutoCAD.BoundaryRepresentation.Face;
using Vertex = Autodesk.AutoCAD.BoundaryRepresentation.Vertex;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using SurfaceBoundaryType = Autodesk.Civil.SurfaceBoundaryType;

[assembly: CommandClass(typeof(C3DProTools.Features.Surfaces.SurfaceFromSolids))]

// Surface from Solids (2026-11-18): a TIN surface made of the triangles of selected 3D solids and meshes (a road as solids,
// its daylight slopes as a mesh): their top faces, their bottom faces, or a surface for each. The objects' own triangles are
// kept (3D faces added with their edges kept), and an outline boundary stops the surface bridging gaps between them. The
// objects are only read. One surface per side; name and style from the drawing's defaults, which the user can change.
namespace C3DProTools.Features.Surfaces
{
    public sealed class SurfaceFromSolids
    {
        internal sealed class Settings
        {
            public List<ObjectId> Objects = new List<ObjectId>();
            public bool Top = true, Bottom;
            public string Name = "";
            public string Style = "";
        }

        [CommandMethod("C3DPRO", "C3DPROSURFSOLID", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Run()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "3DSOLID,MESH,POLYLINE") });
            PromptSelectionResult sel = ed.SelectImplied();
            if (sel.Status != PromptStatus.OK || sel.Value.Count == 0)
                sel = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect 3D solids and meshes for the surface: " }, filter);
            if (sel.Status != PromptStatus.OK) return;
            var s = new Settings { Objects = sel.Value.GetObjectIds().ToList() };

            var kw = new PromptKeywordOptions("\nSurface of") { AllowNone = true };
            kw.Keywords.Add("Top"); kw.Keywords.Add("Bottom"); kw.Keywords.Add("Both");
            kw.Keywords.Default = "Top";
            PromptResult kr = ed.GetKeywords(kw);
            if (kr.Status != PromptStatus.OK && kr.Status != PromptStatus.None) return;
            string side = string.IsNullOrEmpty(kr.StringResult) ? "Top" : kr.StringResult;
            s.Top = side != "Bottom"; s.Bottom = side != "Top";

            (string defName, string defStyle) = Defaults(doc, s.Objects);
            PromptResult nr = ed.GetString(new PromptStringOptions($"\nSurface name <{defName}>: ") { AllowSpaces = true });
            if (nr.Status != PromptStatus.OK) return;
            s.Name = nr.StringResult.Trim().Length > 0 ? nr.StringResult.Trim() : defName;
            PromptResult st = ed.GetString(new PromptStringOptions($"\nSurface style <{defStyle}>: ") { AllowSpaces = true });
            if (st.Status != PromptStatus.OK) return;
            s.Style = st.StringResult.Trim().Length > 0 ? st.StringResult.Trim() : defStyle;

            var log = new StringBuilder();
            try { Execute(doc, s, log); }
            catch (Exception ex)
            {
                C3DProLogger.Error("C3DPROSURFSOLID failed", ex);
                log.AppendLine($"C3DPROSURFSOLID failed: {ex.Message}. Nothing was changed.");
            }
            ed.WriteMessage("\n" + RunReport.Write(log.ToString(), "C3DPROSURFSOLID", side) + "\n");
        }

        /// <summary>The default name (the objects' layer) and the drawing's default surface style.</summary>
        internal static (string name, string style) Defaults(Document doc, IEnumerable<ObjectId> ids)
        {
            string layer = "", style = "";
            using (Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                ObjectId first = ids.FirstOrDefault();
                if (!first.IsNull && tr.GetObject(first, OpenMode.ForRead) is Entity e) layer = e.Layer;
                try
                {
                    CivilDocument civil = CivilDocument.GetCivilDocument(doc.Database);
                    ObjectId sid = civil.Settings.GetSettings<SettingsCmdCreateSurface>().Styles.SurfaceStyleId.Value;
                    if (!sid.IsNull && tr.GetObject(sid, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.StyleBase sb) style = sb.Name;
                    if (style.Length == 0 && civil.Styles.SurfaceStyles.Count > 0) style = ((Autodesk.Civil.DatabaseServices.Styles.StyleBase)tr.GetObject(civil.Styles.SurfaceStyles[0], OpenMode.ForRead)).Name;
                }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                tr.Commit();
            }
            return (layer.Length > 0 ? layer : "Surface from solids", style);
        }

        // ---- Reading the triangles ----

        private static P3 P(Point3d p) => new P3(p.X, p.Y, p.Z);

        /// <summary>The triangles of one object, outward, and whether it is closed; null for an object that is not a solid or mesh.</summary>
        internal static (List<Tri> tris, bool closed, string kind)? Read(Transaction tr, Entity e)
        {
            switch (e)
            {
                case Solid3d solid:
                {
                    List<Tri> st = SolidTriangles(solid, out int odd);
                    return (st, true, odd > 0 ? $"3D solid (warning: {odd} faces curved or with an opening: the surface follows their corners only)" : "3D solid");
                }
                case SubDMesh mesh:
                {
                    var v = mesh.Vertices.Cast<Point3d>().Select(P).ToList();
                    var tris = new List<Tri>();
                    Int32Collection f = mesh.FaceArray;
                    for (int i = 0; i < f.Count;)
                    {
                        int n = f[i];
                        var poly = Enumerable.Range(1, n).Select(k => v[f[i + k]]).ToList();
                        tris.AddRange(poly.Count == 3 ? new List<Tri> { new Tri(poly[0], poly[1], poly[2]) } : TriangulateAnyWay(poly));
                        i += n + 1;
                    }
                    bool closedMesh = MeshTriangles.IsClosed(tris);
                    return (closedMesh ? MeshTriangles.Outward(tris) : tris, closedMesh, "mesh");
                }
                case PolyFaceMesh pf:
                {
                    var v = new List<P3>();
                    var tris = new List<Tri>();
                    foreach (ObjectId id in pf)
                    {
                        DBObject o = tr.GetObject(id, OpenMode.ForRead);
                        if (o is PolyFaceMeshVertex pv) v.Add(P(pv.Position));
                        else if (o is FaceRecord fr)
                        {
                            var idx = Enumerable.Range(0, 4).Select(k => Math.Abs((int)fr.GetVertexAt((short)k))).Where(k => k > 0).ToList();
                            var poly = idx.Distinct().Where(k => k <= v.Count).Select(k => v[k - 1]).ToList();
                            if (poly.Count == 3) tris.Add(new Tri(poly[0], poly[1], poly[2]));
                            else if (poly.Count == 4) { tris.Add(new Tri(poly[0], poly[1], poly[2])); tris.Add(new Tri(poly[0], poly[2], poly[3])); }
                        }
                    }
                    bool closedPf = MeshTriangles.IsClosed(tris);
                    return (closedPf ? MeshTriangles.Outward(tris) : tris, closedPf, "polyface mesh");
                }
                default: return null;
            }
        }

        /// <summary>A polygon of a mesh in its own order (a fan when it is not ear-clipped in plan, e.g. vertical).</summary>
        private static List<Tri> TriangulateAnyWay(List<P3> poly)
        {
            List<Tri> t = MeshTriangles.Triangulate(poly);
            if (t.Count > 0) return t;
            return Enumerable.Range(1, poly.Count - 2).Select(k => new Tri(poly[0], poly[k], poly[k + 1])).ToList();
        }

        /// <summary>
        /// A solid's faces as triangles, each turned to face outward: tested by a point just off the face, which must be
        /// outside the solid (the loop order of a face is not relied on).
        /// </summary>
        private static List<Tri> SolidTriangles(Solid3d solid, out int odd)
        {
            var result = new List<Tri>();
            odd = 0;
            using (var brep = new Brep(solid))
            {
                foreach (BrepFace face in brep.Faces)
                {
                    List<P3> poly = null;
                    foreach (BoundaryLoop loop in face.Loops)
                    {
                        if (loop.LoopType != LoopType.LoopExterior) continue;
                        poly = loop.Vertices.Cast<Vertex>().Select(v => P(v.Point)).ToList();
                        break;
                    }
                    if (poly == null || poly.Count < 3) continue;
                    List<Tri> tris = MeshTriangles.Triangulate(poly);
                    if (tris.Count == 0) continue;  // a vertical face: a side
                    // A face whose own area is not that of its corners' triangles is curved or has an opening (review finding):
                    // the surface would cut across it, so it is reported.
                    try
                    {
                        double real = face.GetArea(), flat = tris.Sum(MeshTriangles.Area3d);
                        if (Math.Abs(real - flat) > Math.Max(1e-4, real * 0.01)) odd++;
                    }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                    // Outward: 5 mm off the first triangle's centre along its normal must be outside.
                    Tri t0 = tris[0];
                    double cx = (t0.A.X + t0.B.X + t0.C.X) / 3, cy = (t0.A.Y + t0.B.Y + t0.C.Y) / 3, cz = (t0.A.Z + t0.B.Z + t0.C.Z) / 3;
                    Vector3d n = new Vector3d(t0.B.X - t0.A.X, t0.B.Y - t0.A.Y, t0.B.Z - t0.A.Z).CrossProduct(new Vector3d(t0.C.X - t0.A.X, t0.C.Y - t0.A.Y, t0.C.Z - t0.A.Z));
                    bool flip = false;
                    if (n.Length > 1e-12)
                    {
                        Point3d probe = new Point3d(cx, cy, cz) + n.GetNormal() * 0.005;
                        try
                        {
                            using (BrepEntity hit = brep.GetPointContainment(probe, out PointContainment pc))
                                flip = pc == PointContainment.Inside;
                        }
                        catch (Exception ex) { C3DProLogger.Warn("Surface from solids: a face's side could not be tested", ex); }
                    }
                    result.AddRange(flip ? tris.Select(t => new Tri(t.A, t.C, t.B)) : tris);
                }
            }
            return result;
        }

        // ---- Making the surface ----

        internal static void Execute(Document doc, Settings s, StringBuilder log)
        {
            Database db = doc.Database;
            var top = new List<Tri>();
            var bottom = new List<Tri>();
            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in s.Objects)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                    (List<Tri> tris, bool closed, string kind)? r;
                    try { r = Read(tr, e); }
                    catch (Exception ex) { log.AppendLine($"  {id.ObjectClass.DxfName} {id.Handle}: not read ({ex.Message})"); continue; }
                    if (r == null) { log.AppendLine($"  {id.ObjectClass.DxfName} {id.Handle}: not a solid or mesh, skipped"); continue; }
                    List<Tri> t = MeshTriangles.Side(r.Value.tris, r.Value.closed, SurfaceSide.Top), b = MeshTriangles.Side(r.Value.tris, r.Value.closed, SurfaceSide.Bottom);
                    top.AddRange(t); bottom.AddRange(b);
                    log.AppendLine($"  {r.Value.kind} {id.Handle} on {e.Layer}: {r.Value.tris.Count} faces ({(r.Value.closed ? "closed" : "open sheet")}), {t.Count} top, {b.Count} bottom");
                }

                CivilDocument civil = CivilDocument.GetCivilDocument(db);
                ObjectId styleId = civil.Styles.SurfaceStyles.Cast<ObjectId>()
                    .FirstOrDefault(id => ((Autodesk.Civil.DatabaseServices.Styles.StyleBase)tr.GetObject(id, OpenMode.ForRead)).Name.Equals(s.Style, StringComparison.OrdinalIgnoreCase));
                if (styleId.IsNull) throw new InvalidOperationException($"there is no surface style \"{s.Style}\"");

                bool both = s.Top && s.Bottom;
                int made = 0;
                if (s.Top) made += Make(tr, db, civil, both ? s.Name + " - Top" : s.Name, styleId, top, "top", log) ? 1 : 0;
                if (s.Bottom) made += Make(tr, db, civil, both ? s.Name + " - Bottom" : s.Name, styleId, bottom, "bottom", log) ? 1 : 0;
                if (made == 0) { tr.Abort(); log.AppendLine("No surface made (see above). Nothing was changed."); return; }
                tr.Commit();
            }
        }

        /// <summary>One TIN surface from the triangles: 3D faces with their edges kept, then erased; the outline as boundaries.</summary>
        private static bool Make(Transaction tr, Database db, CivilDocument civil, string name, ObjectId styleId, List<Tri> tris, string side, StringBuilder log)
        {
            if (tris.Count == 0) { log.AppendLine($"No {side} faces in the objects selected: no {side} surface."); return false; }
            name = Unique(tr, civil, name);
            ObjectId sid = TinSurface.Create(db, name);
            var surf = (TinSurface)tr.GetObject(sid, OpenMode.ForWrite);
            surf.StyleId = styleId;

            var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
            var faces = new ObjectIdCollection();
            foreach (Tri t in tris)
            {
                var f = new Face(new Point3d(t.A.X, t.A.Y, t.A.Z), new Point3d(t.B.X, t.B.Y, t.B.Z), new Point3d(t.C.X, t.C.Y, t.C.Z), true, true, true, true);
                faces.Add(ms.AppendEntity(f));
                tr.AddNewlyCreatedDBObject(f, true);
            }
            surf.DrawingObjectsDefinition.AddFrom3DFaces(faces, true, $"C3D Pro: {side} of solids and meshes");

            foreach (ObjectId id in faces) tr.GetObject(id, OpenMode.ForWrite).Erase();
            surf.Rebuild();

            // Triangles the TIN adds between the objects (across gaps, openings and around the outside) are not theirs: an edge
            // that is none of the objects' edges is deleted, which removes them. Separate pieces and holes are kept as they are
            // (an outer boundary per piece does not work: Civil 3D uses one; review finding).
            (long, long, long, long) EdgeKey(Point3d p, Point3d q)
            {
                long ax = (long)Math.Round(p.X * 1000), ay = (long)Math.Round(p.Y * 1000), bx = (long)Math.Round(q.X * 1000), by = (long)Math.Round(q.Y * 1000);
                return (ax, ay) .CompareTo((bx, by)) <= 0 ? (ax, ay, bx, by) : (bx, by, ax, ay);
            }
            Point3d Q(P3 p) => new Point3d(p.X, p.Y, p.Z);
            var own = new HashSet<(long, long, long, long)>();
            foreach (Tri t in tris) { own.Add(EdgeKey(Q(t.A), Q(t.B))); own.Add(EdgeKey(Q(t.B), Q(t.C))); own.Add(EdgeKey(Q(t.C), Q(t.A))); }
            var extra = new Dictionary<(long, long, long, long), TinSurfaceEdge>();
            foreach (TinSurfaceTriangle st in surf.GetTriangles(false))
                foreach (TinSurfaceEdge e in new[] { st.Edge1, st.Edge2, st.Edge3 })
                {
                    var k = EdgeKey(e.Vertex1.Location, e.Vertex2.Location);
                    if (!own.Contains(k) && !extra.ContainsKey(k)) extra[k] = e;
                }
            if (extra.Count > 0) { surf.DeleteLines(extra.Values); surf.Rebuild(); }

            int n = 0;
            try { n = surf.GetTriangles(false).Count; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            var props = surf.GetGeneralProperties();
            log.AppendLine($"Surface \"{name}\" ({side}): {tris.Count} faces given, {n} triangles ({extra.Count} edges between the objects removed); " +
                           $"elevations {props.MinimumElevation:0.000} to {props.MaximumElevation:0.000}.");
            // Fewer triangles than faces: objects overlap in plan, and a TIN keeps one height per point (review finding).
            if (n < tris.Count) log.AppendLine($"  warning: {tris.Count - n} faces are not in the surface: objects overlap in plan, so it is not a clean {side}. Select objects that do not overlap.");
            return true;
        }

        private static string Unique(Transaction tr, CivilDocument civil, string name)
        {
            var names = new HashSet<string>(civil.GetSurfaceIds().Cast<ObjectId>().Select(id => ((Autodesk.Civil.DatabaseServices.Surface)tr.GetObject(id, OpenMode.ForRead)).Name), StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(name)) return name;
            for (int i = 2; ; i++) if (!names.Contains($"{name} ({i})")) return $"{name} ({i})";
        }
    }
}
