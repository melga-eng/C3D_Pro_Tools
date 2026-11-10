using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using C3DProTools.Features.Networks.Civil3D;
using C3DProTools.Features.Sheets.Contracts;
using CivilLabel = Autodesk.Civil.DatabaseServices.Label;

// Writes one build item (one plan sheet) in one transaction: its PlanLabels changes, then its Arrange step, then the build
// records. The item commits whole or rolls back whole (docs/sheet-production-contract.md, section 10, "Atomicity"). Arranging
// is the existing Annotation Studio arranger, told to move only this sheet's unpinned tool labels; every other label is an
// obstacle. The caller holds the document lock.
namespace C3DProTools.Features.Sheets.Civil3D
{
    internal static class BuildSheetsCivil3D
    {
        public static BuildItemOutcome Apply(Document doc, BuildItem item, LabelRuleSet rules, string stamp)
        {
            Database db = doc.Database;
            CivilDocument civil = CivilDocument.GetCivilDocument(db);
            List<DrawingObjectRef> created = new List<DrawingObjectRef>();
            List<OperationIssue> issues = new List<OperationIssue>();
            string layout = item.Layouts.Count > 0 ? item.Layouts[0] : "";
            int added = 0, removed = 0, moved = 0;
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    (added, removed) = PlanLabelsCivil3D.ApplyChanges(tr, db, civil, item.Changes, created);
                    if (item.Changes.Any(c => c.Step == BuildStep.Arrange))
                        moved = Arrange(tr, db, civil, layout, rules, issues);
                    foreach (BuildStep step in item.Changes.Select(c => c.Step).Distinct())
                        BuildXData.WriteRecord(tr, db, layout, step, stamp);
                    tr.Commit();
                }
                return new BuildItemOutcome(item.ItemKey, ItemStatus.Committed, added, moved, removed, created, issues);
            }
            catch (Exception ex)
            {
                C3DProLogger.Error($"Build: sheet {layout} was rolled back", ex);
                return new BuildItemOutcome(item.ItemKey, ItemStatus.RolledBack, 0, 0, 0, null,
                    new[] { OperationIssue.Error(SheetCodes.ApplyPostconditionFailed, $"This sheet could not be built, so nothing on it was changed: {ex.Message}") with { Layout = layout } });
            }
        }

        /// <summary>
        /// Arranges the sheet's unpinned tool labels (the ones just added included) with the Annotation Studio arranger, then
        /// records each one's new placement in its tag, so the next build does not mistake the move for the user's. Returns how
        /// many labels it moved.
        /// </summary>
        private static int Arrange(Transaction tr, Database db, CivilDocument civil, string layout, LabelRuleSet rules, List<OperationIssue> issues)
        {
            SheetInfo sheet = AnnotSheets.Read(tr, db).FirstOrDefault(s => s.Layout.Equals(layout, StringComparison.OrdinalIgnoreCase));
            if (sheet == null) throw new InvalidOperationException($"Sheet {layout} is not in the drawing any more.");
            List<LabelInfo> labels = AnnotLabels.Scan(tr, db);

            // Only this sheet's unpinned tool labels may move.
            Dictionary<ObjectId, BuildTag> movable = new Dictionary<ObjectId, BuildTag>();
            foreach (LabelInfo l in labels)
            {
                if (!(tr.GetObject(l.Id, OpenMode.ForRead) is CivilLabel label)) continue;
                BuildTag tag = BuildXData.Decode(label.GetXDataForApplication(BuildTag.XDataApplication));
                if (tag == null || !string.Equals(tag.Layout, layout, StringComparison.OrdinalIgnoreCase)) continue;
                if (tag.IsPinned(PlanLabelsCivil3D.PlacementOf(label))) continue;
                movable[l.Id] = tag;
            }
            if (movable.Count == 0) return 0;
            Dictionary<ObjectId, (double X, double Y)> before = movable.Keys.ToDictionary(id => id, id =>
            {
                var at = ((CivilLabel)tr.GetObject(id, OpenMode.ForRead)).LabelLocation;
                return (at.X, at.Y);
            });

            AnnotationSettings s = AnnotationSettings.Load();
            ArrangeOptions o = new ArrangeOptions
            {
                MaxDistance = s.MaxDistance, Gap = s.Gap, Margin = s.Margin, RearrangeAll = true, SlideOnPipe = s.SlideNormalLabels,
                AvoidModelText = s.AvoidModelText, Only = new HashSet<ObjectId>(movable.Keys), Plan = true, Profile = false,
                KeepClearNetworks = new HashSet<string>(s.KeepClearNetworks ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
                SlideStyles = new HashSet<string>(s.SlideStyles ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
                DraggedStyles = new HashSet<string>(s.DraggedStyles ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
            };
            // A rule with a leader drags its labels out to clear space, like the boxed labels on the shop drawings.
            foreach (LabelRule r in rules.Rules.Where(r => r != null && r.View == ViewKind.Plan && r.Leader))
            {
                LabelKind kind = r.Select.Kind == TargetKind.Structure ? LabelKind.PlanStructure : LabelKind.PlanPipe;
                foreach (string style in r.LabelStyles) o.DraggedStyles.Add(FormAnnotationStudio.StyleKey(kind, style.Trim()));
            }

            AnnotScene scene = AnnotScene.Read(tr, db, civil, o.AvoidModelText, o.Profile);
            ArrangeReport report = AnnotArranger.Arrange(tr, sheet, labels, scene, o, new HashSet<ObjectId>(), false, AnnotLabels.PaperPerUnit(db));
            if (report.Unresolved > 0)
                issues.Add(OperationIssue.Warning(SheetCodes.ArrangeUnresolved,
                    $"{report.Unresolved} label(s) had no clear place and stay where they were; move them by hand (they are then pinned).") with { Layout = layout });

            // Re-record every movable label's placement: arranged labels moved, and the tag must say so.
            int moved = 0;
            foreach (KeyValuePair<ObjectId, BuildTag> kv in movable)
            {
                CivilLabel label = (CivilLabel)tr.GetObject(kv.Key, OpenMode.ForWrite);
                RecordedPlacement now = PlanLabelsCivil3D.PlacementOf(label);
                var at = label.LabelLocation;
                if (Math.Abs(at.X - before[kv.Key].X) > RecordedPlacement.PositionToleranceM || Math.Abs(at.Y - before[kv.Key].Y) > RecordedPlacement.PositionToleranceM) moved++;
                label.XData = BuildXData.Encode(kv.Value with { Placement = now });
            }
            return moved;
        }
    }
}
