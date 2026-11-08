using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

// One viewport's label arrangement as data (paper millimetres, in the viewport's frame): everything the labels keep clear of,
// every dragged label to place, and the rules. Written from a real sheet by the Annotation Studio ("Export arrangement case")
// and replayed by the tests, so the planner is measured on real crowded sheets without Civil 3D. Geometry only.
namespace C3DProTools
{
    internal sealed class ArrangeCase
    {
        public sealed class Obstacle
        {
            public string Group { get; set; } = "";
            public string Kind { get; set; } = "";
            public string Network { get; set; }
            public string Part { get; set; }
            /// <summary>The outline's points, each [x, y].</summary>
            public List<double[]> Points { get; set; } = new List<double[]>();
        }

        public sealed class Label
        {
            public string Key { get; set; } = "";
            public double[] Anchor { get; set; } = new double[2];
            /// <summary>Box relative to the leader end, each [minX, minY, maxX, maxY]: right, left, and (when measured) up / down.</summary>
            public double[] Right { get; set; }
            public double[] Left { get; set; }
            public double[] UpRight { get; set; }
            public double[] UpLeft { get; set; }
            public double[] DownRight { get; set; }
            public double[] DownLeft { get; set; }
            public List<string> Networks { get; set; } = new List<string>();
            /// <summary>Points along its pipe it may slide to, each [x, y, ratio], and its own ratio.</summary>
            public List<double[]> Alternatives { get; set; } = new List<double[]>();
            public double? Ratio { get; set; }
            /// <summary>The profile view it must stay inside, [minX, minY, maxX, maxY] (null in plan).</summary>
            public double[] Region { get; set; }
        }

        public string Sheet { get; set; } = "";
        public double MaxDistance { get; set; } = 60;
        public double Gap { get; set; } = 1.5;
        public double Margin { get; set; } = 3;
        /// <summary>The viewport outline on paper, each [x, y].</summary>
        public List<double[]> Viewport { get; set; } = new List<double[]>();
        public List<Obstacle> Obstacles { get; set; } = new List<Obstacle>();
        public List<Label> Labels { get; set; } = new List<Label>();

        private static double[] R(LRect r) => new[] { r.MinX, r.MinY, r.MaxX, r.MaxY };
        private static LRect R(double[] a) => new LRect(a[0], a[1], a[2], a[3]);
        private static LRect? Rn(double[] a) => a == null ? (LRect?)null : R(a);

        public LayoutRules Rules() => new LayoutRules { MaxDistance = MaxDistance, Gap = Gap, MinLeader = 3, Step = 2 };

        public void AddLabel(PlanLabel p, LRect? region)
        {
            Labels.Add(new Label
            {
                Key = p.Key, Anchor = new[] { p.Anchor.X, p.Anchor.Y }, Right = R(p.Shape.Right), Left = R(p.Shape.Left),
                UpRight = p.Shape.UpRight.HasValue ? R(p.Shape.UpRight.Value) : null, UpLeft = p.Shape.UpLeft.HasValue ? R(p.Shape.UpLeft.Value) : null,
                DownRight = p.Shape.DownRight.HasValue ? R(p.Shape.DownRight.Value) : null, DownLeft = p.Shape.DownLeft.HasValue ? R(p.Shape.DownLeft.Value) : null,
                Networks = p.Networks?.ToList() ?? new List<string>(), Alternatives = p.AltAnchors.Select(a => new[] { a.X, a.Y, a.Ratio }).ToList(), Ratio = p.Ratio,
                Region = region.HasValue ? R(region.Value) : null
            });
        }

        public void AddObstacles(LabelObstacles o)
        {
            foreach ((ObstacleGroup group, string kind, string network, string part, List<(double X, double Y)> shape) in o.Items())
                Obstacles.Add(new Obstacle { Group = group.ToString(), Kind = kind, Network = network, Part = part, Points = shape.Select(p => new[] { p.X, p.Y }).ToList() });
        }

        /// <summary>True when the point lies inside the viewport outline, at least the margin from it.</summary>
        public static bool Inside(IReadOnlyList<(double X, double Y)> outline, double margin, (double X, double Y) p)
        {
            if (!LabelLayout.InPolygon(outline, p)) return false;
            for (int i = 0; i < outline.Count; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Count];
                double abx = b.X - a.X, aby = b.Y - a.Y, len2 = abx * abx + aby * aby;
                double t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2));
                double dx = p.X - (a.X + abx * t), dy = p.Y - (a.Y + aby * t);
                if (Math.Sqrt(dx * dx + dy * dy) < margin) return false;
            }
            return true;
        }

        public LabelObstacles BuildObstacles()
        {
            LabelObstacles o = new LabelObstacles();
            foreach (Obstacle ob in Obstacles)
            {
                ObstacleGroup g = (ObstacleGroup)Enum.Parse(typeof(ObstacleGroup), ob.Group);
                List<(double X, double Y)> pts = ob.Points.Select(p => (p[0], p[1])).ToList();
                if (pts.Count == 2) o.AddSegment(pts[0], pts[1], g, ob.Kind, ob.Network, ob.Part);
                else o.AddPolygon(pts, g, ob.Kind, ob.Network, ob.Part);
            }
            return o;
        }

        public List<PlanLabel> BuildLabels()
        {
            List<(double X, double Y)> outline = Viewport.Select(p => (p[0], p[1])).ToList();
            return Labels.Select(l =>
            {
                LRect? region = Rn(l.Region);
                return new PlanLabel
                {
                    Key = l.Key, Anchor = (l.Anchor[0], l.Anchor[1]),
                    Shape = new DraggedShape { Right = R(l.Right), Left = R(l.Left), UpRight = Rn(l.UpRight), UpLeft = Rn(l.UpLeft), DownRight = Rn(l.DownRight), DownLeft = Rn(l.DownLeft) },
                    Networks = new HashSet<string>(l.Networks), AltAnchors = l.Alternatives.Select(a => (a[0], a[1], a[2])).ToList(), Ratio = l.Ratio,
                    InArea = b => b.Corners().All(p => Inside(outline, Margin, p) && (!region.HasValue || region.Value.Contains(p)))
                };
            }).ToList();
        }

        public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

        public static ArrangeCase FromJson(string json) => JsonSerializer.Deserialize<ArrangeCase>(json);

        public static ArrangeCase Load(string path) => FromJson(File.ReadAllText(path));
    }
}
