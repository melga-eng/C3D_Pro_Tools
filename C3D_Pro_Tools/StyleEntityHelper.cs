using Autodesk.AutoCAD.DatabaseServices;

namespace C3DProTools
{
    /// <summary>Reads and writes the one style an entity is drawn with.</summary>
    internal static class StyleEntityHelper
    {
        /// <summary>Returns the style the entity references, or ObjectId.Null if it is not a tracked entity type.</summary>
        public static ObjectId GetStyleId(DBObject obj)
        {
            // Civil 3D throws for entities that have no style (the API says so); they simply have none.
            if (obj is Autodesk.Civil.DatabaseServices.Entity civilEnt) { try { return civilEnt.StyleId; } catch (System.Exception) { return ObjectId.Null; } }
            if (obj is MText mText) return mText.TextStyleId;
            if (obj is DBText dbText) return dbText.TextStyleId;
            if (obj is Dimension dim) return dim.DimensionStyle;
            if (obj is MLeader ml) return ml.MLeaderStyle;
            if (obj is Table table) return table.TableStyle;
            if (obj is Mline mline) return mline.Style;
            return ObjectId.Null;
        }

        /// <summary>Assigns a style to an entity that must already be open for write. Returns false if the type is not tracked.</summary>
        public static bool TrySetStyleId(DBObject obj, ObjectId styleId)
        {
            if (obj is Autodesk.Civil.DatabaseServices.Entity civilEnt) { civilEnt.StyleId = styleId; return true; }
            if (obj is MText mText) { mText.TextStyleId = styleId; return true; }
            if (obj is DBText dbText) { dbText.TextStyleId = styleId; return true; }
            if (obj is Dimension dim) { dim.DimensionStyle = styleId; return true; }
            if (obj is MLeader ml) { ml.MLeaderStyle = styleId; return true; }
            if (obj is Table table) { table.TableStyle = styleId; return true; }
            if (obj is Mline mline) { mline.Style = styleId; return true; }
            return false;
        }
    }
}
