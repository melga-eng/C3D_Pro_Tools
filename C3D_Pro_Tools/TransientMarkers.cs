using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using System;
using System.Collections.Generic;
using Polyline = Autodesk.AutoCAD.DatabaseServices.Polyline;

namespace C3DProTools
{
    /// <summary>
    /// Temporary on-screen markers (transient graphics): never saved in the drawing, not selectable, removed on Dispose.
    /// The markers belong to the drawing that was active when they were added; they are always erased from that drawing's
    /// transient manager, even after another drawing has become active (a modeless window must clear them before that
    /// drawing closes: see <see cref="Owner"/>).
    /// </summary>
    internal sealed class TransientMarkers : IDisposable
    {
        private readonly List<Entity> _markers = new List<Entity>();
        private readonly IntegerCollection _allViewports = new IntegerCollection();
        private TransientManager _manager;
        private static readonly List<Entity> NotErased = new List<Entity>();

        /// <summary>The drawing the markers are shown in, or null when there are none.</summary>
        public Autodesk.AutoCAD.ApplicationServices.Document Owner { get; private set; }

        /// <summary>Shows a copy of <paramref name="polyline"/> (with its bulges) in the given colour and width.</summary>
        public void AddPolyline(Polyline polyline, short colorIndex, double width)
        {
            Polyline copy = (Polyline)polyline.Clone();
            for (int i = 0; i < copy.NumberOfVertices; i++)
            {
                copy.SetStartWidthAt(i, width);
                copy.SetEndWidthAt(i, width);
            }
            Add(copy, colorIndex);
        }

        public void AddPath(IList<Point3d> points, short colorIndex, double width)
        {
            if (points.Count < 2) return;
            Polyline pl = new Polyline();
            for (int i = 0; i < points.Count; i++)
                pl.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0, width, width);
            Add(pl, colorIndex);
        }

        /// <summary>A ring (closed two-vertex polyline with half-circle bulges, so it can carry a width).</summary>
        public void AddRing(Point3d center, double radius, short colorIndex, double width)
        {
            Polyline pl = new Polyline();
            pl.AddVertexAt(0, new Point2d(center.X - radius, center.Y), 1.0, width, width);
            pl.AddVertexAt(1, new Point2d(center.X + radius, center.Y), 1.0, width, width);
            pl.Closed = true;
            Add(pl, colorIndex);
        }

        public void Clear()
        {
            foreach (Entity marker in _markers)
            {
                // Only a marker its drawing no longer holds may be disposed; one still drawn would be drawn from freed memory.
                // One that could not be erased is kept alive for the session (left to the garbage collector it would be freed all the same).
                if (_manager != null && _manager.EraseTransient(marker, _allViewports)) marker.Dispose();
                else lock (NotErased) NotErased.Add(marker);
            }
            _markers.Clear();
            _manager = null;
            Owner = null;
        }

        public void Dispose() => Clear();

        private void Add(Entity entity, short colorIndex)
        {
            TransientManager current = TransientManager.CurrentTransientManager;
            // Markers already shown in another drawing are taken away first: one set is only ever in one drawing.
            if (_manager != null && _manager.UnmanagedObject != current.UnmanagedObject) Clear();
            if (_manager == null)
            {
                _manager = current;
                Owner = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            }
            entity.ColorIndex = colorIndex;
            _markers.Add(entity);
            _manager.AddTransient(entity, TransientDrawingMode.DirectTopmost, 128, _allViewports);
        }
    }
}
