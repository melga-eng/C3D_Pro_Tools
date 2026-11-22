using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using C3DProTools.NavisLink;
using Color = Autodesk.AutoCAD.Colors.Color;

namespace C3DProTools
{
    /// <summary>A clash area in the drawing's coordinates (metres): its corners after Navis Import's shift and turn.</summary>
    internal sealed class ClashCloud
    {
        public string Name = "", Status = "", Test = "";
        public int Clashes;
        public List<(double X, double Y)> Corners = new List<(double X, double Y)>();
    }

    /// <summary>
    /// Revision clouds around the clash areas the Clash Grouper sent with a selection (2026-11-22): one per group or single
    /// clash, on Z-Zz_10_40-M-ClashClouds (Uniclass Zz_10_40 "Hold clouds and notation", not plotted), named with the group,
    /// its status and test. A cloud of the same test and name drawn before is replaced.
    /// </summary>
    internal static class ClashClouds
    {
        public const string Layer = "Z-Zz_10_40-M-ClashClouds";
        private const string App = "C3DPRO_CLASH";
        /// <summary>Space around the clashes (m).</summary>
        public const double Margin = 2.0;

        /// <summary>The sent areas grown by the margin and moved by the import's shift and turn.</summary>
        public static List<ClashCloud> From(IEnumerable<NavisClashArea> areas, NavisTransform t)
        {
            var list = new List<ClashCloud>();
            foreach (NavisClashArea a in areas ?? Enumerable.Empty<NavisClashArea>())
            {
                if (a == null || a.MaxX < a.MinX || a.MaxY < a.MinY) continue;
                list.Add(new ClashCloud
                {
                    Name = a.Name ?? "", Status = a.Status ?? "", Test = a.Test ?? "", Clashes = a.Clashes,
                    Corners = ClashCloudShape.Box(a.MinX, a.MinY, a.MaxX, a.MaxY, Margin).Select(c => { V3 p = t.Point(new V3(c.X, c.Y, a.Z)); return (p.X, p.Y); }).ToList()
                });
            }
            return list;
        }

        /// <summary>Draws the clouds and their names (drawing units from <paramref name="scale"/>, units per metre); returns how many.</summary>
        public static int Draw(Database db, IReadOnlyList<ClashCloud> clouds, double scale)
        {
            if (clouds.Count == 0) return 0;
            int n = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId layer = EnsureLayer(tr, db);
                RegAppTable apps = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
                if (!apps.Has(App))
                {
                    apps.UpgradeOpen();
                    var app = new RegAppTableRecord { Name = App };
                    apps.Add(app);
                    tr.AddNewlyCreatedDBObject(app, true);
                }
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                var tags = new HashSet<string>(clouds.Select(Tag));
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e) || e.LayerId != layer) continue;
                    using (ResultBuffer rb = e.GetXDataForApplication(App))
                        if (rb != null && rb.AsArray().Any(v => v.Value is string s && tags.Contains(s))) { e.UpgradeOpen(); e.Erase(); }
                }
                foreach (ClashCloud c in clouds)
                {
                    List<(double X, double Y)> corners = c.Corners.Select(p => (p.X * scale, p.Y * scale)).ToList();
                    var outline = ClashCloudShape.Outline(corners, ClashCloudShape.ArcFor(corners));
                    var pl = new Polyline { LayerId = layer, Closed = true };
                    for (int i = 0; i < outline.Count; i++) pl.AddVertexAt(i, new Point2d(outline[i].X, outline[i].Y), outline[i].B, 0, 0);
                    ms.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    pl.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, App), new TypedValue((int)DxfCode.ExtendedDataAsciiString, Tag(c)));

                    // The name inside the top left corner, sized to the cloud.
                    double w = corners.Max(p => p.X) - corners.Min(p => p.X), h = corners.Max(p => p.Y) - corners.Min(p => p.Y);
                    double height = Math.Min(2.5 * scale, Math.Max(0.4 * scale, Math.Min(w, h) / 10));
                    var text = new MText
                    {
                        LayerId = layer, TextHeight = height, Attachment = AttachmentPoint.TopLeft,
                        Location = new Point3d(corners.Min(p => p.X) + height, corners.Max(p => p.Y) - height, 0),
                        Contents = $"{c.Name}\\P{c.Status} · {c.Test}"
                    };
                    ms.AppendEntity(text);
                    tr.AddNewlyCreatedDBObject(text, true);
                    text.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, App), new TypedValue((int)DxfCode.ExtendedDataAsciiString, Tag(c)));
                    n++;
                }
                tr.Commit();
            }
            return n;
        }

        private static string Tag(ClashCloud c) => "clash:" + c.Test + "|" + c.Name;

        private static ObjectId EnsureLayer(Transaction tr, Database db)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(Layer)) return lt[Layer];
            lt.UpgradeOpen();
            var l = new LayerTableRecord { Name = Layer, Color = Color.FromColorIndex(ColorMethod.ByAci, 1), IsPlottable = false };
            ObjectId id = lt.Add(l);
            tr.AddNewlyCreatedDBObject(l, true);
            return id;
        }
    }
}
