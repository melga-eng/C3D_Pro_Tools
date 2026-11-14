using System;
using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Features.Sheets.Application
{
    /// <summary>
    /// Labels already placed by hand count as done: a rule never adds a second label of the same style to a part that has one
    /// where the rule would put it. For a profile rule that is the same profile view (a part can show in two views on one
    /// sheet); for a plan rule the same sheet.
    /// </summary>
    internal static class HandLabels
    {
        /// <param name="viewOf">Label handle → handle of the profile view it is in, for every profile label of the drawing.</param>
        public static bool Covers(SheetScene scene, PlannedChange c, IReadOnlyDictionary<string, string> viewOf)
        {
            if (c.Kind != ChangeKind.Add || !c.Part.HasValue) return false;
            DrawingObjectRef view = ProfileLabelPlanner.ViewOf(c.SourceKey);
            return scene.Labels.Any(l => !l.IsToolLabel && l.Target == c.Part.Value
                && string.Equals(l.Current?.StyleName, c.StyleSlot, StringComparison.Ordinal)
                && (view.IsValid
                    ? viewOf != null && viewOf.TryGetValue(l.Ref.Handle ?? "", out string v) && string.Equals(v, view.Handle, StringComparison.OrdinalIgnoreCase)
                    : !string.IsNullOrEmpty(l.Layout) && string.Equals(l.Layout, c.Layout, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>The changes without the adds a hand-placed label already covers.</summary>
        public static List<PlannedChange> Without(SheetScene scene, IEnumerable<PlannedChange> changes, IReadOnlyDictionary<string, string> viewOf, out int skipped)
        {
            List<PlannedChange> all = changes.ToList();
            List<PlannedChange> kept = all.Where(c => !Covers(scene, c, viewOf)).ToList();
            skipped = all.Count - kept.Count;
            return kept;
        }
    }
}
