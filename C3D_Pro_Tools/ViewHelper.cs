using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using System;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace C3DProTools
{
    /// <summary>Zooms the active viewport from code, including while one of the tools' dialogs is open.</summary>
    internal static class ViewHelper
    {
        /// <summary>Zooms so that a square of <paramref name="size"/> drawing units around the WCS point fills the view height.</summary>
        public static void ZoomTo(Point3d center, double size)
        {
            ZoomTo(new Extents3d(center - new Vector3d(size / 2.0, size / 2.0, 0), center + new Vector3d(size / 2.0, size / 2.0, 0)), 1.0, 0.0);
        }

        /// <summary>Zooms to WCS extents, scaled by <paramref name="margin"/> and never smaller than <paramref name="minSize"/>.</summary>
        public static void ZoomTo(Extents3d extents, double margin = 1.15, double minSize = 10.0)
        {
            Document acDoc = AcadApp.DocumentManager.MdiActiveDocument;
            Editor ed = acDoc.Editor;
            using (acDoc.LockDocument())
            using (ViewTableRecord view = ed.GetCurrentView())
            {
                // The view centre is in display coordinates, so map the WCS extents through the view's own frame.
                Matrix3d wcsToDcs = (Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                                     Matrix3d.Displacement(view.Target - Point3d.Origin) *
                                     Matrix3d.PlaneToWorld(view.ViewDirection)).Inverse();
                Point3d min = extents.MinPoint, max = extents.MaxPoint;
                Point3d first = min.TransformBy(wcsToDcs);
                // Seeded with a real corner: a default Extents3d already contains the origin.
                Extents3d dcs = new Extents3d(first, first);
                foreach (Point3d corner in new[] { new Point3d(max.X, min.Y, min.Z), new Point3d(min.X, max.Y, min.Z), max })
                    dcs.AddPoint(corner.TransformBy(wcsToDcs));

                double aspect = view.Width / view.Height;
                double width = dcs.MaxPoint.X - dcs.MinPoint.X;
                double height = dcs.MaxPoint.Y - dcs.MinPoint.Y;
                double viewHeight = Math.Max(minSize, Math.Max(height, width / aspect) * margin);

                view.CenterPoint = new Point2d((dcs.MinPoint.X + dcs.MaxPoint.X) / 2.0, (dcs.MinPoint.Y + dcs.MaxPoint.Y) / 2.0);
                view.Height = viewHeight;
                view.Width = viewHeight * aspect;
                ed.SetCurrentView(view);
            }
        }
    }
}
