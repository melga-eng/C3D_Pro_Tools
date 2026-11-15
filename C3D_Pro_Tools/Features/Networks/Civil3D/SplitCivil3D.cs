using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using C3DProTools.Features.Networks.Application;
using C3DProTools.Features.Networks.Contracts;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

// The split's Civil 3D side: drawing identity, handles, reads, the document lock and transactions, and the existing split
// engine (NetworkEditOps) for the geometry and edits. Autodesk objects stay in this folder; the workflow sees plain values.
namespace C3DProTools.Features.Networks.Civil3D
{
    /// <summary>Drawing identities, handles and points between the contracts and AutoCAD.</summary>
    internal static class DrawingRefs3D
    {
        // One key per open document, for as long as it stays open (a reopened drawing is a new Document, so a new key).
        private static readonly ConditionalWeakTable<Document, string> Keys = new ConditionalWeakTable<Document, string>();

        public static DrawingIdentity Identity(Document doc)
        {
            if (doc == null) return null;
            string key = Keys.GetValue(doc, _ => Guid.NewGuid().ToString("N"));
            Database db = doc.Database;
            return new DrawingIdentity(key, db.FingerprintGuid, doc.Name, UnitLabel(db.Insunits));
        }

        /// <summary>The open document a drawing identity names, or null when it is not open (or not the same document any more).</summary>
        public static Document Find(DrawingIdentity drawing)
        {
            if (drawing == null) return null;
            foreach (Document d in AcadApp.DocumentManager)
                if (drawing.Matches(Identity(d))) return d;
            return null;
        }

        public static string UnitLabel(UnitsValue units) => units switch
        {
            UnitsValue.Meters => "m",
            UnitsValue.Millimeters => "mm",
            UnitsValue.Centimeters => "cm",
            UnitsValue.Kilometers => "km",
            UnitsValue.Feet => "ft",
            UnitsValue.USSurveyFeet => "US ft",
            UnitsValue.Inches => "in",
            UnitsValue.Yards => "yd",
            _ => null
        };

        public static DrawingObjectRef Ref(ObjectId id) => DrawingObjectRef.Of(id.Handle.ToString());
        public static DrawingObjectRef? RefOrNull(ObjectId id) => id.IsNull ? null : Ref(id);

        /// <summary>The live object a handle names in this drawing; false when there is none or it is erased.</summary>
        public static bool TryResolve(Database db, DrawingObjectRef r, out ObjectId id)
        {
            id = ObjectId.Null;
            if (!r.IsValid) return false;
            if (!db.TryGetObjectId(new Handle(Convert.ToInt64(r.Handle, 16)), out id) || id.IsNull || id.IsErased) { id = ObjectId.Null; return false; }
            return true;
        }

        public static ObjectId ResolveOrNull(Database db, DrawingObjectRef? r) => r.HasValue && TryResolve(db, r.Value, out ObjectId id) ? id : ObjectId.Null;

        public static Point3 Point(Point3d p) => new Point3(p.X, p.Y, p.Z);
        public static Point3d Point(Point3 p) => new Point3d(p.X, p.Y, p.Z);

        /// <summary>The live objects these handles name in this drawing (missing ones left out).</summary>
        public static List<ObjectId> ObjectIds(Database db, IEnumerable<DrawingObjectRef> refs) =>
            refs.Select(r => TryResolve(db, r, out ObjectId id) ? id : ObjectId.Null).Where(id => !id.IsNull).ToList();
    }

    /// <summary>The split's access to Civil 3D: plans with the existing engine and writes in one locked, transacted session.</summary>
    internal sealed class SplitCivil3DAccess : ISplitDrawingAccess
    {
        public DrawingIdentity ActiveDrawing() => DrawingRefs3D.Identity(AcadApp.DocumentManager.MdiActiveDocument);

        public DrawingSplitPlan Plan(SplitRequest request, CancellationToken cancel)
        {
            Document doc = DrawingRefs3D.Find(request.Drawing) ?? throw new InvalidOperationException("The drawing is not open.");
            Database db = doc.Database;
            List<OperationIssue> issues = new List<OperationIssue>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                List<ObjectId> pipes = new List<ObjectId>();
                foreach (DrawingObjectRef r in request.Pipes)
                {
                    if (DrawingRefs3D.TryResolve(db, r, out ObjectId id) && tr.GetObject(id, OpenMode.ForRead) is Pipe) pipes.Add(id);
                    else issues.Add(OperationIssue.Warning(SplitIssueCodes.PipeMissing, $"Pipe {r.Handle} is not a gravity pipe in this drawing any more; it is left out.", nameof(SplitRequest.Pipes), r));
                }
                int missed = 0;
                List<NetworkEditOps.SplitPlan> plans;
                if (request.Mode == SplitMode.AtPoints)
                {
                    // No pipes chosen: a point may split any gravity pipe of the drawing (as Network Editor does today).
                    if (request.Pipes.Count == 0)
                        foreach (ObjectId netId in CivilDocument.GetCivilDocument(db).GetPipeNetworkIds())
                            if (tr.GetObject(netId, OpenMode.ForRead) is Network n) pipes.AddRange(n.GetPipeIds().Live());
                    plans = NetworkEditOps.PlanSplitAtPoints(tr, pipes, request.Picks.Select(DrawingRefs3D.Point).ToList(), request.PickTolerance, out missed, cancel);
                }
                // The piece limit is applied before any point is made, so a tiny length on a long pipe never builds a huge plan.
                else plans = NetworkEditOps.PlanSplit(tr, pipes, request.Parts, request.Mode == SplitMode.FixedLength ? request.Every : 0, SplitLimits.MaxPiecesPerPipe, cancel);

                List<PlannedPipe> planned = new List<PlannedPipe>();
                foreach (NetworkEditOps.SplitPlan p in plans)
                {
                    if (cancel.IsCancellationRequested) break;
                    if (p.TooManyPieces > 0)
                    {
                        issues.Add(OperationIssue.Warning(SplitIssueCodes.TooManyPieces,
                            $"{p.Name} would be split into {p.TooManyPieces} pieces (at most {SplitLimits.MaxPiecesPerPipe}); it is left out. Use a longer length.",
                            nameof(SplitRequest.Every), DrawingRefs3D.Ref(p.PipeId)));
                        continue;
                    }
                    Pipe pipe = (Pipe)tr.GetObject(p.PipeId, OpenMode.ForRead);
                    SplitItemPlan item = new SplitItemPlan(DrawingRefs3D.Ref(p.PipeId), p.Name, p.Curved, p.Length, p.Points.Select(DrawingRefs3D.Point).ToList(), Snapshot(tr, pipe));
                    planned.Add(new PlannedPipe(item, p.Along));
                }
                tr.Commit();
                return new DrawingSplitPlan(planned, missed, issues);
            }
        }

        public ISplitWriteSession BeginWrite(DrawingIdentity drawing)
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            // Written only to the drawing the plan is for, and only while it is the current one.
            if (doc == null || !drawing.Matches(DrawingRefs3D.Identity(doc)))
                throw new InvalidOperationException("The plan's drawing is not the current drawing.");
            return new SplitWriteSession(doc);
        }

        /// <summary>The pipe's state the split depends on (for the stale check).</summary>
        internal static PipeSnapshot Snapshot(Transaction tr, Pipe pipe)
        {
            bool curved = pipe.SubEntityType == PipeSubEntityType.Curved;
            double bulge = 0;
            if (curved)
            {
                try { using (PipeCurveGeometry g = pipe.CurveGeometry) bulge = g?.Bulge ?? double.NaN; }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { bulge = double.NaN; }
            }
            string sizeName = "";
            try { sizeName = pipe.PartSizeName ?? ""; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            ObjectId family = ObjectId.Null;
            try { family = pipe.PartFamilyId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return new PipeSnapshot(DrawingRefs3D.Point(pipe.StartPoint), DrawingRefs3D.Point(pipe.EndPoint), DrawingRefs3D.Ref(pipe.NetworkId),
                DrawingRefs3D.RefOrNull(pipe.StartStructureId), DrawingRefs3D.RefOrNull(pipe.EndStructureId),
                DrawingRefs3D.RefOrNull(family), DrawingRefs3D.RefOrNull(PipePartCatalog.SizeIdOf(tr, pipe)), sizeName, curved, bulge);
        }

        /// <summary>The part chooser's pick as a contract value (for the form, which holds a <see cref="PartPick"/>).</summary>
        public static SplitStructureChoice ToChoice(PartPick pick)
        {
            if (pick == null) return null;
            return new SplitStructureChoice(pick.PartsListId.IsNull ? default : DrawingRefs3D.Ref(pick.PartsListId), pick.FamilyGuid ?? "", pick.FamilyName ?? "", pick.SizeName ?? "",
                DrawingRefs3D.RefOrNull(pick.TemplateId), pick.SwitchNetwork)
            {
                PartsListName = pick.PartsListName ?? "",
                Family = DrawingRefs3D.RefOrNull(pick.FamilyId),
                Size = DrawingRefs3D.RefOrNull(pick.SizeId),
                Dimensions = (pick.Values ?? new Dictionary<PartContextType, double>()).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
            };
        }

        /// <summary>A contract structure choice as the engine's <see cref="PartPick"/>, its handles resolved in this drawing.</summary>
        internal static PartPick ToPick(Database db, SplitStructureChoice choice)
        {
            if (choice == null) return null;
            Dictionary<PartContextType, double> values = new Dictionary<PartContextType, double>();
            foreach (KeyValuePair<string, double> kv in choice.Dimensions)
                if (Enum.TryParse(kv.Key, out PartContextType t)) values[t] = kv.Value;
            return new PartPick
            {
                Domain = DomainType.Structure,
                PartsListId = DrawingRefs3D.ResolveOrNull(db, choice.PartsList.IsValid ? choice.PartsList : (DrawingObjectRef?)null),
                PartsListName = choice.PartsListName ?? "",
                FamilyGuid = choice.FamilyGuid ?? "",
                FamilyName = choice.FamilyName ?? "",
                FamilyId = DrawingRefs3D.ResolveOrNull(db, choice.Family),
                SizeId = DrawingRefs3D.ResolveOrNull(db, choice.Size),
                SizeName = choice.SizeName ?? "",
                TemplateId = DrawingRefs3D.ResolveOrNull(db, choice.Template),
                Values = values,
                SwitchNetwork = choice.SwitchNetwork
            };
        }
    }

    /// <summary>One write: the document lock and the outer transaction, both released on dispose (uncommitted work undone).</summary>
    internal sealed class SplitWriteSession : ISplitWriteSession
    {
        private readonly Document _doc;
        private readonly DocumentLock _lock;
        private readonly Transaction _tr;
        private readonly List<OperationIssue> _notes = new List<OperationIssue>();
        private readonly Dictionary<SplitStructureChoice, PartPick> _picks = new Dictionary<SplitStructureChoice, PartPick>();
        private bool _done;

        public SplitWriteSession(Document doc)
        {
            _doc = doc;
            _lock = doc.LockDocument();
            try { _tr = doc.Database.TransactionManager.StartTransaction(); }
            catch { _lock.Dispose(); throw; }
        }

        public IReadOnlyList<OperationIssue> Notes => _notes.ToList();

        public PipeSnapshot ReadPipe(DrawingObjectRef pipe) =>
            DrawingRefs3D.TryResolve(_doc.Database, pipe, out ObjectId id) && _tr.GetObject(id, OpenMode.ForRead) is Pipe p ? SplitCivil3DAccess.Snapshot(_tr, p) : null;

        public SplitItemOutcome SplitPipe(SplitItemPlan item, SplitStructureChoice structure)
        {
            if (!DrawingRefs3D.TryResolve(_doc.Database, item.Pipe, out ObjectId pipeId))
                return Undone(item, OperationIssue.Error(SplitIssueCodes.PipeMissing, $"{item.PipeName} is no longer in the drawing; it was not split.", null, item.Pipe));
            NetworkEditOps.SplitPlan plan = new NetworkEditOps.SplitPlan { PipeId = pipeId, Name = item.PipeName, Curved = item.Curved, Length = item.Length };
            foreach (Point3 p in item.Points) plan.Points.Add(DrawingRefs3D.Point(p));
            PartPick pick = null;
            if (structure != null && !_picks.TryGetValue(structure, out pick)) _picks[structure] = pick = SplitCivil3DAccess.ToPick(_doc.Database, structure);
            NetworkEditOps.Counts c;
            try
            {
                // The engine splits the pipe in its own nested transaction (parts list edits for the structure included): all of
                // it, or none of it. Its failures are caught there and come back in the counts.
                c = NetworkEditOps.ApplySplit(_tr, new List<NetworkEditOps.SplitPlan> { plan }, pick, _doc.Editor);
            }
            catch (Exception ex)
            {
                // Outside the pipe's own transaction, where nothing is written (building the plan or the part pick).
                C3DProLogger.Warn($"Split: {item.PipeName} could not be prepared", ex);
                return Undone(item, OperationIssue.Error(SplitIssueCodes.ItemFailed, $"{item.PipeName} could not be split ({ex.Message}); it is left as it was.", null, item.Pipe));
            }
            foreach (string line in c.Log.Where(l => _notes.All(n => n.Message != l)))
                _notes.Add(OperationIssue.Info(SplitIssueCodes.PartsListChanged, line));
            if (c.Failed > 0 || c.Done == 0)
            {
                (ObjectId _, string message, bool connection) = c.Failures.FirstOrDefault();
                return Undone(item, connection
                    ? OperationIssue.Error(SplitIssueCodes.ItemConnectionFailed, $"{item.PipeName}: {message} The pipe is left as it was.", null, item.Pipe)
                    : OperationIssue.Error(SplitIssueCodes.ItemFailed, $"{item.PipeName} could not be split ({message ?? "no reason given"}); it is left as it was.", null, item.Pipe));
            }
            OperationIssue warning = c.Warnings.Count > 0 ? OperationIssue.Warning(SplitIssueCodes.StructureUnavailable, c.Warnings[0].message, nameof(SplitRequest.Structure), item.Pipe) : null;
            return new SplitItemOutcome(item.Pipe, item.PipeName, ItemStatus.Committed, c.NewIds.Select(DrawingRefs3D.Ref).ToList(), c.NewStructureIds.Select(DrawingRefs3D.Ref).ToList(), warning);
        }

        public void Commit()
        {
            _tr.Commit();
            _done = true;
        }

        public void Dispose()
        {
            // Not committed: disposing the transaction aborts it, undoing the session.
            try { _tr.Dispose(); }
            finally { _lock.Dispose(); }
            if (!_done) C3DProLogger.Info("Split: the write session ended without commit; its changes were undone.");
        }

        private static SplitItemOutcome Undone(SplitItemPlan item, OperationIssue issue) => new SplitItemOutcome(item.Pipe, item.PipeName, ItemStatus.RolledBack, null, null, issue);
    }
}
