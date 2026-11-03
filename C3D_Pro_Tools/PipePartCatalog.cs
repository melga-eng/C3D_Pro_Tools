using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using System;
using System.Collections.Generic;

namespace C3DProTools
{
    /// <summary>Looks up part families and sizes in a network's parts list.</summary>
    internal static class PipePartCatalog
    {
        /// <summary>The part size of an existing pipe, found by name in its family when Civil 3D does not return it directly.</summary>
        public static ObjectId SizeIdOf(Transaction tr, Pipe pipe)
        {
            ObjectId sizeId = ObjectId.Null;
            try { sizeId = pipe.GetPartSizeId(); } catch (Exception) { }
            return sizeId.IsNull ? ResolvePartSizeId(tr, pipe.PartFamilyId, pipe.PartSizeName) : sizeId;
        }

        /// <summary>Resolves a part size by name (or description) in the family, falling back to its first size.</summary>
        public static ObjectId ResolvePartSizeId(Transaction tr, ObjectId familyId, string partSizeName)
        {
            if (familyId.IsNull) return ObjectId.Null;
            PartFamily fam = tr.GetObject(familyId, OpenMode.ForRead) as PartFamily;
            if (fam == null || fam.PartSizeCount == 0) return ObjectId.Null;

            for (int i = 0; i < fam.PartSizeCount; i++)
            {
                ObjectId sizeId = fam[i];
                PartSize pSize = tr.GetObject(sizeId, OpenMode.ForRead) as PartSize;
                if (pSize != null && (pSize.Name.Equals(partSizeName, StringComparison.OrdinalIgnoreCase) ||
                                     pSize.Description.Equals(partSizeName, StringComparison.OrdinalIgnoreCase)))
                {
                    return sizeId;
                }
            }
            return fam[0];
        }

        public static List<KeyValuePair<string, ObjectId>> Sizes(Transaction tr, ObjectId familyId)
        {
            List<KeyValuePair<string, ObjectId>> sizes = new List<KeyValuePair<string, ObjectId>>();
            if (!(tr.GetObject(familyId, OpenMode.ForRead) is PartFamily fam)) return sizes;
            for (int i = 0; i < fam.PartSizeCount; i++)
            {
                if (tr.GetObject(fam[i], OpenMode.ForRead) is PartSize size) sizes.Add(new KeyValuePair<string, ObjectId>(size.Name, fam[i]));
            }
            return sizes;
        }
    }
}
