using System;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using Exception = System.Exception;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using Entity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace C3DProTools.Features.Standards
{
    /// <summary>C3DPROLAYERS Data: every network part and label of the drawing with its network, family, size, description, style and layer, and the drawing's object layer settings (tab separated, read only).</summary>
    internal static class LayerDataReport
    {        private static string S(Func<string> get) { try { return (get() ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '); } catch (Exception) { return "?"; } }

        /// <summary>Tab-separated: kind, handle, class, network, family, size, description, name, style, layer, labelled part's kind / layer / network.</summary>
        internal static string Report(Document doc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("kind\thandle\tclass\tnetwork\tfamily\tsize\tdescription\tname\tstyle\tlayer\tfeature_class\tfeature_layer\tfeature_network\tfeature_name");
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForRead);
                string StyleName(Func<ObjectId> get) => S(() => { ObjectId id = get(); return id.IsNull ? "" : (tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Styles.StyleBase)?.Name; });
                foreach (ObjectId id in ms)
                {
                    DBObject o;
                    try { o = tr.GetObject(id, OpenMode.ForRead); } catch (Exception) { continue; }
                    if (o is Part p)
                        sb.AppendLine(string.Join("\t", "part", id.Handle, o.GetRXClass().Name, S(() => p.NetworkName), S(() => p.PartFamilyName), S(() => p.PartSizeName),
                            S(() => p.Description), S(() => p.Name), StyleName(() => p.StyleId), p.Layer, "", "", "", ""));
                    else if (o is PressurePart pp)
                        sb.AppendLine(string.Join("\t", "part", id.Handle, o.GetRXClass().Name, S(() => pp.NetworkName), S(() => pp.PartFamilyName), "",
                            S(() => pp.PartDescription), S(() => pp.Name), StyleName(() => pp.StyleId), pp.Layer, "", "", "", ""));
                    else if (o is LabelBase lb)
                    {
                        string fc = "", fl = "", fn = "", fname = "";
                        try
                        {
                            ObjectId f = lb.FeatureId;
                            if (!f.IsNull && tr.GetObject(f, OpenMode.ForRead) is Entity fe)
                            {
                                fc = fe.GetRXClass().Name; fl = fe.Layer;
                                if (fe is Part fp) { fn = S(() => fp.NetworkName); fname = S(() => fp.Name); }
                                else if (fe is PressurePart fpp) { fn = S(() => fpp.NetworkName); fname = S(() => fpp.Name); }
                                else if (fe is Autodesk.Civil.DatabaseServices.Entity ce) fname = S(() => ce.Name);
                            }
                        }
                        catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                        sb.AppendLine(string.Join("\t", "label", id.Handle, o.GetRXClass().Name, "", "", "", "", "", StyleName(() => lb.StyleId), lb.Layer, fc, fl, fn, fname));
                    }
                }
                tr.Commit();
            }
            // The drawing's object layers (Drawing Settings > Object Layers): the base layer and its modifier per object type.
            try
            {
                var civil = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(doc.Database);
                var layers = civil.Settings.DrawingSettings.ObjectLayerSettings;
                foreach (Autodesk.Civil.Settings.SettingsObjectLayerType t in Enum.GetValues(typeof(Autodesk.Civil.Settings.SettingsObjectLayerType)))
                {
                    try
                    {
                        var s = layers.GetObjectLayerSetting(t);
                        sb.AppendLine(string.Join("\t", "setting", t, "", "", "", "", "", S(() => s.Modifier.ToString()), S(() => s.ModifierValue), S(() => s.LayerName), "", "", "", ""));
                    }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
            }
            catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return sb.ToString();
        }

    }
}

