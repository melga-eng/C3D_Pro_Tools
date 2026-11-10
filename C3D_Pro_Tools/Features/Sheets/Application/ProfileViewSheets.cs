using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using C3DProTools.Features.Sheets.Contracts;

// Which profile sheet shows which profile view, from the scene alone: the view's box against the profile sheets' viewport
// outlines. The ProfileLabels step needs it (ProfileLabelPlanner takes the mapping as an input).
namespace C3DProTools.Features.Sheets.Application
{
    internal static class ProfileViewSheets
    {
        /// <summary>
        /// Each profile view shown on a profile sheet, with that sheet: the first profile sheet (tab order, then viewport handle)
        /// whose viewport contains the centre of the view's box. A view on no profile sheet is left out; one shown on several
        /// sheets (an overview sheet, say) is mapped to the first only, so it is labelled once.
        /// </summary>
        public static List<ProfileViewLayout> Map(SheetScene scene)
        {
            List<SheetSnapshot> sheets = scene.Sheets.Where(s => s.Kind == SheetKind.Profile).OrderBy(s => s.TabOrder).ToList();
            List<ProfileViewLayout> map = new List<ProfileViewLayout>();
            foreach (ProfileViewSnapshot v in scene.ProfileViews)
            {
                if (!v.ModelBox.HasValue || !v.ModelBox.Value.IsValid) continue;
                Rect2 b = v.ModelBox.Value;
                Point2 centre = new Point2((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2);
                string layout = sheets.FirstOrDefault(s => s.Viewports.OrderBy(vp => Handle(vp.Ref)).Any(vp => Geometry2.Contains(vp.ModelBoundary, centre)))?.Layout;
                if (layout != null) map.Add(new ProfileViewLayout(v.Ref, layout));
            }
            return map;
        }

        private static ulong Handle(DrawingObjectRef r) =>
            ulong.TryParse(r.Handle, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong v) ? v : ulong.MaxValue;
    }
}
