using System;
using System.Collections.Generic;
using System.Linq;

// Label placement on a sheet, worked out in paper millimetres in the viewport's own frame: what a label must keep clear of
// (exact outlines, turned with their text), the places a dragged label can go (square to the viewport first, any angle when
// nothing else fits), and the plan for a whole viewport (crowded labels first, then crossings untangled). No AutoCAD /
// Civil 3D types, so the rules are tested without Civil 3D; AnnotArranger feeds it from the drawing and moves the labels.
namespace C3DProTools
{
    /// <summary>An axis-aligned box on paper (mm).</summary>
    internal readonly struct LRect
    {
        public readonly double MinX, MinY, MaxX, MaxY;
        public LRect(double minX, double minY, double maxX, double maxY) { MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public (double X, double Y) Center => ((MinX + MaxX) / 2, (MinY + MaxY) / 2);
        public static LRect Around(IEnumerable<(double X, double Y)> pts)
        {
            double a = double.MaxValue, b = double.MaxValue, c = double.MinValue, d = double.MinValue;
            foreach (var p in pts) { a = Math.Min(a, p.X); b = Math.Min(b, p.Y); c = Math.Max(c, p.X); d = Math.Max(d, p.Y); }
            return new LRect(a, b, c, d);
        }
        public LRect Offset(double dx, double dy) => new LRect(MinX + dx, MinY + dy, MaxX + dx, MaxY + dy);
        public LRect Grow(double g) => new LRect(MinX - g, MinY - g, MaxX + g, MaxY + g);
        public bool Overlaps(LRect o) => MinX < o.MaxX && o.MinX < MaxX && MinY < o.MaxY && o.MinY < MaxY;
        public bool Contains((double X, double Y) p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;
        public bool Inside(LRect o) => MinX >= o.MinX && MaxX <= o.MaxX && MinY >= o.MinY && MaxY <= o.MaxY;
        public List<(double X, double Y)> Corners() => new List<(double X, double Y)> { (MinX, MinY), (MaxX, MinY), (MaxX, MaxY), (MinX, MaxY) };
        public override string ToString() => $"[{MinX:0.0},{MinY:0.0} {MaxX:0.0},{MaxY:0.0}]";

        /// <summary>True when the segment crosses the box or lies in it.</summary>
        public bool Hits((double X, double Y) a, (double X, double Y) b) =>
            Math.Min(a.X, b.X) < MaxX && Math.Max(a.X, b.X) > MinX &&
            Math.Min(a.Y, b.Y) < MaxY && Math.Max(a.Y, b.Y) > MinY &&
            !LabelLayout.Separated(Corners(), new[] { a, b }, 0);
    }

    /// <summary>The kinds of things a label is checked against.</summary>
    internal enum ObstacleGroup
    {
        /// <summary>Text and labels: never covered by a label's text, and never crossed by a leader.</summary>
        Text,
        /// <summary>Leader lines and labelled points: never covered by a label's text; leaders crossing each other only costs.</summary>
        Leader,
        /// <summary>Parts of a network (pipes, structures): covered only when the network is one the label must keep clear of.</summary>
        Network
    }

    /// <summary>Everything on a sheet a label is checked against, as exact convex outlines, with a spatial grid so each check only looks nearby.</summary>
    internal sealed class LabelObstacles
    {
        private sealed class Item
        {
            public ObstacleGroup Group;
            /// <summary>The network (for <see cref="ObstacleGroup.Network"/>) and the owner (a label, or the part a normal label sits on).</summary>
            public string Network, Part, Kind;
            public LRect Bounds;
            /// <summary>The outline: a convex polygon, or two points for a line.</summary>
            public List<(double X, double Y)> Shape;
            public bool IsSegment => Shape.Count == 2;
        }

        private const double Cell = 15.0;
        private readonly List<Item> _items = new List<Item>();
        private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        private readonly Dictionary<long, List<int>> _textCells = new();
        private readonly Dictionary<long, List<int>> _leaderCells = new();
        /// <summary>Dragged labels placed so far (their text boxes), for lining neighbours up with them.</summary>
        private readonly List<(LRect box, string owner)> _placed = new List<(LRect, string)>();

        public int Count => _items.Count;

        private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;

        private void Index(int i)
        {
            LRect b = _items[i].Bounds;
            if (b.Width > 5000 || b.Height > 5000) b = new LRect(b.MinX, b.MinY, Math.Min(b.MaxX, b.MinX + 5000), Math.Min(b.MaxY, b.MinY + 5000));
            for (int x = (int)Math.Floor(b.MinX / Cell); x <= (int)Math.Floor(b.MaxX / Cell); x++)
                for (int y = (int)Math.Floor(b.MinY / Cell); y <= (int)Math.Floor(b.MaxY / Cell); y++)
                {
                    if (!_cells.TryGetValue(Key(x, y), out List<int> l)) _cells[Key(x, y)] = l = new List<int>();
                    l.Add(i);
                    var group = _items[i].Group;
                    var cells = group == ObstacleGroup.Text ? _textCells : group == ObstacleGroup.Leader ? _leaderCells : null;
                    if (cells != null)
                    {
                        if (!cells.TryGetValue(Key(x, y), out var grouped)) cells[Key(x, y)] = grouped = new List<int>();
                        grouped.Add(i);
                    }
                }
        }

        private IEnumerable<Item> Near(LRect b, int skipFirst = 0, ObstacleGroup? group = null)
        {
            if (skipFirst > 0)
            {
                for (int i = skipFirst; i < _items.Count; i++)
                {
                    var bounds = _items[i].Bounds;
                    if (bounds.MinX <= b.MaxX && bounds.MaxX >= b.MinX && bounds.MinY <= b.MaxY && bounds.MaxY >= b.MinY) yield return _items[i];
                }
                yield break;
            }
            var cells = group == ObstacleGroup.Text ? _textCells : group == ObstacleGroup.Leader ? _leaderCells : _cells;
            HashSet<int> seen = new HashSet<int>();
            for (int x = (int)Math.Floor(b.MinX / Cell); x <= (int)Math.Floor(b.MaxX / Cell); x++)
                for (int y = (int)Math.Floor(b.MinY / Cell); y <= (int)Math.Floor(b.MaxY / Cell); y++)
                    if (cells.TryGetValue(Key(x, y), out List<int> l))
                        foreach (int i in l) if (seen.Add(i)) yield return _items[i];
        }

        private void Add(Item it) { _items.Add(it); Index(_items.Count - 1); }

        public void AddRect(LRect r, ObstacleGroup group, string kind, string network = null, string part = null) =>
            Add(new Item { Group = group, Kind = kind, Network = network, Part = part, Shape = r.Corners(), Bounds = r });

        public void AddSegment((double X, double Y) a, (double X, double Y) b, ObstacleGroup group, string kind, string network = null, string part = null) =>
            Add(new Item { Group = group, Kind = kind, Network = network, Part = part, Shape = new List<(double X, double Y)> { a, b }, Bounds = LRect.Around(new[] { a, b }) });

        /// <summary>A polygon outline (made convex: its hull).</summary>
        public void AddPolygon(IReadOnlyList<(double X, double Y)> polygon, ObstacleGroup group, string kind, string network = null, string part = null)
        {
            List<(double X, double Y)> hull = LabelLayout.Hull(polygon);
            if (hull.Count < 2) return;
            Add(new Item { Group = group, Kind = kind, Network = network, Part = part, Shape = hull, Bounds = LRect.Around(hull) });
        }

        /// <summary>A copy to try placements on without touching this one.</summary>
        public LabelObstacles Clone()
        {
            LabelObstacles c = new LabelObstacles();
            foreach (Item it in _items) c.Add(it);
            c._placed.AddRange(_placed);
            return c;
        }

        /// <summary>A copy without the text and leaders of <paramref name="owners"/> (labels to be placed again); their labelled points stay reserved.</summary>
        public LabelObstacles Without(ICollection<string> owners)
        {
            LabelObstacles c = new LabelObstacles();
            foreach (Item it in _items) if (it.Part == null || !owners.Contains(it.Part) || it.Kind == LabelLayout.AnchorKind) c.Add(it);
            c._placed.AddRange(_placed.Where(p => !owners.Contains(p.owner)));
            return c;
        }

        /// <summary>A copy without any leader lines (for a second-best place that covers only another label's leader).</summary>
        public LabelObstacles WithoutLeaders()
        {
            LabelObstacles c = new LabelObstacles();
            // Leader lines go; the reserved labelled points stay (a label covering one hides where another label points).
            foreach (Item it in _items) if (it.Group != ObstacleGroup.Leader || it.Kind == LabelLayout.AnchorKind) c.Add(it);
            c._placed.AddRange(_placed);
            return c;
        }

        internal IEnumerable<LRect> PlacedBoxes => _items.Where(p => p.Group == ObstacleGroup.Text && !p.IsSegment).Select(p => p.Bounds);

        internal void Placed(LRect box, string owner) => _placed.Add((box, owner));

        /// <summary>Every item, for writing an arrangement case.</summary>
        internal IEnumerable<(ObstacleGroup group, string kind, string network, string part, List<(double X, double Y)> shape)> Items() =>
            _items.Select(it => (it.Group, it.Kind, it.Network, it.Part, it.Shape));

        /// <summary>
        /// What an outline (kept <paramref name="gap"/> clear) covers, or null when it is clear: text, leaders, and the parts of
        /// the networks in <paramref name="networks"/>. Items of <paramref name="ownPart"/> (the label itself) and
        /// <paramref name="alsoOwn"/> (the part a normal label sits on) are exempt.
        /// </summary>
        public string Blocker(IReadOnlyList<(double X, double Y)> shape, double gap, ISet<string> networks, string ownPart = null, string alsoOwn = null, int skipFirst = 0)
        {
            LRect g = LRect.Around(shape).Grow(gap);
            foreach (Item it in Near(g, skipFirst))
            {
                if (it.Group == ObstacleGroup.Network && (it.Network == null || networks == null || !networks.Contains(it.Network))) continue;
                if (it.Part != null && (it.Part == ownPart || it.Part == alsoOwn)) continue;
                if (!it.Bounds.Overlaps(g)) continue;
                if (!LabelLayout.Separated(shape, it.Shape, gap)) return it.Kind;
            }
            return null;
        }

        public string Blocker(LRect box, double gap, ISet<string> networks, string ownPart = null, string alsoOwn = null, int skipFirst = 0) =>
            Blocker(box.Corners(), gap, networks, ownPart, alsoOwn, skipFirst);

        /// <summary>True when a leader from <paramref name="a"/> to <paramref name="b"/> crosses text (other than <paramref name="own"/>'s own label).</summary>
        public bool LeaderCrossesText((double X, double Y) a, (double X, double Y) b, string own = null, int skipFirst = 0)
        {
            var seg = new[] { a, b };
            foreach (Item it in Near(LRect.Around(seg), skipFirst, ObstacleGroup.Text))
                if (it.Group == ObstacleGroup.Text && !it.IsSegment && (own == null || it.Part != own) && !LabelLayout.Separated(seg, it.Shape, 0)) return true;
            return false;
        }

        /// <summary>How many leaders the segment crosses (leaders of <paramref name="own"/> left out).</summary>
        public int LeaderCrossings((double X, double Y) a, (double X, double Y) b, string own = null, int skipFirst = 0)
        {
            int n = 0;
            foreach (Item it in Near(LRect.Around(new[] { a, b }), skipFirst, ObstacleGroup.Leader))
                if (it.Group == ObstacleGroup.Leader && it.IsSegment && (own == null || it.Part != own) && LabelLayout.Cross(a, b, it.Shape[0], it.Shape[1])) n++;
            return n;
        }

        /// <summary>True when the box lines up with a dragged label placed nearby: the same top, bottom, left or right edge.</summary>
        public bool LinesUp(LRect box, double reach)
        {
            foreach ((LRect p, string _) in _placed)
            {
                if (Math.Abs(p.Center.X - box.Center.X) > reach || Math.Abs(p.Center.Y - box.Center.Y) > reach) continue;
                if (Math.Abs(p.MinY - box.MinY) < 0.3 || Math.Abs(p.MaxY - box.MaxY) < 0.3 || Math.Abs(p.MinX - box.MinX) < 0.3 || Math.Abs(p.MaxX - box.MaxX) < 0.3)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// A dragged label's shape: its text box relative to the leader end, with the text to the right of the end and to the left
    /// (Civil 3D puts dragged text on the side of the anchor the label point is on), and where it was measured, above and below
    /// the anchor too (the leader may join the text elsewhere then). Dragged text reads upright on the sheet.
    /// </summary>
    internal sealed class DraggedShape
    {
        public LRect Right, Left;
        public LRect? UpRight, UpLeft, DownRight, DownLeft;

        /// <summary>
        /// Measured in the drawing: the text box (relative to the leader end) with the leader pointing at each angle (radians,
        /// from the anchor). When there are any, the box of the nearest angle is used and the right/left guess is not: Civil 3D
        /// picks the side of turned text by its own rule, which only measuring shows.
        /// </summary>
        /// <remarks>
        /// Each sample also keeps the text's outline (convex, relative to the leader end; null when not measured): text turned
        /// with its pipe fills only a slim slanted strip of its box, so planning with the box alone wastes most of the room.
        /// </remarks>
        public List<(double Angle, LRect Box, List<(double X, double Y)> Hull)> Samples = new List<(double Angle, LRect Box, List<(double X, double Y)> Hull)>();

        private int Nearest(double dx, double dy)
        {
            double a = Math.Atan2(dy, dx), g = double.MaxValue;
            int best = 0;
            for (int i = 0; i < Samples.Count; i++)
            {
                double d = Math.Abs(Math.IEEERemainder(a - Samples[i].Angle, 2 * Math.PI));
                if (d < g) { g = d; best = i; }
            }
            return best;
        }

        /// <summary>The box for a leader ending (dx, dy) from the anchor, the text to the right or left of its end.</summary>
        public LRect BoxFor(double dx, double dy, bool right)
        {
            if (Samples.Count > 0) return Samples[Nearest(dx, dy)].Box;
            if (dy >= 0) return right ? (UpRight ?? Right) : (UpLeft ?? Left);
            return right ? (DownRight ?? Right) : (DownLeft ?? Left);
        }

        /// <summary>The text's outline (relative to the leader end) for a leader at (dx, dy); null when only boxes are known.</summary>
        public List<(double X, double Y)> HullFor(double dx, double dy) => Samples.Count > 0 ? Samples[Nearest(dx, dy)].Hull : null;

        /// <summary>Records what the drawing showed for a leader at (dx, dy): replaces a sample within 3° of it, else adds one.</summary>
        public void Learn(double dx, double dy, LRect box, List<(double X, double Y)> hull = null)
        {
            double a = Math.Atan2(dy, dx);
            Samples.RemoveAll(s => Math.Abs(Math.IEEERemainder(a - s.Angle, 2 * Math.PI)) < 3 * Math.PI / 180);
            Samples.Add((a, box, hull));
        }
    }

    /// <summary>One place a dragged label could go.</summary>
    internal struct LabelSpot
    {
        public LRect Box;
        public (double X, double Y) End;
        public double Cost;
        /// <summary>0 = leader straight up on the sheet, 1 = straight down, 2 = sideways, 3 = slanting.</summary>
        public int Direction;
        /// <summary>Where the leader starts, and the ratio along its pipe, when the label slides there (null: its own anchor).</summary>
        public (double X, double Y) Anchor;
        public double? Ratio;
        /// <summary>The text's exact outline here (convex; null: the box is the outline).</summary>
        public List<(double X, double Y)> Hull;
        /// <summary>What the text covers: its outline when measured, else its box.</summary>
        public IReadOnlyList<(double X, double Y)> Outline => Hull ?? Box.Corners();
        /// <summary>The text covers the point.</summary>
        public bool Covers((double X, double Y) p) => Hull != null ? !LabelLayout.Separated(Hull, new[] { p, p }, 0) : Box.Contains(p);
        /// <summary>The outline moved to a leader end: <paramref name="relative"/> plus (x, y); null stays null.</summary>
        public static List<(double X, double Y)> At(List<(double X, double Y)> relative, double x, double y) => relative?.Select(p => (p.X + x, p.Y + y)).ToList();
        public override string ToString() => $"dir {Direction} end ({End.X:0.0}, {End.Y:0.0}) cost {Cost:0.00}" + (Ratio.HasValue ? $" at ratio {Ratio:0.00}" : "");
    }

    internal sealed class LayoutRules
    {
        /// <summary>Longest leader on paper (mm).</summary>
        public double MaxDistance = 60;
        /// <summary>Clear space kept around a label (mm).</summary>
        public double Gap = 1.5;
        /// <summary>Shortest leader (mm), and the spacing of the grid of leader ends tried (along the viewport's axes).</summary>
        public double MinLeader = 3, Step = 2;
        /// <summary>Leaders go up the sheet rather than down (labels of a viewport all one way where they can).</summary>
        public bool PreferUp = true;
        /// <summary>Extra cost of a leader going the other way.</summary>
        public double OtherWayCost = 0.3;
        /// <summary>Extra cost of a leader at 45° to the viewport's axes (less for smaller angles): square leaders come first.</summary>
        public double SlantCost = 0.6;
        /// <summary>Extra cost per leader crossed: more than any other place costs, so a leader crosses only where nothing else fits.</summary>
        public double CrossingCost = 10;
        /// <summary>Cost taken off a box lined up with a placed neighbour (rows and columns square to the viewport).</summary>
        public double AlignBonus = 0.15;
        /// <summary>Cost of a box right against a labelled point still waiting for its place (less the further off it is).</summary>
        public double OcclusionCost = 0.8;
        /// <summary>Cost of sliding a label's anchor along its pipe, per whole length of the pipe slid.</summary>
        public double SlideCost = 0.4;
        /// <summary>
        /// Longest leader (mm) where labelled points crowd (a junction with many pipe ends, a run of close joints): there is no
        /// room for their labels within the usual longest leader, and by hand they are fanned or stepped further out. 0 = 2.5
        /// times <see cref="MaxDistance"/>.
        /// </summary>
        public double CrowdedReach;
        public double Crowded => CrowdedReach > 0 ? CrowdedReach : 2.5 * MaxDistance;
    }

    internal static class LabelLayout
    {
        internal static bool Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d)
        {
            double d1 = Side(c, d, a), d2 = Side(c, d, b), d3 = Side(a, b, c), d4 = Side(a, b, d);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static double Side((double X, double Y) a, (double X, double Y) b, (double X, double Y) p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

        /// <summary>
        /// True when two convex outlines (polygons, or two-point lines) are at least <paramref name="gap"/> apart along some edge
        /// direction of either (separating axes). With a gap of 0, touching counts as apart.
        /// </summary>
        internal static bool Separated(IReadOnlyList<(double X, double Y)> a, IReadOnlyList<(double X, double Y)> b, double gap)
        {
            foreach (IReadOnlyList<(double X, double Y)> poly in new[] { a, b })
            {
                int edges = poly.Count == 2 ? 1 : poly.Count;
                for (int i = 0; i < edges; i++)
                {
                    var p = poly[i];
                    var q = poly[(i + 1) % poly.Count];
                    double nx = -(q.Y - p.Y), ny = q.X - p.X, len = Math.Sqrt(nx * nx + ny * ny);
                    if (len < 1e-12) continue;
                    nx /= len; ny /= len;
                    double minA = double.MaxValue, maxA = double.MinValue, minB = double.MaxValue, maxB = double.MinValue;
                    foreach (var v in a) { double t = v.X * nx + v.Y * ny; minA = Math.Min(minA, t); maxA = Math.Max(maxA, t); }
                    foreach (var v in b) { double t = v.X * nx + v.Y * ny; minB = Math.Min(minB, t); maxB = Math.Max(maxB, t); }
                    if (minB - maxA >= gap - 1e-9 || minA - maxB >= gap - 1e-9) return true;
                }
            }
            return false;
        }

        internal static bool InPolygon(IReadOnlyList<(double X, double Y)> poly, (double X, double Y) p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y + 1e-12) + poly[i].X)
                    inside = !inside;
            return inside;
        }

        /// <summary>The convex hull of the points, counter-clockwise (a label's text and frame as one outline, turned with its text).</summary>
        public static List<(double X, double Y)> Hull(IEnumerable<(double X, double Y)> points)
        {
            List<(double X, double Y)> p = points.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
            if (p.Count < 3) return p;
            List<(double X, double Y)> h = new List<(double X, double Y)>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = h.Count;
                foreach (var q in pass == 0 ? p : Enumerable.Reverse(p))
                {
                    while (h.Count >= start + 2 && Side(h[h.Count - 2], h[h.Count - 1], q) <= 1e-12) h.RemoveAt(h.Count - 1);
                    h.Add(q);
                }
                h.RemoveAt(h.Count - 1);
            }
            return h;
        }

        /// <summary>
        /// The smallest rectangle round the points, turned to fit them (one of its sides lies along a side of their hull): a
        /// structure's drawn lines as a clean outline square to the structure, whatever its part data says.
        /// </summary>
        public static List<(double X, double Y)> MinAreaRect(IEnumerable<(double X, double Y)> points)
        {
            List<(double X, double Y)> hull = Hull(points);
            if (hull.Count < 3) return hull;
            double bestArea = double.MaxValue;
            List<(double X, double Y)> best = null;
            for (int i = 0; i < hull.Count; i++)
            {
                var a = hull[i];
                var b = hull[(i + 1) % hull.Count];
                double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                if (len < 1e-12) continue;
                double ux = (b.X - a.X) / len, uy = (b.Y - a.Y) / len;
                double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
                foreach (var p in hull)
                {
                    double u = p.X * ux + p.Y * uy, v = -p.X * uy + p.Y * ux;
                    minU = Math.Min(minU, u); maxU = Math.Max(maxU, u); minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
                }
                double area = (maxU - minU) * (maxV - minV);
                if (area >= bestArea - 1e-12) continue;
                bestArea = area;
                (double X, double Y) At(double u, double v) => (u * ux - v * uy, u * uy + v * ux);
                best = new List<(double X, double Y)> { At(minU, minV), At(maxU, minV), At(maxU, maxV), At(minU, maxV) };
            }
            return best ?? hull;
        }

        /// <summary>The points turned by <paramref name="angle"/> (radians, counter-clockwise) about <paramref name="about"/>.</summary>
        public static List<(double X, double Y)> Turn(IEnumerable<(double X, double Y)> points, (double X, double Y) about, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            return points.Select(q => (about.X + (q.X - about.X) * c - (q.Y - about.Y) * s, about.Y + (q.X - about.X) * s + (q.Y - about.Y) * c)).ToList();
        }

        /// <summary>
        /// How far (radians) a text direction on the sheet is from reading horizontally, between −90° and +90° (text upside down
        /// counts as horizontal: plan readability flips it).
        /// </summary>
        public static double OffUpright(double paperAngle)
        {
            double a = Math.IEEERemainder(paperAngle, Math.PI);
            return Math.Abs(a - Math.PI / 2) < 1e-12 ? -Math.PI / 2 : a;
        }

        /// <summary>How far (degrees, 0 to 45) a leader is from the viewport's nearest axis.</summary>
        public static double OffAxis(double dx, double dy)
        {
            double a = Math.Abs(Math.Atan2(dy, dx) * 180 / Math.PI) % 90;
            return Math.Min(a, 90 - a);
        }

        /// <summary>
        /// The places a dragged label anchored at <paramref name="a"/> can go, cheapest first: leader ends on a grid square to the
        /// viewport, any angle, up to the longest leader. Short leaders square to the viewport (up or down, as preferred) cost
        /// least; slanting ones cost more the further they are off square. The text sits beside the leader end, away from the
        /// anchor, and never covers the anchor.
        /// </summary>
        /// <param name="outward">
        /// For a label in a group of labelled points: the way out of the group (from its middle to this anchor). Leaders and text
        /// pointing back into the group cost more, so a crowded manhole's labels fan out all round it; (0, 0) when alone.
        /// </param>
        public static List<LabelSpot> Spots((double X, double Y) a, DraggedShape shape, LayoutRules rules, (double X, double Y) outward = default)
        {
            List<LabelSpot> spots = new List<LabelSpot>();
            double max = Math.Max(rules.MinLeader, rules.MaxDistance), step = Math.Max(0.5, rules.Step);
            double ol = Math.Sqrt(outward.X * outward.X + outward.Y * outward.Y);
            (double X, double Y) o = ol > 1e-9 ? (outward.X / ol, outward.Y / ol) : (0, 0);
            int n = (int)Math.Ceiling(max / step);
            for (int ix = -n; ix <= n; ix++)
                for (int iy = -n; iy <= n; iy++)
                {
                    double dx = ix * step, dy = iy * step, length = Math.Sqrt(dx * dx + dy * dy);
                    if (length < rules.MinLeader - 1e-9 || length > max + 1e-9) continue;
                    double off = OffAxis(dx, dy);
                    int direction = off > 1e-9 ? 3 : ix == 0 ? (dy > 0 ? 0 : 1) : 2;
                    bool otherWay = rules.PreferUp ? dy < -1e-9 : dy > 1e-9;
                    double inward = ol > 1e-9 ? Math.Max(0, -(dx * o.X + dy * o.Y) / length) : 0;
                    double cost = length / max + rules.SlantCost * off / 45 + (otherWay ? rules.OtherWayCost : 0) + 0.4 * inward;
                    (double X, double Y) end = (a.X + dx, a.Y + dy);
                    foreach (bool right in ix > 0 ? new[] { true } : ix < 0 ? new[] { false } : new[] { true, false })
                    {
                        LRect box = shape.BoxFor(dx, dy, right).Offset(end.X, end.Y);
                        LabelSpot spot = new LabelSpot { Box = box, Hull = LabelSpot.At(shape.HullFor(dx, dy), end.X, end.Y), End = end, Anchor = a, Direction = direction };
                        if (spot.Covers(a)) continue;
                        double sideCost = Math.Abs(o.X) > 0.3 ? (right == (o.X > 0) ? 0 : 0.1) : (right ? 0 : 0.05);
                        spot.Cost = cost + sideCost;
                        spots.Add(spot);
                        // Measured shapes do not depend on the side guess: one spot per leader end.
                        if (shape.Samples.Count > 0) break;
                    }
                }
            return spots.OrderBy(sp => sp.Cost).ToList();
        }

        // Exact column positions fill gaps a grid tied to the anchor can miss.
        internal static IEnumerable<LabelSpot> StackSpots(LabelObstacles obstacles, (double X, double Y) a,
            DraggedShape shape, LayoutRules rules, (double X, double Y) outward)
        {
            foreach (var placed in obstacles.PlacedBoxes)
                foreach (bool up in new[] { true, false })
                    foreach (bool right in new[] { true, false })
                        foreach (bool alignLeft in new[] { true, false })
                        {
                            var relative = shape.BoxFor(0, up ? 1 : -1, right);
                            double x = alignLeft ? placed.MinX - relative.MinX : placed.MaxX - relative.MaxX;
                            double y = up ? placed.MaxY + rules.Gap + 1e-6 - relative.MinY : placed.MinY - rules.Gap - 1e-6 - relative.MaxY;
                            double dx = x - a.X, dy = y - a.Y;
                            // Measured shapes depend on which side of the anchor the end lies.
                            if ((dy >= 0) != up || (dx > 1e-9 && !right) || (dx < -1e-9 && right)) continue;
                            double length = Math.Sqrt(dx * dx + dy * dy);
                            if (length < rules.MinLeader || length > rules.MaxDistance) continue;
                            var box = relative.Offset(x, y);
                            var hull = LabelSpot.At(shape.HullFor(0, up ? 1 : -1), x, y);
                            if (new LabelSpot { Box = box, Hull = hull }.Covers(a)) continue;
                            double off = OffAxis(dx, dy);
                            double ol = Math.Sqrt(outward.X * outward.X + outward.Y * outward.Y);
                            double inward = ol > 1e-9 ? Math.Max(0, -(dx * outward.X + dy * outward.Y) / (length * ol)) : 0;
                            double sideCost = Math.Abs(outward.X) > 0.3 ? (right == (outward.X > 0) ? 0 : 0.1) : (right ? 0 : 0.05);
                            yield return new LabelSpot { Box = box, Hull = hull, End = (x, y), Anchor = a, Direction = off > 1e-9 ? 3 : Math.Abs(dx) < 1e-9 ? (dy > 0 ? 0 : 1) : 2,
                                Cost = length / rules.MaxDistance + rules.SlantCost * off / 45 +
                                    ((rules.PreferUp ? dy < 0 : dy > 0) ? rules.OtherWayCost : 0) + 0.4 * inward + sideCost };
                        }
        }

        /// <summary>Distance (mm) from a point to a box (0 inside it).</summary>
        public static double Distance(LRect box, (double X, double Y) p)
        {
            double dx = Math.Max(0, Math.Max(box.MinX - p.X, p.X - box.MaxX)), dy = Math.Max(0, Math.Max(box.MinY - p.Y, p.Y - box.MaxY));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// The best clear place for a dragged label, or null: its text covers nothing it must keep clear of, stays inside
        /// <paramref name="inArea"/>, and its leader crosses no text; leaders crossed add to the cost, lining up with a placed
        /// neighbour takes a little off, and a box right against a labelled point still waiting for its place (one of
        /// <paramref name="unplaced"/>) costs more, so the first label of a crowd does not wall the others in.
        /// </summary>
        public static LabelSpot? Choose(LabelObstacles obstacles, (double X, double Y) a, DraggedShape shape, LayoutRules rules, ISet<string> networks,
            string own, Func<LRect, bool> inArea, ICollection<LabelSpot> skip = null, (double X, double Y) outward = default,
            IReadOnlyList<(double X, double Y)> unplaced = null, IReadOnlyList<LabelSpot> candidates = null, LabelObstacles staticObstacles = null)
        {
            LabelSpot? best = null;
            double reach = rules.MaxDistance;
            var stacks = StackSpots(obstacles, a, shape, rules, outward);
            if (staticObstacles != null) stacks = stacks.Where(s =>
                staticObstacles.Blocker(s.Outline, rules.Gap, networks, own) == null && !staticObstacles.LeaderCrossesText(a, s.End, own) &&
                staticObstacles.LeaderCrossings(a, s.End, own) == 0);
            int skipFirst = staticObstacles?.Count ?? 0;
            foreach (LabelSpot s in (candidates ?? Spots(a, shape, rules, outward)).Concat(stacks).OrderBy(s => s.Cost))
            {
                if (best.HasValue && s.Cost - rules.AlignBonus >= best.Value.Cost) break;
                if (skip != null && skip.Any(k => Math.Abs(k.End.X - s.End.X) < 1e-6 && Math.Abs(k.End.Y - s.End.Y) < 1e-6 && Math.Abs(k.Box.MinX - s.Box.MinX) < 1e-6)) continue;
                if (inArea != null && !inArea(s.Box)) continue;
                if (obstacles.Blocker(s.Outline, rules.Gap, networks, own, skipFirst: skipFirst) != null) continue;
                if (obstacles.LeaderCrossesText(a, s.End, own, skipFirst)) continue;
                double cost = s.Cost + rules.CrossingCost * obstacles.LeaderCrossings(a, s.End, own) - (obstacles.LinesUp(s.Box, reach) ? rules.AlignBonus : 0);
                if (unplaced != null)
                {
                    // Scaled to the label: within one box height of a waiting point costs, the more the closer.
                    double scale = s.Box.Height + rules.Gap;
                    foreach ((double X, double Y) u in unplaced)
                    {
                        double d = Distance(s.Box, u);
                        if (d < scale) cost += rules.OcclusionCost * (1 - d / scale);
                    }
                }
                if (!best.HasValue || cost < best.Value.Cost) best = new LabelSpot { Box = s.Box, Hull = s.Hull, End = s.End, Anchor = a, Direction = s.Direction, Cost = cost };
            }
            return best;
        }

        /// <summary>
        /// Reserves the point a label points to, so no other label's text covers it (its leader would start under that text).
        /// The label itself (<paramref name="label"/>) is exempt.
        /// </summary>
        public static void ReserveAnchor(LabelObstacles obstacles, (double X, double Y) anchor, string label) =>
            obstacles.AddRect(new LRect(anchor.X - 0.5, anchor.Y - 0.5, anchor.X + 0.5, anchor.Y + 0.5), ObstacleGroup.Leader, AnchorKind, part: label);

        /// <summary>The kind of a reserved labelled point.</summary>
        public const string AnchorKind = "labelled point";

        /// <summary>Records a placed dragged label: its text and its leader become obstacles for the labels after it.</summary>
        public static void Claim(LabelObstacles obstacles, LabelSpot spot, (double X, double Y) anchor, string label)
        {
            obstacles.AddPolygon(spot.Outline, ObstacleGroup.Text, "label", part: label);
            obstacles.AddSegment(anchor, spot.End, ObstacleGroup.Leader, "leader", part: label);
            obstacles.Placed(spot.Box, label);
        }

        public static void Claim(LabelObstacles obstacles, LRect box, (double X, double Y) anchor, (double X, double Y) end, string label)
        {
            obstacles.AddRect(box, ObstacleGroup.Text, "label", part: label);
            obstacles.AddSegment(anchor, end, ObstacleGroup.Leader, "leader", part: label);
            obstacles.Placed(box, label);
        }
    }

    /// <summary>A dragged label to be planned: where it points, its shape, what it keeps clear of, and (once planned) its place.</summary>
    internal sealed class PlanLabel
    {
        public string Key;
        // Null means an older caller/case: recognize the measured manhole box.
        private bool? _isStructure;
        public bool IsStructure
        {
            get => _isStructure ?? (Shape != null && Math.Abs(Shape.Right.Width - 35) <= 2.5 && Math.Abs(Shape.Right.Height - 33.5) <= 2);
            set => _isStructure = value;
        }
        public (double X, double Y) Anchor;
        public DraggedShape Shape;
        public ISet<string> Networks;
        public Func<LRect, bool> InArea;
        /// <summary>Its place (null when none is clear).</summary>
        public LabelSpot? Spot;
        /// <summary>The way out of its group of labelled points (set by the planner; (0, 0) alone).</summary>
        public (double X, double Y) OutwardVec;
        /// <summary>Leaders go up rather than down (set by the planner, per group).</summary>
        public bool PreferUp = true;
        /// <summary>Other points along its pipe it may slide its anchor to (a pipe label whose style allows it), and its own ratio.</summary>
        public List<(double X, double Y, double Ratio)> AltAnchors = new List<(double X, double Y, double Ratio)>();
        public double? Ratio;
        // Per-plan static feasibility cache. Dynamic label and leader obstacles are still checked on every choice.
        internal LabelObstacles StaticObstacles;
        internal readonly Dictionary<(double X, double Y, bool Up), List<LabelSpot>> Candidates = new();

        /// <summary>Where its leader starts: its own anchor, or the point it slid to.</summary>
        public (double X, double Y) Start => Spot.HasValue && Spot.Value.Ratio.HasValue ? Spot.Value.Anchor : Anchor;
    }

    /// <summary>
    /// Plans the dragged labels of one viewport together. Labelled points are grouped (a manhole with several labels, a run of
    /// pipe ends); each group is planned in a few orders (most crowded first, edge points first, middle points first) and both
    /// ways (leaders up, leaders down), and the best is kept: most labels placed, then least cost. Then labels with a crossed
    /// leader are placed again, crossing pairs swapped or placed again together, and a label left without a place moves a
    /// placed neighbour out of its way and places itself first.
    /// </summary>
    internal static class LabelPlanner
    {
        private static LabelObstacles With(LabelObstacles baseObs, IEnumerable<PlanLabel> placed)
        {
            LabelObstacles o = baseObs.Clone();
            foreach (PlanLabel p in placed) Claim(o, p);
            return o;
        }

        private static void Claim(LabelObstacles o, PlanLabel p)
        {
            if (!p.Spot.HasValue) return;
            if (p.Spot.Value.Ratio.HasValue) LabelLayout.ReserveAnchor(o, p.Spot.Value.Anchor, p.Key);
            LabelLayout.Claim(o, p.Spot.Value, p.Start, p.Key);
        }

        private static int Crossings(List<PlanLabel> labels)
        {
            int n = 0;
            for (int i = 0; i < labels.Count; i++)
                for (int j = i + 1; j < labels.Count; j++)
                    if (labels[i].Spot.HasValue && labels[j].Spot.HasValue && LabelLayout.Cross(labels[i].Start, labels[i].Spot.Value.End, labels[j].Start, labels[j].Spot.Value.End)) n++;
            return n;
        }

        private static LayoutRules Copy(LayoutRules r, bool up) => new LayoutRules
        {
            MaxDistance = r.MaxDistance, Gap = r.Gap, MinLeader = r.MinLeader, Step = r.Step, PreferUp = up, OtherWayCost = r.OtherWayCost,
            SlantCost = r.SlantCost, CrossingCost = r.CrossingCost, AlignBonus = r.AlignBonus, OcclusionCost = r.OcclusionCost, SlideCost = r.SlideCost, CrowdedReach = r.CrowdedReach
        };

        /// <summary>
        /// The best place for one label: from its own anchor, or (a pipe label allowed to slide) from a point further along its
        /// pipe, the slide costing a little.
        /// </summary>
        public static LabelSpot? ChooseFor(LabelObstacles o, PlanLabel l, LayoutRules rules, IReadOnlyList<(double X, double Y)> unplaced = null, ICollection<LabelSpot> skip = null)
        {
            LayoutRules r = Copy(rules, l.PreferUp);
            List<LabelSpot> Candidates((double X, double Y) anchor)
            {
                if (l.StaticObstacles == null) return null;
                var key = (anchor.X, anchor.Y, l.PreferUp);
                if (!l.Candidates.TryGetValue(key, out var spots))
                {
                    if (l.Candidates.TryGetValue((anchor.X, anchor.Y, !l.PreferUp), out var opposite))
                    {
                        spots = opposite.Select(s => {
                            double dy = s.End.Y - anchor.Y;
                            bool otherWay = l.PreferUp ? dy < -1e-9 : dy > 1e-9;
                            s.Cost += Math.Abs(dy) <= 1e-9 ? 0 : otherWay ? r.OtherWayCost : -r.OtherWayCost;
                            return s;
                        }).OrderBy(s => s.Cost).ToList();
                    }
                    else spots = LabelLayout.Spots(anchor, l.Shape, r, l.OutwardVec)
                        .Where(s => (l.InArea == null || l.InArea(s.Box)) &&
                            l.StaticObstacles.Blocker(s.Outline, r.Gap, l.Networks, l.Key) == null &&
                            !l.StaticObstacles.LeaderCrossesText(anchor, s.End, l.Key) &&
                            l.StaticObstacles.LeaderCrossings(anchor, s.End, l.Key) == 0).ToList();
                    l.Candidates[key] = spots;
                }
                return spots;
            }
            LabelSpot? best = LabelLayout.Choose(o, l.Anchor, l.Shape, r, l.Networks, l.Key, l.InArea, skip, l.OutwardVec, unplaced, Candidates(l.Anchor), l.StaticObstacles);
            foreach ((double X, double Y, double Ratio) alt in l.AltAnchors)
            {
                LabelSpot? s = LabelLayout.Choose(o, (alt.X, alt.Y), l.Shape, r, l.Networks, l.Key, l.InArea, skip, l.OutwardVec, unplaced, Candidates((alt.X, alt.Y)), l.StaticObstacles);
                if (!s.HasValue) continue;
                LabelSpot slid = s.Value;
                slid.Cost += rules.SlideCost * Math.Abs(alt.Ratio - (l.Ratio ?? 0.5));
                slid.Ratio = alt.Ratio;
                if (!best.HasValue || slid.Cost < best.Value.Cost) best = slid;
            }
            return best;
        }

        /// <summary>Groups of labelled points: chains of points no further apart than <paramref name="reach"/>.</summary>
        private static List<List<PlanLabel>> Groups(List<PlanLabel> labels, double reach)
        {
            int[] g = Enumerable.Range(0, labels.Count).ToArray();
            int Find(int i) { while (g[i] != i) i = g[i] = g[g[i]]; return i; }
            for (int i = 0; i < labels.Count; i++)
                for (int j = i + 1; j < labels.Count; j++)
                    if (Math.Abs(labels[i].Anchor.X - labels[j].Anchor.X) <= reach && Math.Abs(labels[i].Anchor.Y - labels[j].Anchor.Y) <= reach) g[Find(j)] = Find(i);
            return Enumerable.Range(0, labels.Count).GroupBy(Find).Select(x => x.Select(i => labels[i]).ToList()).OrderByDescending(x => x.Count).ToList();
        }

        /// <summary>Plans every label's place (each one's <see cref="PlanLabel.Spot"/>); returns whether most leaders go up.</summary>
        private static bool FreePlan(LabelObstacles baseObs, List<PlanLabel> labels, LayoutRules rules)
        {
            if (labels.Count == 0) return rules.PreferUp;
            foreach (var label in labels) { label.Candidates.Clear(); label.StaticObstacles = baseObs; }
            double groupReach = Math.Max(5, rules.MaxDistance / 4);
            List<List<PlanLabel>> groups = Groups(labels, groupReach);
            foreach (List<PlanLabel> group in groups)
            {
                double mx = group.Average(l => l.Anchor.X), my = group.Average(l => l.Anchor.Y);
                foreach (PlanLabel l in group)
                {
                    double dx = l.Anchor.X - mx, dy = l.Anchor.Y - my, len = Math.Sqrt(dx * dx + dy * dy);
                    l.OutwardVec = group.Count < 2 || len < 0.1 ? (0, 0) : (dx / len, dy / len);
                }
            }

            // Count actual free choices, rather than assuming all anchors in a cluster are equally difficult.
            foreach (var label in labels) { label.PreferUp = rules.PreferUp; ChooseFor(baseObs, label, rules); }
            int Freedom(PlanLabel label) => label.Candidates.Where(pair => pair.Key.Up == rules.PreferUp).Sum(pair => pair.Value.Count);
            groups = groups.OrderBy(group => group.Min(Freedom)).ToList();
            if (groups.Any(group => group.Count >= 8) && SolveDense(labels, rules))
            {
                foreach (var l in labels)
                {
                    var obstacles = With(baseObs, labels.Where(x => x != l));
                    foreach (var entry in l.Candidates.ToList())
                    {
                        var r = Copy(rules, entry.Key.Up);
                        var edges = LabelLayout.StackSpots(obstacles, (entry.Key.X, entry.Key.Y), l.Shape, r, l.OutwardVec)
                            .Where(s => (l.InArea == null || l.InArea(s.Box)) && baseObs.Blocker(s.Outline, r.Gap, l.Networks, l.Key) == null &&
                                !baseObs.LeaderCrossesText(s.Anchor, s.End, l.Key) && baseObs.LeaderCrossings(s.Anchor, s.End, l.Key) == 0);
                        l.Candidates[entry.Key] = entry.Value.Concat(edges).OrderBy(s => s.Cost).ToList();
                    }
                }
                OptimizeDense(labels, rules);
                RepairCrossingPairs(labels, rules);
                for (int pass = 0; pass < 2; pass++)
                {
                    int before = Crossings(labels);
                    foreach (var l in labels)
                    {
                        var old = l.Spot;
                        var others = With(baseObs, labels.Where(x => x != l));
                        foreach (var spot in At(others, l, old.Value, old.Value.End, rules))
                        {
                            l.Spot = spot;
                            if (Crossings(labels) < before) { before = Crossings(labels); old = l.Spot; }
                        }
                        l.Spot = old;
                    }
                    Untangle(baseObs, labels, rules);
                    if (Crossings(labels) == 0) break;
                }
                foreach (var l in labels) { l.Candidates.Clear(); l.StaticObstacles = null; }
                return rules.PreferUp;
            }


            // Each group in turn (largest first), against everything placed before it: a few orders, both ways; the best kept.
            HashSet<PlanLabel> done = new HashSet<PlanLabel>();
            foreach (List<PlanLabel> group in groups)
            {
                LabelObstacles placed = With(baseObs, done);
                double mx = group.Average(l => l.Anchor.X), my = group.Average(l => l.Anchor.Y);
                double Spread(PlanLabel l) => Math.Abs(l.Anchor.X - mx) + Math.Abs(l.Anchor.Y - my);
                List<List<PlanLabel>> orders = new List<List<PlanLabel>>
                {
                    group.OrderBy(Freedom).ThenBy(l => l.Anchor.Y).ToList(),
                    group.OrderByDescending(l => group.Count(o => Math.Abs(o.Anchor.X - l.Anchor.X) < groupReach / 2 && Math.Abs(o.Anchor.Y - l.Anchor.Y) < groupReach / 2)).ThenBy(l => l.Anchor.X).ToList()
                };
                if (group.Count >= 3)
                {
                    orders.Add(group.OrderByDescending(Spread).ToList());
                    orders.Add(group.OrderBy(Spread).ToList());
                    orders.Add(group.OrderBy(l => l.Anchor.X).ThenBy(l => l.Anchor.Y).ToList());
                }
                (int placed, double cost, Dictionary<PlanLabel, LabelSpot?> spots, bool up)? best = null;
                foreach (bool up in new[] { rules.PreferUp, !rules.PreferUp })
                    foreach (List<PlanLabel> order in orders)
                    {
                        LabelObstacles o = placed.Clone();
                        Dictionary<PlanLabel, LabelSpot?> spots = new Dictionary<PlanLabel, LabelSpot?>();
                        int ok = 0;
                        double total = 0;
                        foreach (PlanLabel l in order)
                        {
                            l.PreferUp = up;
                            // Every point still waiting (here or in groups still to come) is kept clear of.
                            List<(double X, double Y)> waiting = labels.Where(x => x != l && !done.Contains(x) && !spots.ContainsKey(x)).Select(x => x.Anchor).ToList();
                            l.Spot = ChooseFor(o, l, rules, waiting);
                            spots[l] = l.Spot;
                            if (!l.Spot.HasValue) continue;
                            ok++;
                            total += l.Spot.Value.Cost;
                            Claim(o, l);
                        }
                        if (!best.HasValue || ok > best.Value.placed || (ok == best.Value.placed && total < best.Value.cost - 1e-9)) best = (ok, total, spots, up);
                    }
                foreach (PlanLabel l in group) { l.Spot = best.Value.spots[l]; l.PreferUp = best.Value.up; done.Add(l); }
            }

            for (int pass = 0; pass < 2; pass++)
            {
                // Labels without a place, or with a leader crossing another, are placed again around all the others.
                foreach (PlanLabel l in labels)
                {
                    LabelObstacles others = With(baseObs, labels.Where(x => x != l));
                    bool crossing = l.Spot.HasValue && others.LeaderCrossings(l.Start, l.Spot.Value.End, l.Key) > 0;
                    if (l.Spot.HasValue && !crossing) continue;
                    LabelSpot? again = ChooseFor(others, l, rules);
                    if (again.HasValue && (!l.Spot.HasValue || again.Value.Cost < l.Spot.Value.Cost - 1e-9)) l.Spot = again;
                }
                Untangle(baseObs, labels, rules);
                MakeRoom(baseObs, labels, rules);
            }

            foreach (var l in labels) { l.Candidates.Clear(); l.StaticObstacles = null; }
            return labels.Count(l => l.PreferUp) * 2 >= labels.Count;
        }

        /// <summary>Structures, then straight-run patterns, then the existing free search for the remainder.</summary>
        public static bool Plan(LabelObstacles baseObs, List<PlanLabel> labels, LayoutRules rules)
        {
            foreach (var l in labels) { l.Spot = null; l.Candidates.Clear(); l.StaticObstacles = null; l.PreferUp = rules.PreferUp; }
            var o = baseObs.Clone();
            // Reserve all points even for callers that have not already done so.
            foreach (var l in labels) LabelLayout.ReserveAnchor(o, l.Anchor, l.Key);
            List<List<PlanLabel>> clusters = Clusters(labels.Where(l => !l.IsStructure).ToList(), 12);
            // A junction's own manhole label waits until its pipe labels are fanned out: placed first it takes the place beside
            // the junction and hides the places behind it from every leader.
            bool AtJunction(PlanLabel s) => clusters.Any(c => c.Any(l => Math.Abs(l.Anchor.X - s.Anchor.X) <= 12 && Math.Abs(l.Anchor.Y - s.Anchor.Y) <= 12));
            void PlaceStructure(PlanLabel l, LayoutRules sr, LabelObstacles obs)
            {
                // The nearest clear place that does not sit on other labelled points' doorstep: a box within 15 mm of a point
                // still waiting for its label costs, the more the nearer (it would wall that point in).
                List<(double X, double Y)> waiting = labels.Where(x => x != l && !x.Spot.HasValue).Select(x => x.Anchor).ToList();
                LabelSpot? best = null;
                double bestCost = double.PositiveInfinity;
                for (double radius = Math.Max(6, sr.MinLeader); radius <= sr.MaxDistance + 1e-9; radius += 2)
                {
                    if (radius > bestCost) break;
                    for (int angle = 0; angle < 72; angle++)
                    {
                        double a = angle * Math.PI / 36;
                        var spot = PatternSpot(obs, l, (l.Anchor.X + radius * Math.Cos(a), l.Anchor.Y + radius * Math.Sin(a)), sr);
                        if (!spot.HasValue) continue;
                        double cost = spot.Value.Cost + waiting.Sum(w => 40 * Math.Max(0, 1 - LabelLayout.Distance(spot.Value.Box, w) / 15));
                        if (cost < bestCost) { best = spot; bestCost = cost; }
                    }
                }
                l.Spot = best;
                Claim(obs, l);
            }
            foreach (var l in labels.Where(l => l.IsStructure && !AtJunction(l))) PlaceStructure(l, rules, o);
            // Where points crowd, leaders may be longer (see LayoutRules.CrowdedReach).
            LayoutRules far = Copy(rules, rules.PreferUp);
            far.MaxDistance = rules.Crowded;
            far.CrowdedReach = rules.Crowded;
            // Junctions first: several labelled points within a few mm are fanned out in columns beside the junction.
            // Each junction both ways (its pipe labels fanned first, or its manhole label first): the way that places more is kept.
            foreach (List<PlanLabel> cluster in clusters)
            {
                List<PlanLabel> own = labels.Where(s => s.IsStructure && !s.Spot.HasValue &&
                    cluster.Any(l => Math.Abs(l.Anchor.X - s.Anchor.X) <= 12 && Math.Abs(l.Anchor.Y - s.Anchor.Y) <= 12)).ToList();
                List<PlanLabel> members = cluster.Concat(own).ToList();
                Dictionary<PlanLabel, LabelSpot?> kept = null;
                double keptCost = 0;
                foreach (bool fanFirst in new[] { true, false })
                {
                    LabelObstacles trial = o.Clone();
                    foreach (PlanLabel m in members) m.Spot = null;
                    if (fanFirst) { Fan(trial, cluster, far); foreach (PlanLabel s in own) PlaceStructure(s, far, trial); }
                    else { foreach (PlanLabel s in own) PlaceStructure(s, far, trial); Fan(trial, cluster, far); }
                    int count = members.Count(m => m.Spot.HasValue);
                    double cost = members.Where(m => m.Spot.HasValue).Sum(m => m.Spot.Value.Cost);
                    if (kept == null || count > kept.Values.Count(v => v.HasValue) || (count == kept.Values.Count(v => v.HasValue) && cost < keptCost))
                    { kept = members.ToDictionary(m => m, m => m.Spot); keptCost = cost; }
                }
                foreach (PlanLabel m in members) { m.Spot = kept[m]; Claim(o, m); }
            }
            foreach (var run in Runs(labels.Where(l => !l.IsStructure && !l.Spot.HasValue).ToList()))
            {
                var ordered = run.Labels;
                var u = run.Axis;
                double Along((double X, double Y) p) => p.X * u.X + p.Y * u.Y;
                double Extent(PlanLabel l)
                {
                    // Classify using the measured boxes at the two perpendicular leader angles.
                    return new[] { 1, -1 }.Max(side =>
                    {
                        double dx = -u.Y * side, dy = u.X * side;
                        var b = l.Shape.BoxFor(dx, dy, dx >= 0);
                        return Math.Abs(u.X) * b.Width + Math.Abs(u.Y) * b.Height;
                    });
                }
                bool sparse = Enumerable.Range(1, ordered.Count - 1).All(i =>
                    Along(ordered[i].Anchor) - Along(ordered[i - 1].Anchor) >= Math.Max(Extent(ordered[i]), Extent(ordered[i - 1])) + rules.Gap);
                if (sparse)
                {
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        var l = ordered[i];
                        foreach (int side in new[] { i % 2 == 0 ? 1 : -1, i % 2 == 0 ? -1 : 1 })
                        {
                            for (double d = Math.Max(8, rules.MinLeader); d <= rules.MaxDistance + 1e-9; d += 2)
                            {
                                l.Spot = PatternSpot(o, l, (l.Anchor.X - u.Y * side * d, l.Anchor.Y + u.X * side * d), rules);
                                if (l.Spot.HasValue) break;
                            }
                            if (l.Spot.HasValue) break;
                        }
                        Claim(o, l);
                    }
                }
                else
                {
                    // Prefer alternating banks on near-vertical runs. Try a single bank as well when
                    // the alternating pattern cannot place the whole run.
                    bool vertical = Math.Abs(u.X) <= 0.25;
                    Dictionary<PlanLabel, LabelSpot> best = new();
                    double bestLength = double.PositiveInfinity;
                    bool bestAlternates = false;
                    foreach (LayoutRules sr in new[] { rules, far })
                    foreach (bool alternate in vertical ? new[] { true, false } : new[] { false })
                    foreach (int side in new[] { 1, -1 })
                    foreach (bool reverse in new[] { false, true })
                        {
                            if (best.Count == ordered.Count && (sr == far || (bestAlternates && !alternate))) continue;
                            var trial = Staircase(o, reverse ? ordered.AsEnumerable().Reverse().ToList() : ordered,
                                reverse ? (-u.X, -u.Y) : u, side, alternate, sr);
                            double length = trial.Values.Sum(s => s.Cost);
                            if (trial.Count > best.Count || (trial.Count == best.Count && length < bestLength))
                            { best = trial; bestLength = length; bestAlternates = alternate; }
                        }
                    foreach (var l in ordered) { l.Spot = best.TryGetValue(l, out var spot) ? spot : null; Claim(o, l); }
                }
            }
            var remaining = labels.Where(l => !l.Spot.HasValue).ToList();
            if (remaining.Count > 0) FreePlan(o, remaining, rules);
            // What is still without a place is tried once more with the longer leaders of crowded places.
            var o2 = o.Clone();
            foreach (var l in labels.Where(l => l.Spot.HasValue && remaining.Contains(l))) Claim(o2, l);
            var left = remaining.Where(l => !l.Spot.HasValue).ToList();
            if (left.Count > 0) FreePlan(o2, left, far);
            // Bring long leaders in along their existing direction where the final layout allows it.
            foreach (PlanLabel l in labels.Where(l => l.Spot.HasValue && !l.Spot.Value.Ratio.HasValue))
            {
                LabelSpot old = l.Spot.Value;
                double dx = old.End.X - l.Anchor.X, dy = old.End.Y - l.Anchor.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length <= 30) continue;
                var others = With(baseObs, labels.Where(x => x != l));
                foreach (PlanLabel other in labels.Where(x => x != l)) LabelLayout.ReserveAnchor(others, other.Anchor, other.Key);
                for (double candidate = length - 1; candidate >= Math.Max(rules.MinLeader, length - 5); candidate--)
                {
                    double scale = candidate / length;
                    var shorter = PatternSpot(others, l, (l.Anchor.X + dx * scale, l.Anchor.Y + dy * scale), far);
                    if (shorter.HasValue) l.Spot = shorter;
                }
            }
            return labels.Count(l => l.PreferUp) * 2 >= labels.Count;
        }

        /// <summary>Groups of three or more labelled points chained closer than <paramref name="reach"/> (mm): a junction.</summary>
        private static List<List<PlanLabel>> Clusters(List<PlanLabel> labels, double reach) =>
            Groups(labels, reach).Where(g => g.Count >= 3 &&
                g.Max(l => l.Anchor.X) - g.Min(l => l.Anchor.X) <= 2.5 * reach &&
                g.Max(l => l.Anchor.Y) - g.Min(l => l.Anchor.Y) <= 2.5 * reach &&
                !IsStraight(g)).ToList();

        private static bool IsStraight(List<PlanLabel> labels)
        {
            var ends = (from a in labels from b in labels
                        let dx = b.Anchor.X - a.Anchor.X
                        let dy = b.Anchor.Y - a.Anchor.Y
                        orderby dx * dx + dy * dy descending
                        select (Start: a.Anchor, X: dx, Y: dy)).First();
            double length = Math.Sqrt(ends.X * ends.X + ends.Y * ends.Y);
            return length > 1e-6 && labels.All(l =>
                Math.Abs((l.Anchor.X - ends.Start.X) * ends.Y - (l.Anchor.Y - ends.Start.Y) * ends.X) <= 1e-6 * length);
        }

        /// <summary>
        /// A junction's labels fanned out: columns of boxes left and right of it, each label taking the nearest free place of its
        /// column grid whose leader reaches it without crossing a label, text or leader already there. Labels alternate sides
        /// (left points to the left, right points to the right), so the leaders spread instead of walling the junction in.
        /// </summary>
        private static void Fan(LabelObstacles o, List<PlanLabel> cluster, LayoutRules far)
        {
            double cx = cluster.Average(l => l.Anchor.X), cy = cluster.Average(l => l.Anchor.Y);
            List<PlanLabel> order = cluster.OrderBy(l => l.Anchor.X).ThenBy(l => l.Anchor.Y).ThenBy(l => l.Key, StringComparer.Ordinal).ToList();
            double w = cluster.Max(l => l.Shape.BoxFor(1, 0, true).Width), h = cluster.Max(l => l.Shape.BoxFor(1, 0, true).Height);
            for (int i = 0; i < order.Count; i++)
            {
                PlanLabel l = order[i];
                // The left half of the points goes left first, the right half right first.
                int first = i < (order.Count + 1) / 2 ? -1 : 1;
                LabelSpot? best = null;
                foreach (int side in new[] { first, -first })
                {
                    for (int col = 0; col < 3 && !best.HasValue; col++)
                    {
                        double x = cx + side * (20 + col * (w + far.Gap + 2));
                        foreach (int k in Enumerable.Range(0, 2 * (int)Math.Ceiling(far.MaxDistance / (h + far.Gap)) + 1).Select(n => n % 2 == 0 ? -n / 2 : (n + 1) / 2))
                        {
                            LabelSpot? s = PatternSpot(o, l, (x, cy + k * (h + far.Gap)), far);
                            if (s.HasValue && (!best.HasValue || s.Value.Cost < best.Value.Cost)) best = s;
                        }
                    }
                    if (best.HasValue) break;
                }
                l.Spot = best;
                Claim(o, l);
            }
        }

        // All pattern boxes are upright. In particular, do not use HullFor to shrink a sampled box.
        private static LabelSpot? PatternSpot(LabelObstacles o, PlanLabel l, (double X, double Y) end, LayoutRules r)
        {
            double dx = end.X - l.Anchor.X, dy = end.Y - l.Anchor.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < Math.Max(1e-9, r.MinLeader) || length > r.MaxDistance + 1e-9 ||
                o.LeaderCrossesText(l.Anchor, end, l.Key) || o.LeaderCrossings(l.Anchor, end, l.Key) > 0) return null;
            foreach (bool right in dx > 1e-9 ? new[] { true } : dx < -1e-9 ? new[] { false } : new[] { true, false })
            {
                var box = l.Shape.BoxFor(dx, dy, right).Offset(end.X, end.Y);
                if (box.Contains(l.Anchor) || (l.InArea != null && !l.InArea(box)) || o.Blocker(box, r.Gap, l.Networks, l.Key) != null) continue;
                return new LabelSpot { Box = box, End = end, Anchor = l.Anchor, Cost = length, Direction = Math.Abs(dx) < 1e-9 ? (dy > 0 ? 0 : 1) : Math.Abs(dy) < 1e-9 ? 2 : 3 };
            }
            return null;
        }

        /// <summary>
        /// A crowded run stepped out on one side of its line, as it is done by hand: the labels in the order of their points, each
        /// at the nearest clear place whose box lies beside or behind its own point (never ahead, where the next points' leaders
        /// must pass), so each next box lands a step further out and no leader crosses another.
        /// </summary>
        private static Dictionary<PlanLabel, LabelSpot> Staircase(LabelObstacles obstacles, List<PlanLabel> labels,
            (double X, double Y) u, int side, bool alternate, LayoutRules r)
        {
            var o = obstacles.Clone();
            var result = new Dictionary<PlanLabel, LabelSpot>();
            for (int i = 0; i < labels.Count; i++)
            {
                PlanLabel l = labels[i];
                int bank = alternate && i % 2 != 0 ? -side : side;
                var n = (X: -u.Y * bank, Y: u.X * bank);
                LabelSpot? best = null;
                for (double d = Math.Max(4, r.MinLeader); d <= r.MaxDistance + 1e-9; d += 2)
                {
                    // Once a place is found, deeper rows cannot be nearer than it.
                    if (best.HasValue && d > best.Value.Cost) break;
                    for (double shift = 4; shift >= -r.MaxDistance - 1e-9; shift -= 2)
                    {
                        var end = (l.Anchor.X + u.X * shift + n.X * d, l.Anchor.Y + u.Y * shift + n.Y * d);
                        var spot = PatternSpot(o, l, end, r);
                        if (!spot.HasValue) continue;
                        // The whole box behind its own point: nothing of it under the points still to come.
                        double ahead = spot.Value.Box.Corners().Max(c => (c.X - l.Anchor.X) * u.X + (c.Y - l.Anchor.Y) * u.Y);
                        if (!alternate && ahead > 1.0) continue;
                        if (!best.HasValue || spot.Value.Cost < best.Value.Cost - 1e-9) best = spot;
                    }
                }
                if (!best.HasValue) continue;
                result[l] = best.Value;
                LabelLayout.Claim(o, best.Value, l.Anchor, l.Key);
            }
            return result;
        }
        /// <summary>Largest straight neighbour sets first, fitted to a common line within 3 paper mm.</summary>
        private static List<(List<PlanLabel> Labels, (double X, double Y) Axis)> Runs(List<PlanLabel> pending)
        {
            var runs = new List<(List<PlanLabel>, (double X, double Y))>();
            while (pending.Count >= 2)
            {
                List<PlanLabel> best = null;
                (double X, double Y) axis = (1, 0);
                for (int i = 0; i < pending.Count; i++)
                    for (int j = i + 1; j < pending.Count; j++)
                    {
                        double dx = pending[j].Anchor.X - pending[i].Anchor.X, dy = pending[j].Anchor.Y - pending[i].Anchor.Y;
                        double len = Math.Sqrt(dx * dx + dy * dy);
                        if (len < 1e-6) continue;
                        var candidates = pending.Where(l => Math.Abs((l.Anchor.X - pending[i].Anchor.X) * dy - (l.Anchor.Y - pending[i].Anchor.Y) * dx) / len <= 3).ToList();
                        if (best == null || candidates.Count > best.Count) { best = candidates; axis = (dx / len, dy / len); }
                    }
                if (best == null) break;
                double mx = best.Average(l => l.Anchor.X), my = best.Average(l => l.Anchor.Y);
                double xx = best.Sum(l => Math.Pow(l.Anchor.X - mx, 2)), yy = best.Sum(l => Math.Pow(l.Anchor.Y - my, 2));
                double xy = best.Sum(l => (l.Anchor.X - mx) * (l.Anchor.Y - my));
                double angle = Math.Atan2(2 * xy, xx - yy) / 2;
                axis = (Math.Cos(angle), Math.Sin(angle));
                if (axis.X < -1e-9 || (Math.Abs(axis.X) <= 1e-9 && axis.Y < 0)) axis = (-axis.X, -axis.Y);
                var u = axis;
                best = best.Where(l => Math.Abs((l.Anchor.Y - my) * u.X - (l.Anchor.X - mx) * u.Y) <= 3 + 1e-9).ToList();
                if (best.Count < 2) break;
                runs.Add((best.OrderBy(l => l.Anchor.X * u.X + l.Anchor.Y * u.Y).ThenBy(l => l.Key, StringComparer.Ordinal).ToList(), u));
                pending = pending.Except(best).ToList();
            }
            return runs;
        }

        private static void RepairCrossingPairs(List<PlanLabel> labels, LayoutRules rules)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                bool changed = false;
                for (int i = 0; i < labels.Count; i++)
                    for (int j = i + 1; j < labels.Count; j++)
                    {
                        var a = labels[i]; var b = labels[j];
                        var oldA = a.Spot.Value; var oldB = b.Spot.Value;
                        bool pairCrosses = LabelLayout.Cross(oldA.Anchor, oldA.End, oldB.Anchor, oldB.End);
                        if (!pairCrosses && LabelLayout.Distance(oldA.Box, oldB.Box.Center) > 60) continue;
                        var others = labels.Where(l => l != a && l != b).Select(l => l.Spot.Value).ToList();
                        int Crossed(LabelSpot s) => others.Count(o => LabelLayout.Cross(s.Anchor, s.End, o.Anchor, o.End));
                        int before = Crossed(oldA) + Crossed(oldB) + (pairCrosses ? 1 : 0);
                        if (before == 0) continue;
                        List<(LabelSpot spot, int crossed)> Options(PlanLabel l, LabelSpot old)
                        {
                            var options = new List<(LabelSpot spot, int crossed)> { (old, Crossed(old)) };
                            foreach (var entry in l.Candidates.Where(p => p.Key.Up == rules.PreferUp))
                                foreach (var candidate in entry.Value)
                                {
                                    int crossed = 0; bool clear = true;
                                    foreach (var other in others)
                                    {
                                        if (!Compatible(candidate, other, rules.Gap)) { clear = false; break; }
                                        if (LabelLayout.Cross(candidate.Anchor, candidate.End, other.Anchor, other.End) && ++crossed >= before) { clear = false; break; }
                                    }
                                    if (!clear) continue;
                                    var spot = candidate;
                                    if (Math.Abs(entry.Key.X - l.Anchor.X) > 1e-9 || Math.Abs(entry.Key.Y - l.Anchor.Y) > 1e-9)
                                        spot.Ratio = l.AltAnchors.First(p => Math.Abs(p.X - entry.Key.X) < 1e-9 && Math.Abs(p.Y - entry.Key.Y) < 1e-9).Ratio;
                                    options.Add((spot, crossed));
                                }
                            var representatives = options.OrderBy(p => p.crossed).ThenBy(p => p.spot.Cost)
                                .GroupBy(p => (Math.Floor(p.spot.End.X / 4), Math.Floor(p.spot.End.Y / 4), p.spot.Ratio, p.spot.Box.MinX < p.spot.End.X))
                                .Select(g => g.First()).ToList();
                            return representatives;
                        }
                        var oa = Options(a, oldA); var ob = Options(b, oldB);
                        foreach (var sa in oa)
                        {
                            if (sa.crossed >= before) break;
                            foreach (var sb in ob)
                            {
                                if (sa.crossed + sb.crossed >= before) break;
                                int crossed = sa.crossed + sb.crossed + (LabelLayout.Cross(sa.spot.Anchor, sa.spot.End, sb.spot.Anchor, sb.spot.End) ? 1 : 0);
                                if (crossed >= before || !Compatible(sa.spot, sb.spot, rules.Gap)) continue;
                                a.Spot = sa.spot; b.Spot = sb.spot; before = crossed; changed = true;
                            }
                        }
                    }
                if (!changed) break;
            }
        }

        private static void OptimizeDense(List<PlanLabel> labels, LayoutRules rules)
        {
            for (int pass = 0; pass < 5; pass++)
            {
                bool changed = false;
                foreach (var a in labels)
                {
                    var others = labels.Where(l => l != a).Select(l => l.Spot.Value).ToList();
                    var old = a.Spot.Value;
                    int best = others.Count(s => LabelLayout.Cross(old.Anchor, old.End, s.Anchor, s.End));
                    if (best == 0) continue;
                    foreach (var entry in a.Candidates.Where(p => p.Key.Up == rules.PreferUp))
                        foreach (var candidate in entry.Value)
                        {
                            if (best == 0) break;
                            int crossed = 0;
                            bool clear = true;
                            foreach (var b in others)
                            {
                                if (!Compatible(candidate, b, rules.Gap)) { clear = false; break; }
                                if (LabelLayout.Cross(candidate.Anchor, candidate.End, b.Anchor, b.End) && ++crossed >= best) { clear = false; break; }
                            }
                            if (!clear) continue;
                            var spot = candidate;
                            if (Math.Abs(entry.Key.X - a.Anchor.X) > 1e-9 || Math.Abs(entry.Key.Y - a.Anchor.Y) > 1e-9)
                                spot.Ratio = a.AltAnchors.First(p => Math.Abs(p.X - entry.Key.X) < 1e-9 && Math.Abs(p.Y - entry.Key.Y) < 1e-9).Ratio;
                            a.Spot = spot; best = crossed; changed = true;
                            if (best == 0) break;
                        }
                }
                if (!changed) break;
            }
        }

        private static bool Compatible(LabelSpot a, LabelSpot b, double gap)
        {
            if (a.Hull == null && b.Hull == null)
            {
                if (a.Box.Grow(gap).Overlaps(b.Box)) return false;
                if (a.Box.Hits(b.Anchor, b.End) || b.Box.Hits(a.Anchor, a.End)) return false;
                return true;
            }
            // Measured outlines: the texts apart by the gap, and neither leader through the other's text.
            if (!LabelLayout.Separated(a.Outline, b.Outline, gap)) return false;
            if (!LabelLayout.Separated(a.Outline, new[] { b.Anchor, b.End }, 0) || !LabelLayout.Separated(b.Outline, new[] { a.Anchor, a.End }, 0)) return false;
            return true;
        }

        // Forward checking prevents an easy label from consuming the last escape route of a crowded one.
        // Domains are the same statically clear grid candidates used by the normal planner, including pipe slides.
        private static bool SolveDense(List<PlanLabel> labels, LayoutRules rules)
        {
            var domains = new Dictionary<PlanLabel, List<LabelSpot>>();
            foreach (var l in labels)
            {
                var domain = new List<LabelSpot>();
                foreach (var entry in l.Candidates.Where(p => p.Key.Up == rules.PreferUp))
                    foreach (var candidate in entry.Value)
                    {
                        var spot = candidate;
                        if (Math.Abs(entry.Key.X - l.Anchor.X) > 1e-9 || Math.Abs(entry.Key.Y - l.Anchor.Y) > 1e-9)
                        {
                            var alt = l.AltAnchors.First(p => Math.Abs(p.X - entry.Key.X) < 1e-9 && Math.Abs(p.Y - entry.Key.Y) < 1e-9);
                            spot.Ratio = alt.Ratio;
                            spot.Cost += rules.SlideCost * Math.Abs(alt.Ratio - (l.Ratio ?? 0.5));
                        }
                        domain.Add(spot);
                    }
                if (domain.Count == 0) return false;
                domains[l] = domain.OrderBy(s => s.Cost).ToList();
            }
            int nodes = 0;
            var assigned = new Dictionary<PlanLabel, LabelSpot>();
            bool Search(Dictionary<PlanLabel, List<LabelSpot>> remaining)
            {
                if (nodes > 256) return false;
                if (remaining.Count == 0) return true;
                var next = remaining.OrderBy(p => p.Value.Count).First();
                var candidates = next.Value.Select(s => (spot: s, crossed: assigned.Values.Count(p => LabelLayout.Cross(s.Anchor, s.End, p.Anchor, p.End))))
                    .OrderBy(p => p.crossed).ThenBy(p => p.spot.Cost);
                var samples = remaining.Where(p => p.Key != next.Key).Select(p => p.Value.Where((s, i) => i % Math.Max(1, p.Value.Count / 32) == 0).Take(32).ToList()).ToList();
                var representatives = candidates.GroupBy(p => (Math.Floor(p.spot.End.X / 10), Math.Floor(p.spot.End.Y / 10), p.spot.Ratio, p.spot.Box.MinX < p.spot.End.X)).Select(g => g.First()).ToList();
                var ranked = representatives.Where((p, i) => i < 8 || i % Math.Max(1, representatives.Count / 200) == 0)
                    .Select(p => (p.spot, p.crossed, blocked: samples.Sum(sample => sample.Count(s => !Compatible(p.spot, s, rules.Gap)) / (double)sample.Count)))
                    .OrderBy(p => p.crossed).ThenBy(p => p.blocked).ThenBy(p => p.spot.Cost);
                foreach (var option in ranked)
                {
                    if (++nodes > 256) return false;
                    var reduced = new Dictionary<PlanLabel, List<LabelSpot>>();
                    bool possible = true;
                    foreach (var domain in remaining)
                    {
                        if (domain.Key == next.Key) continue;
                        var free = domain.Value.Where(s => Compatible(option.spot, s, rules.Gap)).ToList();
                        if (free.Count == 0) { possible = false; break; }
                        reduced[domain.Key] = free;
                    }
                    if (!possible) continue;
                    assigned[next.Key] = option.spot;
                    if (Search(reduced)) return true;
                    assigned.Remove(next.Key);
                    if (nodes > 256) break;
                }
                return false;
            }
            bool solved = Search(domains);
            if (solved) foreach (var l in labels) { l.Spot = assigned[l]; l.PreferUp = rules.PreferUp; }
            return solved;
        }

        /// <summary>
        /// Two labels whose leaders cross trade leader ends (each keeps its own shape, its text on the side of its new end) when
        /// both are clear there, or are placed again one after the other: a crossing nearly always goes that way.
        /// </summary>
        public static int Untangle(LabelObstacles baseObs, List<PlanLabel> labels, LayoutRules rules)
        {
            int swaps = 0;
            for (int i = 0; i < labels.Count; i++)
                for (int j = i + 1; j < labels.Count; j++)
                {
                    PlanLabel a = labels[i], b = labels[j];
                    if (!a.Spot.HasValue || !b.Spot.HasValue || !LabelLayout.Cross(a.Start, a.Spot.Value.End, b.Start, b.Spot.Value.End)) continue;
                    LabelObstacles others = With(baseObs, labels.Where(x => x != a && x != b));
                    int before = Crossings(labels);
                    LabelSpot? oldA = a.Spot, oldB = b.Spot;
                    List<(LabelSpot? na, LabelSpot? nb)> tries = new List<(LabelSpot?, LabelSpot?)>();
                    foreach (LabelSpot sa in At(others, a, oldA.Value, oldB.Value.End, rules))
                    {
                        LabelObstacles o = others.Clone();
                        LabelLayout.Claim(o, sa, sa.Ratio.HasValue ? sa.Anchor : a.Anchor, a.Key);
                        foreach (LabelSpot sb in At(o, b, oldB.Value, oldA.Value.End, rules)) tries.Add((sa, sb));
                    }
                    foreach ((PlanLabel first, PlanLabel second) in new[] { (a, b), (b, a) })
                    {
                        LabelObstacles o = others.Clone();
                        first.Spot = ChooseFor(o, first, rules);
                        if (!first.Spot.HasValue) continue;
                        LabelSpot f1 = first.Spot.Value;
                        Claim(o, first);
                        LabelSpot? f2 = ChooseFor(o, second, rules);
                        if (f2.HasValue) tries.Add(first == a ? (f1, f2) : (f2, f1));
                    }
                    bool better = false;
                    foreach ((LabelSpot? na, LabelSpot? nb) in tries)
                    {
                        a.Spot = na; b.Spot = nb;
                        if (Crossings(labels) < before) { better = true; break; }
                    }
                    if (better) swaps++;
                    else { a.Spot = oldA; b.Spot = oldB; }
                }
            return swaps;
        }

        /// <summary>
        /// A label left without a place moves each of its nearest placed neighbours (up to three) out of the way in turn: it is
        /// placed first, then the neighbour again (along its pipe too, if it may slide). Kept when both find a place and no
        /// crossing is added.
        /// </summary>
        public static int MakeRoom(LabelObstacles baseObs, List<PlanLabel> labels, LayoutRules rules)
        {
            int made = 0;
            foreach (PlanLabel a in labels.Where(x => !x.Spot.HasValue).ToList())
            {
                foreach (PlanLabel b in labels.Where(x => x != a && x.Spot.HasValue).OrderBy(x => LabelLayout.Distance(x.Spot.Value.Box, a.Anchor)).Take(3).ToList())
                {
                    int before = Crossings(labels);
                    LabelSpot? oldB = b.Spot;
                    LabelObstacles o = With(baseObs, labels.Where(x => x != a && x != b));
                    a.Spot = ChooseFor(o, a, rules, new[] { b.Anchor });
                    if (!a.Spot.HasValue) { b.Spot = oldB; continue; }
                    Claim(o, a);
                    b.Spot = ChooseFor(o, b, rules);
                    if (b.Spot.HasValue && Crossings(labels) <= before) { made++; break; }
                    a.Spot = null;
                    b.Spot = oldB;
                }
            }
            return made;
        }

        /// <summary>The label with its leader ending at <paramref name="end"/>, for each side its text may take there that is clear.</summary>
        private static IEnumerable<LabelSpot> At(LabelObstacles o, PlanLabel l, LabelSpot from, (double X, double Y) end, LayoutRules rules)
        {
            foreach (var spot in AtAnchor(o, l, from, end, rules)) yield return spot;
            var primary = from; primary.Ratio = null; primary.Anchor = l.Anchor;
            foreach (var spot in AtAnchor(o, l, primary, end, rules)) yield return spot;
            foreach (var alt in l.AltAnchors)
            {
                var slid = from; slid.Anchor = (alt.X, alt.Y); slid.Ratio = alt.Ratio;
                foreach (var spot in AtAnchor(o, l, slid, end, rules)) yield return spot;
            }
        }

        private static IEnumerable<LabelSpot> AtAnchor(LabelObstacles o, PlanLabel l, LabelSpot from, (double X, double Y) end, LayoutRules rules)
        {
            (double X, double Y) start = from.Ratio.HasValue ? from.Anchor : l.Anchor;
            double dx = end.X - start.X, dy = end.Y - start.Y, length = Math.Sqrt(dx * dx + dy * dy);
            if (length > rules.MaxDistance + 1e-9 || length < Math.Max(rules.MinLeader, 1e-9) || o.LeaderCrossesText(start, end, l.Key) ||
                o.LeaderCrossings(start, end, l.Key) > 0) yield break;
            foreach (bool right in dx > 1e-9 ? new[] { true } : dx < -1e-9 ? new[] { false } : new[] { true, false })
            {
                LRect box = l.Shape.BoxFor(dx, dy, right).Offset(end.X, end.Y);
                LabelSpot spot = new LabelSpot
                {
                    Box = box, Hull = LabelSpot.At(l.Shape.HullFor(dx, dy), end.X, end.Y), End = end, Anchor = start, Ratio = from.Ratio, Direction = 3,
                    Cost = length / rules.MaxDistance + rules.SlantCost * LabelLayout.OffAxis(dx, dy) / 45
                };
                if (spot.Covers(start) || (l.InArea != null && !l.InArea(box))) continue;
                if (o.Blocker(spot.Outline, rules.Gap, l.Networks, l.Key) != null) continue;
                yield return spot;
                if (l.Shape.Samples.Count > 0) yield break;
            }
        }
    }
}
