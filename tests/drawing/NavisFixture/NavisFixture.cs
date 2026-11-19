using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using C3DProTools.NavisLink;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(C3DProTools.Tests.Drawing.NavisFixture))]

namespace C3DProTools.Tests.Drawing
{
    /// <summary>
    /// TEST ONLY. Writes a Navisworks selection made from the drawing's largest network, as a model from elsewhere would
    /// bring it (geometry only, no Civil 3D properties):
    ///   COPY-1..3  the network's first three straight pipes, exactly where they are  -> must be "already in the drawing";
    ///   NEW-1..3   a 15 m duct starting inside each of three structures               -> must join that network, connected;
    ///   FAR-1      a duct 10 km away                                                  -> must become a new network.
    /// </summary>
    public sealed class NavisFixture
    {
        [CommandMethod("C3DPROTESTFIXTURE", CommandFlags.Modal)]
        public void Write()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            PromptResult pr = ed.GetString(new PromptStringOptions("\nFixture file: ") { AllowSpaces = true });
            if (pr.Status != PromptStatus.OK) return;
            string file = pr.StringResult.Trim().Trim('"');
            var response = new NavisResponse { Ok = true, Document = "fixture.nwd", Units = "m" };
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                CivilDocument civil = CivilDocument.GetCivilDocument(doc.Database);
                Network net = civil.GetPipeNetworkIds().Cast<ObjectId>().Select(id => (Network)tr.GetObject(id, OpenMode.ForRead))
                    .OrderByDescending(n => n.GetStructureIds().Count).First();
                var pipes = net.GetPipeIds().Cast<ObjectId>().Select(id => (Pipe)tr.GetObject(id, OpenMode.ForRead))
                    .Where(p => p.SubEntityType != PipeSubEntityType.Curved && p.StartPoint.DistanceTo(p.EndPoint) > 2).Take(3).ToList();
                int i = 0;
                foreach (Pipe p in pipes)
                    response.Elements.Add(Element($"COPY-{++i}", p.StartPoint, p.EndPoint, Math.Max(0.05, p.OuterDiameterOrWidth / 2)));

                var structures = net.GetStructureIds().Cast<ObjectId>().Select(id => (Structure)tr.GetObject(id, OpenMode.ForRead)).Take(3).ToList();
                i = 0;
                foreach (Structure s in structures)
                {
                    double z = Math.Min(s.RimElevation, s.SumpElevation + 0.6);
                    Point3d a = new Point3d(s.Location.X, s.Location.Y, z), b = new Point3d(s.Location.X + 15 * Math.Cos(0.3 + i), s.Location.Y + 15 * Math.Sin(0.3 + i), z - 0.1);
                    response.Elements.Add(Element($"NEW-{++i}", a, b, 0.08));
                }
                Point3d far = new Point3d(structures[0].Location.X + 10000, structures[0].Location.Y, 0);
                response.Elements.Add(Element("FAR-1", far, far + new Vector3d(20, 0, -0.2), 0.08));
                tr.Commit();
                ed.WriteMessage($"\nFixture from network \"{net.Name}\": {pipes.Count} copies, {structures.Count} new ducts, 1 far duct -> {file}");
            }
            using (FileStream f = File.Create(file))
                new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(NavisResponse)).WriteObject(f, response);
        }

        private static NavisElement Element(string name, Point3d a, Point3d b, double radius)
        {
            double[] t = Cylinder(a, b, radius);
            var e = new NavisElement { Guid = Guid.NewGuid().ToString(), Name = name, ClassName = "Solid", ClassDisplayName = "Solid", Layer = "FIXTURE", Triangles = t };
            e.Box = new[] { Min(t, 0), Min(t, 1), Min(t, 2), Max(t, 0), Max(t, 1), Max(t, 2) };
            return e;
        }

        private static double Min(double[] t, int k) { double m = double.MaxValue; for (int i = k; i < t.Length; i += 3) m = Math.Min(m, t[i]); return m; }
        private static double Max(double[] t, int k) { double m = double.MinValue; for (int i = k; i < t.Length; i += 3) m = Math.Max(m, t[i]); return m; }

        /// <summary>A closed 48-sided prism from a to b, caps fanned from their first vertex (as Navisworks gives meshes).</summary>
        private static double[] Cylinder(Point3d a, Point3d b, double r, int sides = 48)
        {
            Vector3d d = (b - a).GetNormal();
            Vector3d u = Math.Abs(d.Z) > 0.99 ? Vector3d.XAxis : Vector3d.ZAxis.CrossProduct(d).GetNormal();
            Vector3d w = d.CrossProduct(u).GetNormal();
            var ring = new List<Vector3d>();
            for (int i = 0; i < sides; i++) { double t = 2 * Math.PI * i / sides; ring.Add(u * (r * Math.Cos(t)) + w * (r * Math.Sin(t))); }
            var o = new List<double>();
            void Tri(Point3d p, Point3d q, Point3d s) => o.AddRange(new[] { p.X, p.Y, p.Z, q.X, q.Y, q.Z, s.X, s.Y, s.Z });
            for (int i = 0; i < sides; i++)
            {
                int j = (i + 1) % sides;
                Tri(a + ring[i], a + ring[j], b + ring[j]);
                Tri(a + ring[i], b + ring[j], b + ring[i]);
            }
            for (int i = 1; i + 1 < sides; i++) { Tri(a + ring[0], a + ring[i + 1], a + ring[i]); Tri(b + ring[0], b + ring[i], b + ring[i + 1]); }
            return o.ToArray();
        }
    }
}
