using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.ComApi;
using C3DProTools.NavisLink;
using ComApi = Autodesk.Navisworks.Api.Interop.ComApi;

namespace C3DProTools.Navis
{
    /// <summary>Turns the current Navisworks selection into link elements: properties plus world-space triangles in metres.</summary>
    internal static class NavisExtractor
    {
        /// <summary>Property tabs that only describe display (colours, render material, triangle counts).</summary>
        private static readonly HashSet<string> SkippedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LcOaMaterial", "LcOpGeometryProperty", "3D Visualization"
        };

        public static NavisResponse ReadSelection(Document doc)
        {
            NavisResponse response = new NavisResponse { Ok = true, Document = doc.FileName ?? "", Units = doc.Units.ToString() };
            double toMetres = ToMetres(doc.Units);
            ModelItemCollection selected = doc.CurrentSelection.SelectedItems;
            response.SelectedCount = selected.Count;

            HashSet<Guid> seen = new HashSet<Guid>();
            foreach (ModelItem root in selected)
            {
                foreach (ModelItem item in Targets(root))
                {
                    if (item.InstanceGuid != Guid.Empty && !seen.Add(item.InstanceGuid)) continue;
                    try { response.Elements.Add(Read(item, toMetres)); }
                    catch (Exception ex) { response.Warnings.Add($"{item.DisplayName}: {ex.Message}"); }
                }
            }
            return response;
        }

        /// <summary>
        /// The objects to send for one selected node: the node itself when it has geometry, a composite object (a group whose
        /// children are all unnamed geometry, e.g. a Revit family or a multi-solid block) as one element, otherwise its children.
        /// Layers and files are always opened up.
        /// </summary>
        private static IEnumerable<ModelItem> Targets(ModelItem item)
        {
            if (item.HasGeometry) { yield return item; yield break; }
            string cls = item.ClassDisplayName ?? "";
            bool container = cls.Equals("File", StringComparison.OrdinalIgnoreCase) || cls.Equals("Layer", StringComparison.OrdinalIgnoreCase);
            List<ModelItem> children = item.Children.ToList();
            if (!container && children.Count > 0 && children.All(c => c.HasGeometry && !c.Children.Any() && string.IsNullOrEmpty(c.DisplayName)))
            {
                yield return item;
                yield break;
            }
            foreach (ModelItem child in children)
                foreach (ModelItem t in Targets(child)) yield return t;
        }

        private static NavisElement Read(ModelItem item, double toMetres)
        {
            NavisElement e = new NavisElement
            {
                Guid = item.InstanceGuid == Guid.Empty ? "" : item.InstanceGuid.ToString(),
                Name = string.IsNullOrEmpty(item.DisplayName) ? item.ClassDisplayName ?? "" : item.DisplayName,
                ClassName = item.ClassName ?? "",
                ClassDisplayName = item.ClassDisplayName ?? ""
            };

            foreach (PropertyCategory cat in item.PropertyCategories)
            {
                if (SkippedCategories.Contains(cat.Name) || SkippedCategories.Contains(cat.DisplayName)) continue;
                foreach (DataProperty prop in cat.Properties)
                {
                    NavisProp p = new NavisProp { Category = cat.DisplayName, Name = prop.DisplayName };
                    try
                    {
                        VariantData v = prop.Value;
                        switch (v.DataType)
                        {
                            case VariantDataType.DoubleLength: p.Number = v.ToDoubleLength() * toMetres; p.Value = p.Number.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture); break;
                            case VariantDataType.Double: p.Number = v.ToDouble(); p.Value = p.Number.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture); break;
                            case VariantDataType.DoubleAngle: p.Number = v.ToDoubleAngle(); p.Value = p.Number.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture); break;
                            case VariantDataType.Int32: p.Number = v.ToInt32(); p.Value = v.ToInt32().ToString(); break;
                            case VariantDataType.Boolean: p.Value = v.ToBoolean() ? "True" : "False"; break;
                            default: p.Value = v.IsDisplayString ? v.ToDisplayString() : v.ToString(); break;
                        }
                    }
                    catch (Exception) { continue; }
                    e.Props.Add(p);
                    if (cat.Name == "LcOaNode" && prop.Name == "LcOaNodeSourceFile") e.SourceFile = p.Value;
                    if (cat.Name == "LcOaNode" && prop.Name == "LcOaNodeLayer") e.Layer = p.Value;
                }
            }

            BoundingBox3D box = item.BoundingBox();
            if (box != null && !box.IsEmpty)
                e.Box = new[] { box.Min.X * toMetres, box.Min.Y * toMetres, box.Min.Z * toMetres, box.Max.X * toMetres, box.Max.Y * toMetres, box.Max.Z * toMetres };

            List<double> tris = new List<double>();
            if (item.HasGeometry) AddTriangles(item, tris, toMetres);
            else foreach (ModelItem d in item.Descendants) if (d.HasGeometry) AddTriangles(d, tris, toMetres);
            e.Triangles = tris.ToArray();
            return e;
        }

        private sealed class TriangleSink : ComApi.InwSimplePrimitivesCB
        {
            public double[] M;
            public double Scale = 1.0;
            public List<double> Out;

            private void Add(ComApi.InwSimpleVertex v)
            {
                Array a = (Array)v.coord;
                int lb = a.GetLowerBound(0);
                double x = Convert.ToDouble(a.GetValue(lb)), y = Convert.ToDouble(a.GetValue(lb + 1)), z = Convert.ToDouble(a.GetValue(lb + 2));
                // Local fragment coordinates are small floats; the double-precision matrix places them in the world exactly.
                double w = M[3] * x + M[7] * y + M[11] * z + M[15];
                Out.Add((M[0] * x + M[4] * y + M[8] * z + M[12]) / w * Scale);
                Out.Add((M[1] * x + M[5] * y + M[9] * z + M[13]) / w * Scale);
                Out.Add((M[2] * x + M[6] * y + M[10] * z + M[14]) / w * Scale);
            }

            public void Triangle(ComApi.InwSimpleVertex v1, ComApi.InwSimpleVertex v2, ComApi.InwSimpleVertex v3) { Add(v1); Add(v2); Add(v3); }
            public void Line(ComApi.InwSimpleVertex v1, ComApi.InwSimpleVertex v2) { }
            public void Point(ComApi.InwSimpleVertex v1) { }
            public void SnapPoint(ComApi.InwSimpleVertex v1) { }
        }

        private static void AddTriangles(ModelItem item, List<double> into, double toMetres)
        {
            ComApi.InwOaPath path = ComApiBridge.ToInwOaPath(item);
            Array own = (Array)path.ArrayData;
            TriangleSink sink = new TriangleSink { Out = into, Scale = toMetres };
            foreach (ComApi.InwOaFragment3 frag in path.Fragments())
            {
                // Shared (instanced) geometry lists fragments of every instance; keep only this item's own.
                if (!SamePath(own, (Array)frag.path.ArrayData)) continue;
                Array m = (Array)((ComApi.InwLTransform3f3)frag.GetLocalToWorldMatrix()).Matrix;
                int lb = m.GetLowerBound(0);
                sink.M = new double[16];
                for (int i = 0; i < 16; i++) sink.M[i] = Convert.ToDouble(m.GetValue(lb + i));
                frag.GenerateSimplePrimitives(ComApi.nwEVertexProperty.eNORMAL, sink);
            }
        }

        private static bool SamePath(Array a, Array b)
        {
            if (a.Length != b.Length) return false;
            int la = a.GetLowerBound(0), lb = b.GetLowerBound(0);
            for (int i = 0; i < a.Length; i++)
                if (!Equals(a.GetValue(la + i), b.GetValue(lb + i))) return false;
            return true;
        }

        /// <summary>Selects the items with these GUIDs and zooms the current view to them.</summary>
        public static NavisResponse Select(Document doc, List<string> guids)
        {
            NavisResponse response = new NavisResponse { Ok = true, Document = doc.FileName ?? "" };
            ModelItemCollection found = new ModelItemCollection();
            foreach (string g in guids.Distinct())
            {
                Search search = new Search();
                search.Selection.SelectAll();
                search.Locations = SearchLocations.DescendantsAndSelf;
                search.SearchConditions.Add(SearchCondition.HasPropertyByName("LcOaNode", "LcOaNodeGuid").EqualValue(VariantData.FromDisplayString(g)));
                ModelItemCollection hits = search.FindAll(doc, false);
                if (hits.Count == 0) response.Warnings.Add("Not found: " + g);
                found.AddRange(hits);
            }
            doc.CurrentSelection.CopyFrom(found);
            response.SelectedCount = found.Count;
            if (found.Count > 0)
            {
                try { ComApiBridge.State.ZoomInCurViewOnCurSel(); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            return response;
        }

        public static double ToMetres(Units units)
        {
            switch (units)
            {
                case Units.Meters: return 1.0;
                case Units.Centimeters: return 0.01;
                case Units.Millimeters: return 0.001;
                case Units.Kilometers: return 1000.0;
                case Units.Feet: return 0.3048;
                case Units.Inches: return 0.0254;
                case Units.Yards: return 0.9144;
                case Units.Miles: return 1609.344;
                case Units.Micrometers: return 1e-6;
                case Units.Mils: return 2.54e-5;
                case Units.Microinches: return 2.54e-8;
                default: return 1.0;
            }
        }
    }
}
