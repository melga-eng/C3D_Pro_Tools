using C3DProTools.NavisLink;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace C3DProTools
{
    internal enum ImportKind { Pipe, Box, Structure, Solid, Skip }

    internal enum ImportConfidence { High, Medium, Low }

    /// <summary>One Navisworks item read and measured, ready to be matched to a Civil 3D part. Lengths are metres.</summary>
    internal sealed class ImportCandidate
    {
        public NavisElement Source;
        public string Name = "", Guid = "";
        /// <summary>"Civil 3D" when the item came from a Civil 3D drawing (exact properties), otherwise "Geometry".</summary>
        public string Origin = "Geometry";
        public ImportKind Detected, Action;

        // Pipes and boxes
        public V3 Start, End;
        /// <summary>A curved Civil 3D pipe (Pipe Radius, Radius Point): the plan radius (0 = straight), a point halfway along the arc, and its turn.</summary>
        public double CurveRadius;
        public V3 CurveMid;
        public bool CurveClockwise;
        public bool IsCurved => CurveRadius > 0;
        public bool Circular = true;
        public double OuterWidth, OuterHeight, InnerWidth, InnerHeight, Wall;
        /// <summary>Barrel centres across the pipe (metres, along <see cref="BarrelAxis"/>); one entry (0,0) for a single pipe.</summary>
        public List<(double u, double w)> Barrels = new List<(double u, double w)> { (0, 0) };
        public V3 BarrelAxis = new V3(1, 0, 0), BarrelUp = new V3(0, 0, 1);

        // Structures
        public double X, Y, Rotation, Rim, Sump, Bottom;
        public bool StructCircular;
        public double StructDiameter, StructWidth, StructLength;

        public string Material = "", PartSizeName = "", Network = "", Layer = "";
        public string StartStructure = "", EndStructure = "";

        public LinearFit Fit;
        public FootprintFit Footprint;
        /// <summary>Largest difference found between the properties and the measured geometry (metres).</summary>
        public double Deviation;
        public ImportConfidence Confidence = ImportConfidence.High;
        public readonly List<string> Notes = new List<string>();

        public double Length => (End - Start).Length;
        public bool IsLinear => Action == ImportKind.Pipe || Action == ImportKind.Box;

        public string SizeText
        {
            get
            {
                string b = Barrels.Count > 1 ? $"{Barrels.Count} × " : "";
                switch (Detected)
                {
                    case ImportKind.Pipe: return InnerWidth > 0 ? $"{b}Ø{Mm(InnerWidth)} / {Mm(OuterWidth)} mm" : $"{b}outer Ø{Mm(OuterWidth)} mm";
                    case ImportKind.Box:
                        // Width is always across the duct (horizontal), height always vertical.
                        return InnerWidth > 0 ? $"{b}W {Mm(InnerWidth)} × H {Mm(InnerHeight)} (outer {Mm(OuterWidth)} × {Mm(OuterHeight)}) mm"
                                              : $"{b}outer W {Mm(OuterWidth)} × H {Mm(OuterHeight)} mm";
                    case ImportKind.Structure:
                        return StructCircular ? $"Ø{Mm(StructDiameter)} mm, H {Rim - Sump:0.00} m" : $"{Mm(StructWidth)} × {Mm(StructLength)} mm, H {Rim - Sump:0.00} m";
                    default: return Source?.Triangles != null ? $"{Source.Triangles.Length / 9} triangles" : "";
                }
            }
        }

        public static string Mm(double metres) => (metres * 1000).ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads what each Navisworks item is. Civil 3D items carry exact properties (centreline, inner/outer size, wall, material),
    /// which are used and checked against the measured mesh; anything else is measured from its geometry alone.
    /// </summary>
    internal static class NavisInterpreter
    {
        /// <summary>Property and geometry may differ by this much before the row is flagged.</summary>
        public const double HighTolerance = 0.005, MediumTolerance = 0.025;

        public static ImportCandidate Read(NavisElement e)
        {
            Props p = new Props(e);
            ImportCandidate c = new ImportCandidate { Source = e, Name = e.Name, Guid = e.Guid, Layer = e.Layer };
            c.Network = p.Text("Network name");
            c.Material = p.Text("Material", "Part Data") is string m && m.Length > 0 ? m : GenericMaterial(p);
            c.PartSizeName = p.Text("Part Size Name");

            string cls = e.ClassName ?? "";
            if (cls == "AECC_PIPE" && p.Has("Pipe Start Easting")) ReadCivilPipe(c, p);
            else if (cls == "AECC_STRUCTURE" && p.Has("Structure Easting")) ReadCivilStructure(c, p);
            else ReadFromGeometry(c, p);

            c.Action = c.Detected;
            Grade(c);
            return c;
        }

        // ---------------- Civil 3D pipes ----------------

        private static void ReadCivilPipe(ImportCandidate c, Props p)
        {
            c.Origin = "Civil 3D";
            c.Start = new V3(p.Num("Pipe Start Easting"), p.Num("Pipe Start Northing"), p.Num("Start Centerline Elevation"));
            c.End = new V3(p.Num("Pipe End Easting"), p.Num("Pipe End Northing"), p.Num("End Centerline Elevation"));
            string shape = p.Text("Cross Sectional Shape");
            c.Circular = !shape.Equals("Rectangular", StringComparison.OrdinalIgnoreCase);
            c.Wall = p.Len("Wall Thickness");
            c.InnerWidth = p.Len("Inner Diameter or Width");
            double rise = p.Num("Start Crown Elevation") - p.Num("Start Invert Elevation");
            c.InnerHeight = c.Circular ? c.InnerWidth : (rise > 0 ? rise : c.InnerWidth);
            c.OuterWidth = p.Len("Outer Diameter or Width");
            if (c.OuterWidth <= 0) c.OuterWidth = c.InnerWidth + 2 * c.Wall;
            c.OuterHeight = c.Circular ? c.OuterWidth : c.InnerHeight + 2 * c.Wall;
            c.StartStructure = p.Text("Start Structure");
            c.EndStructure = p.Text("End Structure");
            c.Detected = c.Circular ? ImportKind.Pipe : ImportKind.Box;
            if (!shape.Equals("Circular", StringComparison.OrdinalIgnoreCase) && c.Circular)
                c.Notes.Add($"{shape} section is brought in as circular.");

            // A curved pipe: built as an arc from its properties. The straight mesh fit does not apply to it.
            double radius = p.LenWhere(n => n.IndexOf("Radius", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("Point", StringComparison.OrdinalIgnoreCase) < 0);
            double cx = p.NumWhere(n => n.IndexOf("Radius Point", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("East", StringComparison.OrdinalIgnoreCase) >= 0);
            double cy = p.NumWhere(n => n.IndexOf("Radius Point", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("North", StringComparison.OrdinalIgnoreCase) >= 0);
            if (radius > 0 && !double.IsNaN(cx) && !double.IsNaN(cy))
            {
                if (Arc(c.Start, c.End, cx, cy, radius, out V3 mid, out bool clockwise, out string problem))
                {
                    c.CurveRadius = radius;
                    c.CurveMid = mid;
                    c.CurveClockwise = clockwise;
                    c.Notes.Add($"Curved pipe, radius {radius:0.000} m: built as a curved pipe.");
                    c.Fit = Fit(c.Source);
                    return;
                }
                c.Notes.Add($"Has a radius ({radius:0.000} m) but {problem}; brought in straight.");
            }

            LinearFit fit = c.Fit = Fit(c.Source);
            if (fit == null) { c.Notes.Add("The mesh could not be measured; the Civil 3D properties are used as they are."); c.Confidence = ImportConfidence.Medium; return; }

            // Several pipes modelled as one object (twin culverts): one Civil 3D pipe per barrel, positioned from the mesh.
            if (fit.Barrels.Count > 1)
            {
                c.Barrels = fit.Barrels;
                c.BarrelAxis = fit.U;
                c.BarrelUp = fit.W;
                c.Notes.Add($"{fit.Barrels.Count} barrels in one object; each becomes its own pipe, {Spacing(fit):0.000} m apart.");
            }

            double endError = EndError(c.Start, c.End, fit);
            double sizeError = c.Circular ? Math.Abs(fit.OuterDiameter - c.OuterWidth)
                : Math.Max(Math.Abs(fit.Width - c.OuterWidth), Math.Abs(fit.Height - c.OuterHeight));
            if (fit.Shape == FitShape.Rectangular && c.Circular && fit.Barrels.Count < 2) sizeError = Math.Max(sizeError, Math.Abs(fit.Height - c.OuterWidth));
            if (endError > MediumTolerance)
            {
                // What Navisworks shows is what clashes, so the mesh wins over the properties.
                c.Notes.Add($"Property end points differ from the model by {endError * 1000:0} mm; the measured centreline is used.");
                OrientLike(c, fit);
            }
            if (sizeError > HighTolerance)
                c.Notes.Add($"Property size differs from the model by {sizeError * 1000:0} mm.");
            c.Deviation = Math.Max(endError, sizeError);
        }

        /// <summary>
        /// The arc of a curved pipe in plan: centre (cx, cy) and radius, from start to end along the shorter way round. Gives a
        /// point halfway along it (level halfway between the ends, as a pipe's grade is even) and whether it turns clockwise.
        /// False, with the reason, when the ends are not on that radius or the arc is a half circle (which way is unknown).
        /// </summary>
        public static bool Arc(V3 start, V3 end, double cx, double cy, double radius, out V3 mid, out bool clockwise, out string problem)
        {
            mid = default; clockwise = false; problem = null;
            double sx = start.X - cx, sy = start.Y - cy, ex = end.X - cx, ey = end.Y - cy;
            double rs = Math.Sqrt(sx * sx + sy * sy), re = Math.Sqrt(ex * ex + ey * ey);
            double tolerance = Math.Max(0.05, radius * 0.01);
            if (Math.Abs(rs - radius) > tolerance || Math.Abs(re - radius) > tolerance)
            {
                problem = $"its ends are {rs:0.000} m and {re:0.000} m from the radius point";
                return false;
            }
            double cross = sx * ey - sy * ex;
            double bx = sx / rs + ex / re, by = sy / rs + ey / re, b = Math.Sqrt(bx * bx + by * by);
            if (b < 1e-6 || Math.Abs(cross) < 1e-9 * radius * radius)
            {
                problem = "the arc is a half circle (it could turn either way)";
                return false;
            }
            clockwise = cross < 0;
            mid = new V3(cx + bx / b * radius, cy + by / b * radius, (start.Z + end.Z) / 2);
            return true;
        }

        private static double Spacing(LinearFit fit) => fit.Barrels.Count < 2 ? 0 : Math.Abs(fit.Barrels[1].u - fit.Barrels[0].u);

        // ---------------- Civil 3D structures ----------------

        private static void ReadCivilStructure(ImportCandidate c, Props p)
        {
            c.Origin = "Civil 3D";
            c.Detected = ImportKind.Structure;
            c.X = p.Num("Structure Easting");
            c.Y = p.Num("Structure Northing");
            c.Rotation = p.Num("Structure Rotation Angle");
            c.Rim = p.Num("Insertion Rim Elevation");
            c.Sump = p.Has("Sump Elevation") ? p.Num("Sump Elevation") : double.NaN;

            FootprintFit f = c.Footprint = c.Source.Triangles.Length >= 9 ? NavisGeometryFit.FitFootprint(c.Source.Triangles) : null;
            if (f != null)
            {
                c.Bottom = f.Bottom;
                if (double.IsNaN(c.Rim) || c.Rim == 0) c.Rim = f.Top;
                if (double.IsNaN(c.Sump)) c.Sump = f.Bottom;
                ApplyFootprint(c, f);
                // Headwalls and end sections are inserted at the pipe end, not at their middle, so their centre is not compared.
                if (!IsEndStructure(c))
                {
                    double off = Math.Sqrt((f.CenterX - c.X) * (f.CenterX - c.X) + (f.CenterY - c.Y) * (f.CenterY - c.Y));
                    if (off > HighTolerance) c.Notes.Add($"Insertion point is {off * 1000:0} mm from the middle of the model.");
                    c.Deviation = Math.Max(c.Deviation, off);
                }
                if (Math.Abs(f.Top - c.Rim) > MediumTolerance) c.Notes.Add($"Rim is {(c.Rim - f.Top) * 1000:0} mm from the top of the model.");
            }
            else c.Notes.Add("The mesh could not be measured; the structure size comes from the catalog match only.");

            if (IsEndStructure(c))
            {
                // Civil 3D headwall sizes cannot be matched reliably; an exact solid keeps the clash geometry right.
                c.Detected = ImportKind.Solid;
                c.Notes.Add("Headwall or end section: brought in as an exact solid (switch to Structure to use a catalog part).");
            }
        }

        private static bool IsEndStructure(ImportCandidate c)
        {
            string t = (c.PartSizeName + " " + c.Source.Props.FirstOrDefault(x => x.Name.EndsWith("Description"))?.Value).ToLowerInvariant();
            return t.Contains("headwall") || t.Contains("end section") || t.Contains("flared") || t.Contains("wing");
        }

        private static void ApplyFootprint(ImportCandidate c, FootprintFit f)
        {
            c.StructCircular = f.Shape == FitShape.Circular;
            c.StructDiameter = f.Diameter;
            c.StructWidth = f.Width;
            c.StructLength = f.Length;
        }

        // ---------------- Anything else: measured from the mesh ----------------

        private static void ReadFromGeometry(ImportCandidate c, Props p)
        {
            c.Origin = "Geometry";
            if (c.Source.Triangles.Length < 9) { c.Detected = ImportKind.Skip; c.Notes.Add("No geometry."); return; }
            LinearFit fit = c.Fit = Fit(c.Source);
            double section = fit == null ? 0 : (fit.Shape == FitShape.Circular ? fit.OuterDiameter * Math.Max(1, fit.Barrels.Count) : Math.Max(fit.Width, fit.Height));
            // A pipe is a straight prism clearly longer than it is wide; a near-vertical axis is a shaft (structure), never a pipe.
            bool vertical = fit != null && Math.Abs((fit.End - fit.Start).Unit.Z) > 0.9;
            if (fit != null && fit.Straight && !vertical && fit.Length > 1.5 * section && fit.MaxDeviation < MediumTolerance)
            {
                c.Start = fit.Start;
                c.End = fit.End;
                c.Circular = fit.Shape == FitShape.Circular;
                c.Detected = c.Circular ? ImportKind.Pipe : ImportKind.Box;
                c.OuterWidth = c.Circular ? fit.OuterDiameter : fit.Width;
                c.OuterHeight = c.Circular ? fit.OuterDiameter : fit.Height;
                if (fit.Barrels.Count > 1) { c.Barrels = fit.Barrels; c.BarrelAxis = fit.U; c.BarrelUp = fit.W; c.Notes.Add($"{fit.Barrels.Count} barrels in one object."); }

                // Inner size: an inner wall in the mesh, else a property, else unknown (matched by outer size only).
                double inner = c.Circular ? fit.InnerDiameter : fit.InnerWidth;
                double propOuter = p.FirstLen("Outside Diameter", "Outer Diameter", "OuterDiameter");
                double propInner = p.FirstLen("Inside Diameter", "Inner Diameter", "InnerDiameter", "Nominal Diameter", "NominalDiameter", "Diameter");
                if (inner > 0) { c.InnerWidth = inner; c.InnerHeight = c.Circular ? inner : fit.InnerHeight; }
                else if (propInner > 0 && propInner < c.OuterWidth) { c.InnerWidth = c.InnerHeight = propInner; }
                c.Wall = c.InnerWidth > 0 ? (c.OuterWidth - c.InnerWidth) / 2 : 0;
                if (propOuter > 0 && Math.Abs(propOuter - c.OuterWidth) > HighTolerance)
                    c.Notes.Add($"Property outer size {propOuter * 1000:0} mm differs from the model ({c.OuterWidth * 1000:0} mm); the model is used.");
                if (c.InnerWidth <= 0) c.Notes.Add("Inner size unknown: matched by outer size.");
                if (Math.Abs(fit.Roll) > 0.01) c.Notes.Add($"Section is rotated {fit.Roll * 180 / Math.PI:0.0}° about its axis; Civil 3D boxes are always upright.");
                c.Deviation = fit.MaxDeviation;
                c.Confidence = ImportConfidence.Medium;
                return;
            }

            FootprintFit f = c.Footprint = NavisGeometryFit.FitFootprint(c.Source.Triangles);
            if (f != null && f.MaxDeviation < MediumTolerance && f.Top - f.Bottom > 0.3)
            {
                c.Detected = ImportKind.Structure;
                c.X = f.CenterX; c.Y = f.CenterY; c.Rotation = f.Rotation;
                c.Rim = f.Top; c.Sump = f.Bottom; c.Bottom = f.Bottom;
                ApplyFootprint(c, f);
                c.Deviation = f.MaxDeviation;
                c.Confidence = ImportConfidence.Medium;
                c.Notes.Add("Measured from the model; rim = top, sump = bottom.");
                return;
            }

            c.Detected = ImportKind.Solid;
            if (fit != null && !fit.Straight) c.Notes.Add("Not a straight pipe (" + fit.Note + ").");
            c.Notes.Add("Brought in as an exact solid.");
        }

        // ---------------- helpers ----------------

        private static LinearFit Fit(NavisElement e) => e.Triangles != null && e.Triangles.Length >= 18 ? NavisGeometryFit.FitLinear(e.Triangles) : null;

        private static double EndError(V3 s, V3 e, LinearFit fit) =>
            Math.Min(Math.Max((fit.Start - s).Length, (fit.End - e).Length), Math.Max((fit.Start - e).Length, (fit.End - s).Length));

        /// <summary>Uses the measured ends, keeping the property flow direction.</summary>
        private static void OrientLike(ImportCandidate c, LinearFit fit)
        {
            bool same = (fit.Start - c.Start).Length + (fit.End - c.End).Length <= (fit.Start - c.End).Length + (fit.End - c.Start).Length;
            c.Start = same ? fit.Start : fit.End;
            c.End = same ? fit.End : fit.Start;
        }

        private static void Grade(ImportCandidate c)
        {
            if (c.Detected == ImportKind.Solid || c.Detected == ImportKind.Skip) { c.Confidence = ImportConfidence.High; return; }
            ImportConfidence byDeviation = c.Deviation <= HighTolerance ? ImportConfidence.High : c.Deviation <= MediumTolerance ? ImportConfidence.Medium : ImportConfidence.Low;
            if (byDeviation > c.Confidence) c.Confidence = byDeviation;
        }

        private static string GenericMaterial(Props p)
        {
            foreach (string name in new[] { "Pipe Material", "Material Name", "Structural Material", "Material" })
            {
                string v = p.Text(name);
                // The Item tab's "Material" is the display material (e.g. "AutoCAD Color Index 170"), not what the pipe is made of.
                if (v.Length > 0 && !v.StartsWith("AutoCAD Color", StringComparison.OrdinalIgnoreCase) && !v.Equals("ByLayer", StringComparison.OrdinalIgnoreCase)) return v;
            }
            return "";
        }

        /// <summary>Normalised material for matching: "concrete", "pvc", "hdpe", "ductile iron", "steel", "corrugated metal", "clay", "grp" or "".</summary>
        public static string MaterialKey(string material)
        {
            string m = (material ?? "").ToLowerInvariant();
            if (m.Length == 0) return "";
            if (m.Contains("corrugated") && (m.Contains("hdpe") || m.Contains("polyethylene") || m.Contains("pe "))) return "corrugated hdpe";
            if (m.Contains("corrugated") || m.Contains("cmp") || m.Contains("metal pipe")) return "corrugated metal";
            if (m.Contains("concrete") || Regex.IsMatch(m, @"\brc(p)?\b")) return "concrete";
            if (m.Contains("pvc")) return "pvc";
            if (m.Contains("hdpe") || m.Contains("polyethylene") || Regex.IsMatch(m, @"\bpe\b")) return "hdpe";
            if (m.Contains("ductile") || Regex.IsMatch(m, @"\bdi\b")) return "ductile iron";
            if (m.Contains("grp") || m.Contains("frp") || m.Contains("fibreglass") || m.Contains("fiberglass")) return "grp";
            if (m.Contains("steel")) return "steel";
            if (m.Contains("clay") || m.Contains("vitrified")) return "clay";
            if (m.Contains("cast iron")) return "cast iron";
            return m.Trim();
        }

        /// <summary>Property lookup by name, ignoring the "Geometry:" style prefixes Civil 3D adds; Civil 3D values win.</summary>
        private sealed class Props
        {
            private readonly Dictionary<string, NavisProp> _byName = new Dictionary<string, NavisProp>(StringComparer.OrdinalIgnoreCase);
            private readonly List<NavisProp> _all;

            public Props(NavisElement e)
            {
                _all = e.Props ?? new List<NavisProp>();
                foreach (NavisProp prop in _all.OrderBy(x => x.Category == "Civil3D" ? 0 : 1))
                {
                    string key = Strip(prop.Name);
                    if (!_byName.ContainsKey(key)) _byName[key] = prop;
                }
            }

            private static string Strip(string name)
            {
                int colon = name.IndexOf(':');
                return (colon >= 0 ? name.Substring(colon + 1) : name).Trim();
            }

            public bool Has(string name) => _byName.ContainsKey(name);

            public string Text(string name, string categoryHint = null)
            {
                if (categoryHint != null)
                {
                    NavisProp hinted = _all.FirstOrDefault(x => Strip(x.Name).Equals(name, StringComparison.OrdinalIgnoreCase) && x.Name.StartsWith(categoryHint, StringComparison.OrdinalIgnoreCase));
                    if (hinted != null) return hinted.Value ?? "";
                }
                return _byName.TryGetValue(name, out NavisProp p) ? (p.Value ?? "").Trim() : "";
            }

            /// <summary>A plain number (coordinates, elevations, angles).</summary>
            public double Num(string name)
            {
                if (!_byName.TryGetValue(name, out NavisProp p)) return double.NaN;
                if (p.Number.HasValue) return p.Number.Value;
                Match m = Regex.Match(p.Value ?? "", @"[-+]?\d[\d,]*(\.\d+)?([eE][-+]?\d+)?");
                return m.Success && double.TryParse(m.Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
            }

            /// <summary>A length in metres from text such as "500.000mm", "0.5 m" or "20 in".</summary>
            public double Len(string name)
            {
                if (!_byName.TryGetValue(name, out NavisProp p)) return 0;
                return ParseLength(p);
            }

            /// <summary>The first property whose name passes the test (the radius names differ between exports).</summary>
            private NavisProp Where(Func<string, bool> test) => _byName.Where(kv => test(kv.Key)).Select(kv => kv.Value).FirstOrDefault();

            public double NumWhere(Func<string, bool> test)
            {
                NavisProp p = Where(test);
                if (p == null) return double.NaN;
                if (p.Number.HasValue) return p.Number.Value;
                Match m = Regex.Match(p.Value ?? "", @"[-+]?\d[\d,]*(\.\d+)?([eE][-+]?\d+)?");
                return m.Success && double.TryParse(m.Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
            }

            /// <summary>A length in metres; a bare number counts as metres (a radius of 50 is 50 m, not 50 mm).</summary>
            public double LenWhere(Func<string, bool> test)
            {
                NavisProp p = Where(test);
                if (p == null) return 0;
                if (Regex.IsMatch(p.Value ?? "", @"\d\s*(mm|cm|m|in|""|ft|')\s*$", RegexOptions.IgnoreCase)) return ParseLength(p);
                return NumWhere(test) is double v && !double.IsNaN(v) ? v : 0;
            }

            public double FirstLen(params string[] names)
            {
                foreach (string n in names) { double v = Len(n); if (v > 0) return v; }
                return 0;
            }

            private static double ParseLength(NavisProp p)
            {
                string s = (p.Value ?? "").Trim();
                Match m = Regex.Match(s, @"([-+]?\d[\d,]*(?:\.\d+)?)\s*(mm|cm|m|in|""|ft|')?", RegexOptions.IgnoreCase);
                if (!m.Success) return p.Number ?? 0;
                if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return 0;
                switch (m.Groups[2].Value.ToLowerInvariant())
                {
                    case "mm": return v / 1000;
                    case "cm": return v / 100;
                    case "m": return v;
                    case "in": case "\"": return v * 0.0254;
                    case "ft": case "'": return v * 0.3048;
                    // A plain number already converted by Navisworks, or a bare value: values over 20 are taken as millimetres.
                    default: return p.Number.HasValue ? p.Number.Value : (v > 20 ? v / 1000 : v);
                }
            }
        }
    }
}
