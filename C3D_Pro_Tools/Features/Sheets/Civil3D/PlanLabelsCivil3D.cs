using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using C3DProTools.Features.Networks.Civil3D;
using C3DProTools.Features.Sheets.Contracts;
using CivilLabel = Autodesk.Civil.DatabaseServices.Label;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

// Writes one PlanLabels item (one plan sheet) to the drawing: creates the planned labels with their build tag, erases the ones
// planned for removal. The item is one transaction: it commits whole or not at all (docs/sheet-production-contract.md,
// section 10, "Atomicity"). The caller holds the document lock.
namespace C3DProTools.Features.Sheets.Civil3D
{
    internal static class PlanLabelsCivil3D
    {
        public static BuildItemOutcome Apply(Document doc, BuildItem item)
        {
            Database db = doc.Database;
            CivilDocument civil = CivilDocument.GetCivilDocument(db);
            List<DrawingObjectRef> created = new List<DrawingObjectRef>();
            int added = 0, removed = 0;
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    (added, removed) = ApplyChanges(tr, db, civil, item.Changes, created);
                    tr.Commit();
                }
                return new BuildItemOutcome(item.ItemKey, ItemStatus.Committed, added, 0, removed, created, null);
            }
            catch (Exception ex)
            {
                C3DProLogger.Error($"Plan labels: sheet {string.Join(", ", item.Layouts)} was rolled back", ex);
                return new BuildItemOutcome(item.ItemKey, ItemStatus.RolledBack, 0, 0, 0, null,
                    new[] { OperationIssue.Error(SheetCodes.ApplyPostconditionFailed, $"The labels of this sheet could not be written, so none were: {ex.Message}") with { Layout = item.Layouts.Count > 0 ? item.Layouts[0] : null } });
            }
        }

        /// <summary>
        /// The PlanLabels changes of an item, in the caller's transaction (the build joins them with Arrange in one): labels
        /// added with their tag, labels erased. Throws on the first failure, so the caller's transaction rolls the item back.
        /// </summary>
        internal static (int added, int removed) ApplyChanges(Transaction tr, Database db, CivilDocument civil, IEnumerable<PlannedChange> changes, List<DrawingObjectRef> created)
        {
            int added = 0, removed = 0;
            EnsureRegApp(tr, db);
            foreach (PlannedChange c in changes)
            {
                if (c.Step != BuildStep.PlanLabels || c.Target != ChangeTarget.Label) continue;
                if (c.Kind == ChangeKind.Add)
                {
                    created.Add(Add(tr, db, civil, c));
                    added++;
                }
                else if (c.Kind == ChangeKind.Remove)
                {
                    if (!c.Existing.HasValue || !DrawingRefs3D.TryResolve(db, c.Existing.Value, out ObjectId id)) continue;   // gone already
                    DBObject label = tr.GetObject(id, OpenMode.ForWrite);
                    label.Erase();
                    removed++;
                }
            }
            return (added, removed);
        }

        private static DrawingObjectRef Add(Transaction tr, Database db, CivilDocument civil, PlannedChange c)
        {
            if (!c.Part.HasValue || !DrawingRefs3D.TryResolve(db, c.Part.Value, out ObjectId partId))
                throw new InvalidOperationException($"The part of change {c.ChangeId} is no longer in the drawing.");
            DBObject part = tr.GetObject(partId, OpenMode.ForRead);
            ObjectId labelId;
            if (part is Pipe)
            {
                ObjectId style = StyleId(civil.Styles.LabelStyles.PipeLabelStyles.PlanProfileLabelStyles, c.StyleSlot);
                labelId = PipeLabel.Create(partId, Math.Max(0, Math.Min(1, c.Ratio ?? 0.5)), style);
            }
            else if (part is Structure s)
            {
                ObjectId style = StyleId(civil.Styles.LabelStyles.StructureLabelStyles.LabelStyles, c.StyleSlot);
                labelId = StructureLabel.Create(partId, style, s.Location);
            }
            else throw new InvalidOperationException($"Change {c.ChangeId} names an object that is not a pipe or structure.");
            if (labelId.IsNull) throw new InvalidOperationException($"Civil 3D did not create the label of change {c.ChangeId}.");

            CivilLabel label = (CivilLabel)tr.GetObject(labelId, OpenMode.ForWrite);
            label.XData = BuildXData.Encode(new BuildTag(c.RuleId, c.SourceKey, c.Layout, c.StyleSlot, PinState.Auto, PlacementOf(label)));
            // Postcondition: the label carries its tag, so the next build finds it again.
            if (BuildXData.Decode(label.GetXDataForApplication(BuildTag.XDataApplication)) == null)
                throw new InvalidOperationException($"The build tag of change {c.ChangeId} could not be written.");
            return DrawingRefs3D.Ref(labelId);
        }

        /// <summary>The label's placement as the scene reader reads it, so a fresh label is never mistaken for a user's edit.</summary>
        internal static RecordedPlacement PlacementOf(CivilLabel label)
        {
            var at = label.LabelLocation;
            double? ratio = null;
            if (label is PipeLabel pl) try { ratio = pl.Ratio; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return new RecordedPlacement(label.StyleName, label.Dragged, at.X, at.Y, ratio, label.CanRotate ? label.RotationAngle : 0.0);
        }

        private static ObjectId StyleId(Autodesk.Civil.DatabaseServices.Styles.StyleCollectionBase styles, string name)
        {
            foreach (ObjectId id in styles)
                if (!id.IsErased && id.GetObject(OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Styles.StyleBase st && st.Name == name) return id;
            throw new InvalidOperationException($"Label style \"{name}\" is not in the drawing.");
        }

        internal static void EnsureRegApp(Transaction tr, Database db)
        {
            RegAppTable table = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (table.Has(BuildTag.XDataApplication)) return;
            table.UpgradeOpen();
            RegAppTableRecord app = new RegAppTableRecord { Name = BuildTag.XDataApplication };
            table.Add(app);
            tr.AddNewlyCreatedDBObject(app, true);
        }
    }
}
