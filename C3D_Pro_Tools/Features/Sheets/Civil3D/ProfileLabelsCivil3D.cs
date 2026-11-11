using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using C3DProTools.Features.Networks.Civil3D;
using C3DProTools.Features.Sheets.Application;
using C3DProTools.Features.Sheets.Contracts;
using CivilLabel = Autodesk.Civil.DatabaseServices.Label;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

// Writes the ProfileLabels changes of a build item in the caller's transaction (docs/sheet-production-contract.md, section 4):
// profile labels of pipes, joints and structures in their view, and crossing pipe labels, each with its build tag. Each label
// goes on the part's profile representation (one per part, shared by the views drawing it) in the view its source key names.
namespace C3DProTools.Features.Sheets.Civil3D
{
    internal static class ProfileLabelsCivil3D
    {
        internal static (int added, int removed) ApplyChanges(Transaction tr, Database db, CivilDocument civil, IEnumerable<PlannedChange> changes,
            LabelRuleSet rules, List<DrawingObjectRef> created)
        {
            List<PlannedChange> mine = changes.Where(c => c.Step == BuildStep.ProfileLabels && c.Target == ChangeTarget.Label).ToList();
            if (mine.Count == 0) return (0, 0);
            PlanLabelsCivil3D.EnsureRegApp(tr, db);
            Dictionary<ObjectId, ObjectId> viewParts = null;
            int added = 0, removed = 0;
            foreach (PlannedChange c in mine)
            {
                if (c.Kind == ChangeKind.Remove)
                {
                    if (!c.Existing.HasValue || !DrawingRefs3D.TryResolve(db, c.Existing.Value, out ObjectId id)) continue;   // gone already
                    tr.GetObject(id, OpenMode.ForWrite).Erase();
                    removed++;
                    continue;
                }
                if (c.Kind != ChangeKind.Add) continue;
                viewParts ??= AnnotLabels.ViewParts(tr, db);
                created.Add(Add(tr, db, civil, c, rules, viewParts));
                added++;
            }
            return (added, removed);
        }

        private static DrawingObjectRef Add(Transaction tr, Database db, CivilDocument civil, PlannedChange c, LabelRuleSet rules, Dictionary<ObjectId, ObjectId> viewParts)
        {
            LabelRule rule = rules.Rules.FirstOrDefault(r => r != null && string.Equals(r.Id?.Trim(), c.RuleId?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Change {c.ChangeId} names rule \"{c.RuleId}\", which is not in the rule set.");
            DrawingObjectRef viewRef = ProfileLabelPlanner.ViewOf(c.SourceKey);
            if (!viewRef.IsValid || !DrawingRefs3D.TryResolve(db, viewRef, out ObjectId viewId))
                throw new InvalidOperationException($"The profile view of change {c.ChangeId} is no longer in the drawing.");
            if (!c.Part.HasValue || !DrawingRefs3D.TryResolve(db, c.Part.Value, out ObjectId partId))
                throw new InvalidOperationException($"The part of change {c.ChangeId} is no longer in the drawing.");
            if (!viewParts.TryGetValue(partId, out ObjectId viewPartId))
                throw new InvalidOperationException($"Change {c.ChangeId}: Civil 3D has no profile representation of the part, so it cannot be labelled in the view.");

            LabelStyleKind kind = ProfileLabelPlanner.StyleKindOf(rule);
            ObjectId labelId;
            DBObject part = tr.GetObject(partId, OpenMode.ForRead);
            if (kind == LabelStyleKind.Structure && part is Structure)
                labelId = StructureProfileLabel.Create(viewId, viewPartId, StyleId(civil.Styles.LabelStyles.StructureLabelStyles.LabelStyles, c.StyleSlot));
            else if (kind == LabelStyleKind.CrossingPipe && part is Pipe)
            {
                ObjectIdCollection made = CrossingPipeProfileLabel.Create(viewPartId, viewId, StyleId(civil.Styles.LabelStyles.PipeLabelStyles.CrossProfileLabelStyles, c.StyleSlot));
                if (made == null || made.Count == 0) throw new InvalidOperationException($"Civil 3D did not create the crossing label of change {c.ChangeId}.");
                labelId = made[0];
                // One crossing label per planned change: any more Civil 3D made for the same crossing go again.
                for (int i = 1; i < made.Count; i++) tr.GetObject(made[i], OpenMode.ForWrite).Erase();
            }
            else if (kind == LabelStyleKind.Pipe && part is Pipe)
                labelId = PipeProfileLabel.Create(viewPartId, viewId, Math.Max(0, Math.Min(1, c.Ratio ?? 0.5)),
                    StyleId(civil.Styles.LabelStyles.PipeLabelStyles.PlanProfileLabelStyles, c.StyleSlot));
            else throw new InvalidOperationException($"Change {c.ChangeId}: the part is not the kind its rule labels.");
            if (labelId.IsNull) throw new InvalidOperationException($"Civil 3D did not create the label of change {c.ChangeId}.");

            CivilLabel label = (CivilLabel)tr.GetObject(labelId, OpenMode.ForWrite);
            label.XData = BuildXData.Encode(new BuildTag(c.RuleId, c.SourceKey, c.Layout, c.StyleSlot, PinState.Auto, PlanLabelsCivil3D.PlacementOf(label)));
            if (BuildXData.Decode(label.GetXDataForApplication(BuildTag.XDataApplication)) == null)
                throw new InvalidOperationException($"The build tag of change {c.ChangeId} could not be written.");
            return DrawingRefs3D.Ref(labelId);
        }

        private static ObjectId StyleId(StyleCollectionBase styles, string name)
        {
            foreach (ObjectId id in styles)
                if (!id.IsErased && id.GetObject(OpenMode.ForRead) is StyleBase st && st.Name == name) return id;
            throw new InvalidOperationException($"Label style \"{name}\" is not in the drawing.");
        }
    }
}
