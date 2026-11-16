using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(C3DProTools.ProfileTool.StyleValuesRun))]

namespace C3DProTools.ProfileTool
{
    /// <summary>
    /// C3DPROSTYLEVAL: Label Style Values (STYLEMGR) with no window. For the label styles of the categories named (wildcards on
    /// the category path), lists the values whose "group › name" matches, or sets them all to one value (as typed in the window:
    /// lengths in mm plotted when the drawing is in metres, angles in degrees). One transaction, one UNDO.
    /// </summary>
    public sealed class StyleValuesRun
    {
        [CommandMethod("C3DPRO", "C3DPROSTYLEVAL", CommandFlags.Modal)]
        public void Run()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            string Ask(string q)
            {
                PromptResult r = ed.GetString(new PromptStringOptions(q) { AllowSpaces = true });
                return r.Status == PromptStatus.OK ? r.StringResult.Trim() : null;
            }
            string categories = Ask("\nStyle categories (wildcards, e.g. *Pipe*): ");
            if (categories == null) return;
            string values = Ask("\nValues (wildcards on \"group › name\", e.g. *Text Height*): ");
            if (values == null) return;
            string newValue = Ask("\nNew value <list only>: ");
            if (newValue == null) return;
            ed.WriteMessage("\n" + RunReport.Write(Execute(doc, categories, values, newValue), "C3DPROSTYLEVAL", newValue.Length == 0 ? "List" : "Set") + "\n");
        }

        internal static string Execute(Document doc, string categories, string values, string newValue)
        {
            var log = new StringBuilder();
            bool write = newValue.Length > 0;
            double lengthScale = doc.Database.Insunits switch { UnitsValue.Meters => 1000.0, UnitsValue.Centimeters => 10.0, _ => 1.0 };
            log.AppendLine($"C3DPROSTYLEVAL categories \"{categories}\", values \"{values}\"" + (write ? $", set to {newValue}" : ", list only"));
            int styles = 0, found = 0, set = 0, failed = 0;
            try
            {
                using (doc.LockDocument())
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    CivilDocument civil = CivilDocument.GetCivilDocument(doc.Database);
                    foreach (StyleCategory cat in StyleCatalog.Load(tr, civil))
                    {
                        if (!NamePatterns.Matches(categories, cat.Path)) continue;
                        foreach (StyleEntry e in cat.Styles)
                        {
                            styles++;
                            var style = (LabelStyle)tr.GetObject(e.Id, write ? OpenMode.ForWrite : OpenMode.ForRead);
                            foreach (PropSlot slot in StyleProps.Read(tr, style, write ? OpenMode.ForWrite : OpenMode.ForRead, false))
                            {
                                if (!NamePatterns.Matches(values, $"{slot.Group} › {slot.Name}")) continue;
                                found++;
                                string was = StyleProps.Format(slot, lengthScale);
                                if (!write) { log.AppendLine($"  {cat.Path} / {e.Name}: {slot.Group} › {slot.Name} = {was} {StyleProps.UnitOf(slot, lengthScale)}"); continue; }
                                if (!StyleProps.TryParse(slot, newValue, lengthScale, out object value, out string error))
                                {
                                    failed++;
                                    log.AppendLine($"  not set {e.Name} › {slot.Group} › {slot.Name}: {error}");
                                    continue;
                                }
                                try
                                {
                                    slot.ValueProp.SetValue(slot.Holder, value);
                                    set++;
                                    log.AppendLine($"  {cat.Path} / {e.Name}: {slot.Group} › {slot.Name} {was} -> {newValue}");
                                }
                                catch (System.Exception ex)
                                {
                                    failed++;
                                    log.AppendLine($"  not set {e.Name} › {slot.Group} › {slot.Name}: {(ex.InnerException ?? ex).Message}");
                                }
                            }
                        }
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                C3DProLogger.Error("C3DPROSTYLEVAL failed", ex);
                log.AppendLine($"C3DPROSTYLEVAL failed: {ex.Message}");
            }
            log.AppendLine(write ? $"{set} value(s) set on {styles} style(s)" + (failed > 0 ? $", {failed} not set" : "") + "."
                                 : $"{found} value(s) found on {styles} style(s).");
            return log.ToString();
        }
    }
}
