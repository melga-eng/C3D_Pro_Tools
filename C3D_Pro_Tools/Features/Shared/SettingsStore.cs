using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using C3DProTools.Features.Shared.Contracts;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C3DProTools.Features.Shared
{
    /// <summary>The project settings saved inside the drawing (an Xrecord in its named objects dictionary), so they travel with it.</summary>
    internal static class SettingsStore
    {
        private const string Key = "C3DPRO_PROJECT_SETTINGS";
        private const int Chunk = 1000;

        /// <summary>Lets every settings reader find the settings of an open drawing by its path.</summary>
        public static void Install() => SettingsSource.FromDrawing = path =>
        {
            Document doc = AcApp.DocumentManager.Cast<Document>().FirstOrDefault(d => string.Equals(d.Name, path, StringComparison.OrdinalIgnoreCase));
            return doc == null ? null : Read(doc.Database);
        };

        public static string Read(Database db)
        {
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(Key) || !(tr.GetObject(nod.GetAt(Key), OpenMode.ForRead) is Xrecord x) || x.Data == null) return null;
                var sb = new StringBuilder();
                foreach (TypedValue v in x.Data) if (v.TypeCode == (short)DxfCode.Text) sb.Append(v.Value as string);
                tr.Commit();
                return sb.Length == 0 ? null : sb.ToString();
            }
        }

        /// <summary>Saves the settings in the drawing (one UNDO step) and remembers them as the start for drawings that have none.</summary>
        public static void Save(Document doc, string json)
        {
            Database db = doc.Database;
            using (doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                var values = new List<TypedValue>();
                for (int i = 0; i < json.Length; i += Chunk) values.Add(new TypedValue((short)DxfCode.Text, json.Substring(i, Math.Min(Chunk, json.Length - i))));
                if (nod.Contains(Key)) ((Xrecord)tr.GetObject(nod.GetAt(Key), OpenMode.ForWrite)).Data = new ResultBuffer(values.ToArray());
                else
                {
                    nod.UpgradeOpen();
                    var x = new Xrecord { Data = new ResultBuffer(values.ToArray()) };
                    nod.SetAt(Key, x);
                    tr.AddNewlyCreatedDBObject(x, true);
                }
                tr.Commit();
            }
            SettingsSource.RememberLastUsed(json);
        }
    }
}
