using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.Linq;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace C3DProTools
{
    /// <summary>
    /// Marks where a profile view's alignment enters and leaves cover zones (closed polylines on chosen layers, as the Profile
    /// &amp; Cover Designer reads them): a vertical line over the graph at each station, with "Start of …" / "End of …" text.
    /// The marks go on their own layer; marking a view again replaces its earlier marks.
    /// </summary>
    internal static class ProfileViewZones
    {
        public const string Layer = "C3DPRO-PV-ZONES";
        /// <summary>Plan step (m) the alignment is sampled at before each zone edge is found to a millimetre.</summary>
        private const double SampleStep = 1.0;

        /// <summary>The zone outlines on these layers (comma-separated, * and ? allowed), arcs followed within 50 mm.</summary>
        public static List<IReadOnlyList<(double X, double Y)>> ReadZones(Transaction tr, Database db, string layers) =>
            CoverJobs.ReadZonePolygons(tr, db, new CoverRules { ZoneLayers = layers ?? "" }).Cast<IReadOnlyList<(double X, double Y)>>().ToList();

        /// <summary>Marks the zone edges in one view; returns how many edges were marked.</summary>
        public static int Mark(Transaction tr, Database db, ObjectId viewId, IReadOnlyList<IReadOnlyList<(double X, double Y)>> zones, string label)
        {
            ProfileView v = (ProfileView)tr.GetObject(viewId, OpenMode.ForRead);
            if (!(tr.GetObject(v.AlignmentId, OpenMode.ForRead) is Alignment al)) return 0;
            double s0 = v.StationStart, s1 = v.StationEnd, e0 = v.ElevationMin, e1 = v.ElevationMax;
            double xa = 0, ya = 0, xb = 0, yb = 0;
            if (!v.FindXYAtStationAndElevation(s0, e0, ref xa, ref ya) || !v.FindXYAtStationAndElevation(s1, e1, ref xb, ref yb)) return 0;
            Extents3d graph = new Extents3d(new Point3d(Math.Min(xa, xb), Math.Min(ya, yb), 0), new Point3d(Math.Max(xa, xb), Math.Max(ya, yb), 0));

            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
            ObjectId layer = EnsureLayer(tr, db);
            // Earlier marks of this view: on the marks layer, inside its graph.
            foreach (ObjectId id in ms)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e) || e.LayerId != layer || !(e is Line || e is DBText)) continue;
                Point3d at = e is Line l ? l.StartPoint : ((DBText)e).Position;
                if (at.X >= graph.MinPoint.X - 1e-6 && at.X <= graph.MaxPoint.X + 1e-6 && at.Y >= graph.MinPoint.Y - 1e-6 && at.Y <= graph.MaxPoint.Y + 1e-6)
                {
                    e.UpgradeOpen();
                    e.Erase();
                }
            }
            if (zones.Count == 0) return 0;

            (double X, double Y) At(double s)
            {
                double x = 0, y = 0;
                try { al.PointLocation(s, 0, ref x, ref y); } catch (Exception) { }
                return (x, y);
            }
            List<(double s0, double s1)> ranges = ProfileViewMath.ZoneRanges(s0, s1, SampleStep, At, zones);
            double height = Math.Max(0.1, (graph.MaxPoint.Y - graph.MinPoint.Y) * 0.025);
            int marked = 0;
            foreach ((double a, double b) in ranges)
            {
                if (a > s0 + 1e-3) { AddMark(tr, ms, v, layer, a, e0, e1, $"Start of {label}", height); marked++; }
                if (b < s1 - 1e-3) { AddMark(tr, ms, v, layer, b, e0, e1, $"End of {label}", height); marked++; }
            }
            return marked;
        }

        private static void AddMark(Transaction tr, BlockTableRecord ms, ProfileView v, ObjectId layer, double s, double e0, double e1, string text, double height)
        {
            double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            if (!v.FindXYAtStationAndElevation(s, e0, ref x0, ref y0) || !v.FindXYAtStationAndElevation(s, e1, ref x1, ref y1)) return;
            Line line = new Line(new Point3d(x0, y0, 0), new Point3d(x1, y1, 0)) { LayerId = layer };
            ms.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);
            // Upright along the line, just right of it, from the bottom of the graph.
            DBText label = new DBText
            {
                TextString = text, Height = height, Rotation = Math.PI / 2, LayerId = layer,
                Position = new Point3d(x0 + height * 1.2, y0 + height, 0)
            };
            ms.AppendEntity(label);
            tr.AddNewlyCreatedDBObject(label, true);
        }

        private static ObjectId EnsureLayer(Transaction tr, Database db)
        {
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(Layer)) return lt[Layer];
            lt.UpgradeOpen();
            LayerTableRecord rec = new LayerTableRecord { Name = Layer, Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 30) };
            ObjectId id = lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            return id;
        }
    }
}
