using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using C3DProTools.Features.Sheets.Application;
using Surface = Autodesk.Civil.DatabaseServices.Surface;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

// Pavement zones from surfaces: each pavement kind (taxiway, road...) is a surface that covers exactly its paved area. Where
// a point lies on that surface it is paved; the first kind listed wins where two overlap; everywhere else is unpaved. The
// zones set the minimum cover and the duct type in the networks' cover check, and mark the pavement edges in profile views.
namespace C3DProTools.Features.Pavement
{
    /// <summary>The "pavement" section of C3DPro.project.json.</summary>
    internal sealed class PavementSettings
    {
        /// <summary>Pavement kinds in priority order.</summary>
        public List<PavementZone> Zones { get; set; } = new List<PavementZone>();
        public PavementZone Unpaved { get; set; } = new PavementZone { Name = "Unpaved" };
        /// <summary>Station-elevation label styles for the pavement edges in profile views (a zone may name its own).</summary>
        public string StartLabelStyle { get; set; } = "";
        public string EndLabelStyle { get; set; } = "";
        /// <summary>Station-elevation style the tool copies for its own edge styles when none are set; empty = the drawing's first.</summary>
        public string BaseLabelStyle { get; set; } = "";
        /// <summary>Marker style of those labels; empty = the drawing's first.</summary>
        public string MarkerStyle { get; set; } = "";
        /// <summary>Step (m) along an alignment before each edge is found to a centimetre.</summary>
        public double Step { get; set; } = 1.0;
        /// <summary>The cover check leaves a zone's edge band (shoulder, blast pad) out of that zone.</summary>
        public bool CoverUsesEdgeBand { get; set; } = true;
        /// <summary>The start / end labels in profile views mark the pavement edge inside the edge band, not the surface edge.</summary>
        public bool LabelsUseEdgeBand { get; set; } = false;

        public bool Any => Zones.Any(z => !string.IsNullOrWhiteSpace(z.Surface));

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        public static PavementSettings Load(string drawingPath, out string problem)
        {
            problem = null;
            string text = Features.Shared.Contracts.SettingsSource.Text(drawingPath, out _);
            try
            {
                using JsonDocument d = JsonDocument.Parse(text);
                foreach (JsonProperty p in d.RootElement.EnumerateObject())
                    if (string.Equals(p.Name, "pavement", StringComparison.OrdinalIgnoreCase))
                    {
                        PavementSettings s = p.Value.Deserialize<PavementSettings>(Json) ?? new PavementSettings();
                        s.Zones ??= new List<PavementZone>();
                        s.Unpaved ??= new PavementZone { Name = "Unpaved" };
                        return s;
                    }
                return new PavementSettings();
            }
            catch (System.Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                problem = $"The C3D Pro settings could not be read: {ex.Message}";
                return new PavementSettings();
            }
        }

        /// <summary>The zone of a kind (0 = unpaved).</summary>
        public PavementZone Of(int kind) => kind <= 0 || kind > Zones.Count ? Unpaved : Zones[kind - 1];
    }

    internal static class PavementCivil3D
    {
        public const string RegApp = "C3DPRO_PAVEMENT";

        /// <summary>One "is this point paved by zone i" test per zone; a zone whose surface is not in the drawing gets null and is named in <paramref name="missing"/>.</summary>
        /// <param name="edgeBands">For the cover check: a zone's <see cref="PavementZone.EdgeBand"/> along the inside of its edge does not count as that zone.</param>
        public static List<Func<double, double, bool>> Tests(Transaction tr, CivilDocument civil, PavementSettings s, List<string> missing, bool edgeBands = false)
        {
            var byName = new Dictionary<string, Surface>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in civil.GetSurfaceIds())
                if (tr.GetObject(id, OpenMode.ForRead) is Surface sf) byName[sf.Name] = sf;
            var tests = new List<Func<double, double, bool>>();
            foreach (PavementZone z in s.Zones)
            {
                if (!byName.TryGetValue((z.Surface ?? "").Trim(), out Surface surface)) { missing.Add(z.Surface); tests.Add(null); continue; }
                bool On(double x, double y)
                {
                    // Outside the surface (or in one of its holes) there is no elevation: not paved.
                    try { double e = surface.FindElevationAtXY(x, y); return !double.IsNaN(e); }
                    catch (System.Exception) { return false; }
                }
                double band = edgeBands ? Math.Max(0, z.EdgeBand) : 0;
                tests.Add(band > 0 ? (x, y) => PavementZones.InsideBy(On, x, y, band) : On);
            }
            return tests;
        }

        /// <summary>The pavement runs along an alignment between two stations.</summary>
        public static List<PavementRun> AlongAlignment(Alignment al, double s0, double s1, IReadOnlyList<Func<double, double, bool>> tests, double step)
        {
            int KindAt(double st)
            {
                double x = 0, y = 0;
                try { al.PointLocation(Math.Max(al.StartingStation, Math.Min(al.EndingStation, st)), 0, ref x, ref y); }
                catch (System.Exception) { return 0; }
                return PavementZones.KindAt(tests, x, y);
            }
            return PavementZones.Runs(s0, s1, step, KindAt);
        }

        /// <summary>The edges inside a view's station range: each paved run's start (unless the view starts in it) and end.</summary>
        public static List<(double station, bool start, int kind)> Edges(List<PavementRun> runs, double s0, double s1)
        {
            var edges = new List<(double, bool, int)>();
            foreach (PavementRun r in runs.Where(r => r.Kind > 0))
            {
                if (r.S0 > s0 + 1e-3) edges.Add((r.S0, true, r.Kind));
                if (r.S1 < s1 - 1e-3) edges.Add((r.S1, false, r.Kind));
            }
            return edges;
        }

        /// <summary>The tool's earlier pavement labels in a view (tagged with <see cref="RegApp"/>).</summary>
        public static List<ObjectId> EarlierLabels(Transaction tr, Database db, ObjectId viewId)
        {
            var list = new List<ObjectId>();
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            Autodesk.AutoCAD.Runtime.RXClass cls = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(StationElevationLabel));
            foreach (ObjectId id in ms)
            {
                if (!id.ObjectClass.IsDerivedFrom(cls) || !(tr.GetObject(id, OpenMode.ForRead) is StationElevationLabel l)) continue;
                bool ours = false;
                try { using (ResultBuffer rb = l.GetXDataForApplication(RegApp)) ours = rb != null; } catch (System.Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                ObjectId view = ObjectId.Null;
                try { view = l.ViewId; } catch (System.Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                if (ours && (view == viewId || view.IsNull && l.FeatureId == viewId)) list.Add(id);
            }
            return list;
        }

        public static ObjectId StationElevationStyle(Transaction tr, CivilDocument civil, string name) =>
            ByName(tr, civil.Styles.LabelStyles.ProfileViewLabelStyles.StationElevationLabelStyles, name);

        public static List<string> StationElevationStyleNames(Transaction tr, CivilDocument civil) =>
            civil.Styles.LabelStyles.ProfileViewLabelStyles.StationElevationLabelStyles.Cast<ObjectId>()
                .Select(id => tr.GetObject(id, OpenMode.ForRead) is StyleBase st ? st.Name : null).Where(n => n != null).ToList();

        /// <summary>The tool's own edge style name when none is set: "C3DPRO Start Of Road Pavement".</summary>
        public static string OwnStyleName(PavementZone z, bool start) => $"C3DPRO {(start ? "Start" : "End")} Of {z.Name}";

        /// <summary>
        /// The edge style, made when missing by copying <paramref name="baseStyle"/> (a station-elevation style): its first text
        /// reads "Start Of Road Pavement" over "CH:" and the base's station value; its other texts are hidden.
        /// </summary>
        public static ObjectId EnsureEdgeStyle(Transaction tr, CivilDocument civil, ObjectId baseStyle, string name, string text)
        {
            ObjectId id = StationElevationStyle(tr, civil, name);
            if (!id.IsNull || baseStyle.IsNull) return id;
            LabelStyle source = (LabelStyle)tr.GetObject(baseStyle, OpenMode.ForWrite);
            id = source.CopyAsSibling(name);
            LabelStyle made = (LabelStyle)tr.GetObject(id, OpenMode.ForWrite);
            // The text that carries the station is kept (rewritten); the others are hidden. With no station anywhere, the first.
            var texts = made.GetComponents(LabelStyleComponentType.Text).Cast<ObjectId>()
                .Select(cid => tr.GetObject(cid, OpenMode.ForWrite) as LabelStyleTextComponent).Where(c => c != null).ToList();
            LabelStyleTextComponent keep = texts.FirstOrDefault(c => (c.Text.Contents.Value ?? "").Contains("Station Value")) ?? texts.FirstOrDefault();
            foreach (LabelStyleTextComponent c in texts)
            {
                if (c == keep)
                {
                    string old = c.Text.Contents.Value ?? "";
                    // The station value field of the base style, with its own precision and format.
                    System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(old, @"<\[Station Value[^\]]*\]>");
                    // As a plain chainage to the millimetre (76.183), the way the sheets show it, not 0+076.18.
                    string field = m.Success ? System.Text.RegularExpressions.Regex.Replace(m.Value.Replace("|FS|", "|FD|"), @"\|P\d\|", "|P3|") : null;
                    c.Text.Contents.Value = field != null ? $"{text}\\PCH:{field}" : text;
                }
                else c.General.Visible.Value = false;
            }
            return id;
        }

        public static ObjectId Marker(Transaction tr, CivilDocument civil, string name)
        {
            ObjectId id = ByName(tr, civil.Styles.MarkerStyles, name);
            if (!id.IsNull) return id;
            foreach (ObjectId m in civil.Styles.MarkerStyles) return m;
            return ObjectId.Null;
        }

        private static ObjectId ByName(Transaction tr, System.Collections.IEnumerable styles, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return ObjectId.Null;
            foreach (ObjectId id in styles)
                if (tr.GetObject(id, OpenMode.ForRead) is StyleBase st && string.Equals(st.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) return id;
            return ObjectId.Null;
        }

        /// <summary>Tags a label as the tool's, so the next run replaces it and leaves hand-made ones alone.</summary>
        public static void Tag(Transaction tr, Database db, DBObject obj)
        {
            RegAppTable table = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (!table.Has(RegApp))
            {
                table.UpgradeOpen();
                RegAppTableRecord rec = new RegAppTableRecord { Name = RegApp };
                table.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
            }
            using (ResultBuffer rb = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegApp), new TypedValue((int)DxfCode.ExtendedDataAsciiString, "pavement edge")))
                obj.XData = rb;
        }
    }
}
