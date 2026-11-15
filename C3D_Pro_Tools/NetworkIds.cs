using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace C3DProTools
{
    internal static class NetworkIds
    {
        /// <summary>
        /// A network's pipe or structure ids without the erased ones: a network can keep listing a part that was deleted, and
        /// opening it throws eWasErased, which stopped whole runs.
        /// </summary>
        public static IEnumerable<ObjectId> Live(this ObjectIdCollection ids)
        {
            if (ids == null) yield break;
            foreach (ObjectId id in ids)
                if (!id.IsNull && !id.IsErased) yield return id;
        }
    }
}
