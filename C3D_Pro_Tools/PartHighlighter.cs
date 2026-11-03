using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using System;
using System.Collections.Generic;
using Polyline = Autodesk.AutoCAD.DatabaseServices.Polyline;

namespace C3DProTools
{
    /// <summary>
    /// Draws temporary coloured markers over detected parts in plan (transient graphics: never saved in the drawing,
    /// not selectable, gone on Dispose). Green = along, orange = partial, yellow = crossing, grey = excluded.
    /// </summary>
    internal sealed class PartHighlighter : IDisposable
    {
        private const short ColorAlong = 3, ColorPartial = 30, ColorCrossing = 2, ColorExcluded = 8;
        // Marker sizes in drawing units (meters): pipe band width, and point-part ring radius / width.
        private const double PipeMarkerWidth = 0.3, RingRadius = 1.0, RingWidth = 0.25;

        private readonly IList<DetectedPart> _parts;
        private readonly List<Polyline> _markers = new List<Polyline>();
        private readonly IntegerCollection _allViewports = new IntegerCollection();

        public PartHighlighter(IList<DetectedPart> parts)
        {
            _parts = parts;
            TransientManager tm = TransientManager.CurrentTransientManager;
            foreach (DetectedPart part in parts)
            {
                Polyline marker = CreateMarker(part);
                marker.ColorIndex = ColorFor(part);
                _markers.Add(marker);
                tm.AddTransient(marker, TransientDrawingMode.DirectTopmost, 128, _allViewports);
            }
        }

        /// <summary>Recolours the marker of the part at <paramref name="index"/> after its Include flag changed.</summary>
        public void Refresh(int index)
        {
            _markers[index].ColorIndex = ColorFor(_parts[index]);
            TransientManager.CurrentTransientManager.UpdateTransient(_markers[index], _allViewports);
        }

        public void Dispose()
        {
            TransientManager tm = TransientManager.CurrentTransientManager;
            foreach (Polyline marker in _markers)
            {
                tm.EraseTransient(marker, _allViewports);
                marker.Dispose();
            }
            _markers.Clear();
        }

        private static short ColorFor(DetectedPart part)
        {
            if (!part.Include) return ColorExcluded;
            switch (part.Category)
            {
                case ProfilePartCategory.Partial: return ColorPartial;
                case ProfilePartCategory.Crossing: return ColorCrossing;
                default: return ColorAlong;
            }
        }

        private static Polyline CreateMarker(DetectedPart part)
        {
            Polyline pl = new Polyline();
            if (part.IsPoint)
            {
                // A closed two-vertex polyline with half-circle bulges is a ring that can carry a width.
                Point3d c = part.Path[0];
                pl.AddVertexAt(0, new Point2d(c.X - RingRadius, c.Y), 1.0, RingWidth, RingWidth);
                pl.AddVertexAt(1, new Point2d(c.X + RingRadius, c.Y), 1.0, RingWidth, RingWidth);
                pl.Closed = true;
            }
            else
            {
                for (int i = 0; i < part.Path.Count; i++)
                    pl.AddVertexAt(i, new Point2d(part.Path[i].X, part.Path[i].Y), 0, PipeMarkerWidth, PipeMarkerWidth);
            }
            return pl;
        }
    }
}
