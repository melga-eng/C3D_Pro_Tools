using Autodesk.AutoCAD.DatabaseServices;

namespace C3DProTools
{
    /// <summary>
    /// Sheet groups in the drawing: each sheet's group name is kept on its layout (an Xrecord in the layout's extension
    /// dictionary), so it travels with the drawing, follows a renamed sheet and goes with a deleted one. Nothing outside the
    /// DWG. Changes are made inside the caller's transaction.
    /// </summary>
    internal static class SheetGroups
    {
        private const string Key = "C3DPRO_SHEETGROUP";

        /// <summary>The sheet's group, empty when it has none.</summary>
        public static string Get(Transaction tr, Layout lay)
        {
            if (lay.ExtensionDictionary.IsNull) return "";
            var dict = (DBDictionary)tr.GetObject(lay.ExtensionDictionary, OpenMode.ForRead);
            if (!dict.Contains(Key) || !(tr.GetObject(dict.GetAt(Key), OpenMode.ForRead) is Xrecord x) || x.Data == null) return "";
            foreach (TypedValue v in x.Data) if (v.TypeCode == (short)DxfCode.Text) return SheetGroupNames.Clean(v.Value as string);
            return "";
        }

        /// <summary>Puts the sheet in <paramref name="group"/>; an empty name takes it out of its group.</summary>
        public static void Set(Transaction tr, ObjectId layoutId, string group)
        {
            group = SheetGroupNames.Clean(group);
            var lay = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
            if (lay.ExtensionDictionary.IsNull)
            {
                if (group.Length == 0) return;
                lay.UpgradeOpen();
                lay.CreateExtensionDictionary();
            }
            var dict = (DBDictionary)tr.GetObject(lay.ExtensionDictionary, OpenMode.ForWrite);
            if (group.Length == 0)
            {
                if (dict.Contains(Key))
                {
                    ObjectId id = dict.GetAt(Key);
                    dict.Remove(id);
                    tr.GetObject(id, OpenMode.ForWrite).Erase();
                }
                return;
            }
            if (dict.Contains(Key) && tr.GetObject(dict.GetAt(Key), OpenMode.ForWrite) is Xrecord old)
            {
                old.Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, group));
                return;
            }
            var x = new Xrecord { Data = new ResultBuffer(new TypedValue((int)DxfCode.Text, group)) };
            dict.SetAt(Key, x);
            tr.AddNewlyCreatedDBObject(x, true);
        }
    }
}
