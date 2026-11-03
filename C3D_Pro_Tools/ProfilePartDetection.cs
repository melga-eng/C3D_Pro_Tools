using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

namespace C3DProTools
{
    internal enum ProfilePartCategory
    {
        /// <summary>The whole part lies within the tolerance of the alignment.</summary>
        Along,
        /// <summary>Only some of a pipe lies within the tolerance (for example a branch leaving the alignment).</summary>
        Partial,
        /// <summary>A pipe that crosses the alignment in plan; Civil 3D draws it as a crossing in the profile view.</summary>
        Crossing
    }

    internal sealed class DetectedPart
    {
        public ObjectId Id;
        public string Name;
        public string Kind;
        public string NetworkName;
        public ProfilePartCategory Category;
        public double StationMin, StationMax;
        public double MaxOffset;
        /// <summary>Station range formatted the way the alignment displays stations.</summary>
        public string StationText;
        /// <summary>Plan path of a pipe, or the single insertion point of a structure / fitting / appurtenance.</summary>
        public List<Point3d> Path;
        /// <summary>How many of the alignment's profile views already show this part.</summary>
        public int AlreadyShownIn;
        public bool Include;

        public bool IsPoint => Path.Count == 1;
    }

    /// <summary>Finds the gravity and pressure parts that lie along (or cross) an alignment in plan.</summary>
    internal static class ProfilePartDetection
    {
        // Pipes are checked at points no farther apart than this (drawing units), so a long straight pipe whose ends
        // sit on a curved alignment is still seen leaving it in the middle.
        private const double SampleSpacing = 5.0;
        private const int MaxSamples = 60;

        public static List<DetectedPart> Detect(Transaction tr, Alignment alignment, IEnumerable<ObjectId> networkIds,
            double tolerance, bool includeCrossings, ICollection<ObjectId> alignmentProfileViews)
        {
            // Cheap plan-box test first, so parts far from the alignment never reach the station/offset queries.
            Extents3d reach = alignment.GeometricExtents;
            reach = new Extents3d(
                new Point3d(reach.MinPoint.X - tolerance, reach.MinPoint.Y - tolerance, double.MinValue),
                new Point3d(reach.MaxPoint.X + tolerance, reach.MaxPoint.Y + tolerance, double.MaxValue));

            List<DetectedPart> found = new List<DetectedPart>();
            foreach (ObjectId netId in networkIds)
            {
                DBObject netObj = tr.GetObject(netId, OpenMode.ForRead);
                if (netObj is Network gravityNet)
                {
                    foreach (ObjectId id in gravityNet.GetPipeIds())
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Pipe pipe)) continue;
                        ArcInfo? arc = null;
                        if (pipe.SubEntityType == PipeSubEntityType.Curved && PipeArcGeometry.TryGetGravityArc(pipe, out ArcInfo a)) arc = a;
                        List<Point3d> path = PipeArcGeometry.SamplePath(pipe.StartPoint, pipe.EndPoint, arc, SampleSpacing, MaxSamples);
                        AddIfFound(found, ClassifyPath(alignment, path, reach, tolerance, includeCrossings),
                            id, pipe.Name, "Pipe", gravityNet.Name, path, CountShownIn(pipe.GetProfileViewsDisplayingMe(), alignmentProfileViews));
                    }
                    foreach (ObjectId id in gravityNet.GetStructureIds())
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Structure structure)) continue;
                        List<Point3d> path = new List<Point3d> { structure.Location };
                        AddIfFound(found, ClassifyPath(alignment, path, reach, tolerance, false),
                            id, structure.Name, "Structure", gravityNet.Name, path, CountShownIn(structure.GetProfileViewsDisplayingMe(), alignmentProfileViews));
                    }
                }
                else if (netObj is PressurePipeNetwork pressureNet)
                {
                    foreach (ObjectId id in pressureNet.GetPipeIds())
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is PressurePipe pipe)) continue;
                        ArcInfo? arc = null;
                        if (pipe.IsCurve && PipeArcGeometry.TryGetPressureArc(pipe, out ArcInfo a)) arc = a;
                        List<Point3d> path = PipeArcGeometry.SamplePath(pipe.StartPoint, pipe.EndPoint, arc, SampleSpacing, MaxSamples);
                        AddIfFound(found, ClassifyPath(alignment, path, reach, tolerance, includeCrossings),
                            id, pipe.Name, "Pressure Pipe", pressureNet.Name, path, CountShownIn(pipe.GetProfileViewsDisplayingMe(), alignmentProfileViews));
                    }
                    foreach (ObjectId id in pressureNet.GetFittingIds())
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is PressureFitting fitting)) continue;
                        List<Point3d> path = new List<Point3d> { fitting.Location };
                        AddIfFound(found, ClassifyPath(alignment, path, reach, tolerance, false),
                            id, fitting.Name, "Fitting", pressureNet.Name, path, CountShownIn(fitting.GetProfileViewsDisplayingMe(), alignmentProfileViews));
                    }
                    foreach (ObjectId id in pressureNet.GetAppurtenanceIds())
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is PressureAppurtenance appurtenance)) continue;
                        List<Point3d> path = new List<Point3d> { appurtenance.Location };
                        AddIfFound(found, ClassifyPath(alignment, path, reach, tolerance, false),
                            id, appurtenance.Name, "Appurtenance", pressureNet.Name, path, CountShownIn(appurtenance.GetProfileViewsDisplayingMe(), alignmentProfileViews));
                    }
                }
            }

            found.Sort((x, y) => x.StationMin.CompareTo(y.StationMin));
            return found;
        }

        private struct Classification
        {
            public ProfilePartCategory Category;
            public double StationMin, StationMax, MaxOffset;
        }

        private static void AddIfFound(List<DetectedPart> found, Classification? result, ObjectId id, string name, string kind,
            string networkName, List<Point3d> path, int shownIn)
        {
            if (!result.HasValue) return;
            Classification c = result.Value;
            found.Add(new DetectedPart
            {
                Id = id,
                Name = name,
                Kind = kind,
                NetworkName = networkName,
                Category = c.Category,
                StationMin = c.StationMin,
                StationMax = c.StationMax,
                MaxOffset = c.MaxOffset,
                Path = path,
                AlreadyShownIn = shownIn,
                // Partial parts are listed for the user to decide, so they start unticked.
                Include = c.Category != ProfilePartCategory.Partial
            });
        }

        /// <summary>
        /// Along = every sample within tolerance; Partial = some; Crossing = none, but consecutive samples sit on
        /// opposite sides of the alignment and the crossing point itself is on it. Returns null for anything else.
        /// </summary>
        private static Classification? ClassifyPath(Alignment alignment, List<Point3d> path, Extents3d reach, double tolerance, bool includeCrossings)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (Point3d p in path)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            if (maxX < reach.MinPoint.X || minX > reach.MaxPoint.X || maxY < reach.MinPoint.Y || minY > reach.MaxPoint.Y) return null;

            int count = path.Count;
            bool[] inRange = new bool[count];
            double[] station = new double[count];
            double[] offset = new double[count];
            int onCount = 0;
            double onMin = double.MaxValue, onMax = double.MinValue, maxOff = 0.0;

            for (int i = 0; i < count; i++)
            {
                inRange[i] = TryStationOffset(alignment, path[i], out station[i], out offset[i]);
                if (inRange[i] && Math.Abs(offset[i]) <= tolerance)
                {
                    onCount++;
                    onMin = Math.Min(onMin, station[i]);
                    onMax = Math.Max(onMax, station[i]);
                    maxOff = Math.Max(maxOff, Math.Abs(offset[i]));
                }
            }

            if (onCount > 0)
            {
                return new Classification
                {
                    Category = onCount == count ? ProfilePartCategory.Along : ProfilePartCategory.Partial,
                    StationMin = onMin,
                    StationMax = onMax,
                    MaxOffset = maxOff
                };
            }

            if (!includeCrossings) return null;

            for (int i = 0; i + 1 < count; i++)
            {
                if (!inRange[i] || !inRange[i + 1] || Math.Sign(offset[i]) == Math.Sign(offset[i + 1])) continue;

                // Interpolate where the offset reaches zero and confirm that point really is on the alignment;
                // the sign can also flip across the centre of a tight alignment curve without any crossing.
                double t = offset[i] / (offset[i] - offset[i + 1]);
                Point3d cross = path[i] + (path[i + 1] - path[i]) * t;
                if (TryStationOffset(alignment, cross, out double crossStation, out double crossOffset) && Math.Abs(crossOffset) <= tolerance)
                {
                    return new Classification
                    {
                        Category = ProfilePartCategory.Crossing,
                        StationMin = crossStation,
                        StationMax = crossStation,
                        MaxOffset = Math.Abs(crossOffset)
                    };
                }
            }
            return null;
        }

        private static bool TryStationOffset(Alignment alignment, Point3d point, out double station, out double offset)
        {
            station = 0.0;
            offset = 0.0;
            bool outOfRange = false;
            try
            {
                alignment.StationOffsetAcceptOutOfRange(point.X, point.Y, ref station, ref offset, ref outOfRange);
            }
            catch (Exception)
            {
                return false;
            }
            return !outOfRange;
        }

        private static int CountShownIn(ObjectIdCollection displayingViews, ICollection<ObjectId> alignmentProfileViews)
        {
            int count = 0;
            foreach (ObjectId id in displayingViews)
            {
                if (alignmentProfileViews.Contains(id)) count++;
            }
            return count;
        }
    }
}
