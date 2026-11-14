using System;
using System.Collections.Generic;
using System.Linq;
using C3DProTools.Features.Sheets.Contracts;

namespace C3DProTools.Features.Sheets.Application
{
    /// <summary>
    /// Labels already placed by hand count as done: a rule never adds a second label of the same style to a part that has one
    /// on the same sheet (or on no sheet: a profile label dragged outside its viewport still belongs to that view).
    /// </summary>
    internal static class HandLabels
    {
        public static bool Covers(SheetScene scene, PlannedChange c) =>
            c.Kind == ChangeKind.Add && c.Part.HasValue && scene.Labels.Any(l => !l.IsToolLabel && l.Target == c.Part.Value
                && string.Equals(l.Current?.StyleName, c.StyleSlot, StringComparison.Ordinal)
                && (string.IsNullOrEmpty(l.Layout) || string.Equals(l.Layout, c.Layout, StringComparison.OrdinalIgnoreCase)));

        /// <summary>The changes without the adds a hand-placed label already covers.</summary>
        public static List<PlannedChange> Without(SheetScene scene, IEnumerable<PlannedChange> changes, out int skipped)
        {
            List<PlannedChange> all = changes.ToList();
            List<PlannedChange> kept = all.Where(c => !Covers(scene, c)).ToList();
            skipped = all.Count - kept.Count;
            return kept;
        }
    }
}
