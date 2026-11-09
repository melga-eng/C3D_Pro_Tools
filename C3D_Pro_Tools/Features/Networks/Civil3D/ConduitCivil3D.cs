using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using C3DProTools.Features.Networks.Application;
using C3DProTools.Features.Networks.Contracts;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

// Generate Conduits' Civil 3D side: reads encasements, structures, destinations and parts as plain values, and makes the
// planned conduits in one locked session, one nested transaction per encasement. Autodesk objects stay in this folder.
namespace C3DProTools.Features.Networks.Civil3D
{
    internal sealed class ConduitCivil3DAccess : IConduitDrawingAccess
    {
        /// <summary>How far from a chosen end a structure is read when matching unconnected ends (m): search limit plus a large manhole.</summary>
        private const double NearReadM = ConduitLimits.MaxStructureSearchM + 15.0;

        public DrawingIdentity ActiveDrawing() => DrawingRefs3D.Identity(AcadApp.DocumentManager.MdiActiveDocument);

        public double MetresPerDrawingUnit()
        {
            Database db = AcadApp.DocumentManager.MdiActiveDocument?.Database;
            return db == null ? double.NaN : Metres(db.Insunits);
        }

        internal static double Metres(UnitsValue u) => u switch
        {
            UnitsValue.Meters => 1.0,
            UnitsValue.Millimeters => 0.001,
            UnitsValue.Centimeters => 0.01,
            UnitsValue.Kilometers => 1000.0,
            UnitsValue.Feet => 0.3048,
            UnitsValue.USSurveyFeet => 1200.0 / 3937.0,
            UnitsValue.Inches => 0.0254,
            UnitsValue.Yards => 0.9144,
            _ => double.NaN
        };

        public ConduitDrawingRead Read(ConduitRequest request, CancellationToken cancel)
        {
            Document doc = DrawingRefs3D.Find(request.Drawing) ?? throw new InvalidOperationException("The drawing is not open.");
            Database db = doc.Database;
            List<OperationIssue> issues = new List<OperationIssue>();
            List<EncasementRead> encasements = new List<EncasementRead>();
            Dictionary<ObjectId, StructureRead> structures = new Dictionary<ObjectId, StructureRead>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                HashSet<ObjectId> networks = new HashSet<ObjectId>();
                List<Point3d> ends = new List<Point3d>();
                foreach (DrawingObjectRef r in request.Encasements)
                {
                    if (cancel.IsCancellationRequested) break;
                    if (!DrawingRefs3D.TryResolve(db, r, out ObjectId id) || !(tr.GetObject(id, OpenMode.ForRead) is Pipe pipe))
                    {
                        issues.Add(OperationIssue.Warning(ConduitIssueCodes.EncasementMissing, $"Encasement {r.Handle} is not a gravity pipe in this drawing any more; it is left out.", nameof(ConduitRequest.Encasements), r));
                        continue;
                    }
                    EncasementSnapshot snap = Snapshot(pipe);
                    string unsupported = pipe.SubEntityType == PipeSubEntityType.Straight || pipe.SubEntityType == PipeSubEntityType.Curved
                        ? (snap.Curved && !double.IsFinite(snap.Bulge) ? "its arc could not be read" : null)
                        : $"it is a {pipe.SubEntityType.ToString().ToLowerInvariant()} pipe; only straight and curved pipes can be filled";
                    ConduitDestinationPlan dest = Destination(tr, db, pipe, request);
                    ConduitPartSnapshot part = Part(tr, db, request.Part);
                    OperationIssue partIssue = part == null ? OperationIssue.Error(ConduitIssueCodes.PartUnavailable, $"{pipe.Name}: the conduit part could not be read (its size or the pipe it is copied from is missing); it is left out.", null, r) : null;
                    encasements.Add(new EncasementRead(r, pipe.Name, snap, unsupported, dest, part, partIssue));
                    networks.Add(pipe.NetworkId);
                    ends.Add(pipe.StartPoint);
                    ends.Add(pipe.EndPoint);
                    foreach (ObjectId sid in new[] { pipe.StartStructureId, pipe.EndStructureId })
                        if (!sid.IsNull && !structures.ContainsKey(sid) && tr.GetObject(sid, OpenMode.ForRead) is Structure st) structures[sid] = StructureOf(st);
                }
                if (request.Ends?.MatchUnconnectedEnds == true)
                    foreach (ObjectId netId in networks)
                        if (tr.GetObject(netId, OpenMode.ForRead) is Network net)
                            foreach (ObjectId sid in net.GetStructureIds())
                            {
                                if (structures.ContainsKey(sid) || !(tr.GetObject(sid, OpenMode.ForRead) is Structure st)) continue;
                                Point3d at = st.Location;
                                if (ends.Any(e => new Point2d(e.X, e.Y).GetDistanceTo(new Point2d(at.X, at.Y)) <= NearReadM)) structures[sid] = StructureOf(st);
                            }
                tr.Commit();
            }
            return new ConduitDrawingRead(encasements, structures.Values.ToList(), issues);
        }

        public IConduitWriteSession BeginWrite(DrawingIdentity drawing)
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null || !drawing.Matches(DrawingRefs3D.Identity(doc)))
                throw new InvalidOperationException("The plan's drawing is not the current drawing.");
            return new ConduitWriteSession(doc);
        }

        // ---------- Reads shared with the write session ----------

        /// <summary>The encasement's state the plan depends on. Outer sizes are rebuilt from inner size and wall (duct banks report 0).</summary>
        internal static EncasementSnapshot Snapshot(Pipe pipe)
        {
            bool curved = pipe.SubEntityType == PipeSubEntityType.Curved;
            double bulge = curved ? BulgeOf(pipe) : 0;
            string size = "";
            try { size = pipe.PartSizeName ?? ""; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            string netName = "";
            try { netName = pipe.NetworkName ?? ""; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return new EncasementSnapshot(DrawingRefs3D.Point(pipe.StartPoint), DrawingRefs3D.Point(pipe.EndPoint), DrawingRefs3D.Ref(pipe.NetworkId), netName,
                DrawingRefs3D.RefOrNull(pipe.StartStructureId), DrawingRefs3D.RefOrNull(pipe.EndStructureId), size, OuterWidthOf(pipe), CoverRoute.OuterHeightOf(pipe), curved, bulge);
        }

        /// <summary>
        /// A curved pipe's signed plan bulge from its own arc (start, mid, end): + when the arc turns left (counter-clockwise),
        /// the convention the conduit contracts use. Computed from the geometry, not Civil 3D's stored sign. NaN when unreadable.
        /// </summary>
        internal static double BulgeOf(Pipe pipe)
        {
            if (!PipeArcGeometry.TryGetGravityArc(pipe, out ArcInfo arc)) return double.NaN;
            Point3d s = pipe.StartPoint, e = pipe.EndPoint;
            double dx = e.X - s.X, dy = e.Y - s.Y, c = Math.Sqrt(dx * dx + dy * dy);
            if (c < 1e-9) return double.NaN;
            // Sagitta: the arc's mid point from the chord's middle; on the chord's right for a left (counter-clockwise) turn.
            double mx = arc.Mid.X - (s.X + e.X) / 2, my = arc.Mid.Y - (s.Y + e.Y) / 2;
            double rightSide = mx * (dy / c) + my * (-dx / c);
            double sag = Math.Sqrt(mx * mx + my * my);
            return Math.Sign(rightSide) * 2 * sag / c;
        }

        internal static double OuterWidthOf(Pipe pipe)
        {
            double outer = 0, inner = 0, wall = 0;
            try { outer = pipe.OuterDiameterOrWidth; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { inner = pipe.InnerDiameterOrWidth; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { wall = Math.Max(0, pipe.WallThickness); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return Math.Max(outer, inner + 2 * wall);
        }

        internal static StructureRead StructureOf(Structure st)
        {
            bool round = false;
            double w = 0, l = 0, wall = 0;
            string fam = "", size = "";
            try { round = st.BoundingShape == BoundingShapeType.Cylinder; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { w = Math.Max(0, st.InnerDiameterOrWidth); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { l = round ? w : Math.Max(0, st.InnerLength); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { wall = Math.Max(0, st.WallThickness); } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { fam = st.PartFamilyName ?? ""; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { size = st.PartSizeName ?? ""; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return new StructureRead(DrawingRefs3D.Ref(st.ObjectId), st.Name, fam, size, DrawingRefs3D.Ref(st.NetworkId), DrawingRefs3D.Point(st.Location), st.Rotation, round, w, l, wall);
        }

        /// <summary>True when the part choice can only be copied from a pipe in the drawing (no parts list part).</summary>
        internal static bool CopyOnly(ConduitPartChoice part) => part.Template.HasValue && !part.PartsList.IsValid;

        /// <summary>Where conduits go: a copy-only part stays in its template pipe's network; otherwise the encasement's, or a new network.</summary>
        internal static ConduitDestinationPlan Destination(Transaction tr, Database db, Pipe encasement, ConduitRequest request)
        {
            if (CopyOnly(request.Part))
            {
                ObjectId t = DrawingRefs3D.ResolveOrNull(db, request.Part.Template);
                if (t.IsNull || !(tr.GetObject(t, OpenMode.ForRead) is Pipe template)) return null;
                return new ConduitDestinationPlan(DrawingRefs3D.Ref(template.NetworkId), Safe(() => template.NetworkName));
            }
            if (request.Destination == ConduitDestination.NewNetwork)
                return new ConduitDestinationPlan(null, string.IsNullOrWhiteSpace(request.NewNetworkName) ? "Conduits" : request.NewNetworkName.Trim(), CreatedAtApply: true);
            return new ConduitDestinationPlan(DrawingRefs3D.Ref(encasement.NetworkId), Safe(() => encasement.NetworkName));
        }

        /// <summary>
        /// The part's identity as it is now: a copy-only part from its template pipe (size and rebuilt outside width); a parts
        /// list part from its size in the list when that can be read, else the chooser's values. Null when it cannot be read.
        /// </summary>
        internal static ConduitPartSnapshot Part(Transaction tr, Database db, ConduitPartChoice choice)
        {
            if (choice == null) return null;
            if (CopyOnly(choice))
            {
                ObjectId t = DrawingRefs3D.ResolveOrNull(db, choice.Template);
                if (t.IsNull || !(tr.GetObject(t, OpenMode.ForRead) is Pipe template)) return null;
                return new ConduitPartSnapshot("", Safe(() => template.PartSizeName), OuterWidthOf(template), choice.Template);
            }
            string family = string.IsNullOrWhiteSpace(choice.FamilyGuid) ? choice.FamilyName ?? "" : choice.FamilyGuid;
            double od = choice.OutsideDiameterM;
            ObjectId famId = DrawingRefs3D.ResolveOrNull(db, choice.Family), sizeId = DrawingRefs3D.ResolveOrNull(db, choice.Size);
            if (choice.Size.HasValue && sizeId.IsNull) return null; // the size it was chosen as is gone from the list
            if (!famId.IsNull && !sizeId.IsNull)
            {
                try
                {
                    if (tr.GetObject(famId, OpenMode.ForRead) is PartFamily fam && tr.GetObject(sizeId, OpenMode.ForRead) is PartSize size)
                    {
                        Dictionary<PartContextType, double> v = PartsListTools.ReadValues(fam, size.SizeDataRecord);
                        double inner = v.TryGetValue(PartContextType.PipeInnerDiameter, out double d) ? d : v.TryGetValue(PartContextType.PipeInnerWidth, out double w) ? w : 0;
                        double wall = v.TryGetValue(PartContextType.WallThickness, out double t) ? t : 0;
                        if (inner > 0) od = inner + 2 * wall;
                    }
                }
                catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            }
            return new ConduitPartSnapshot(family, choice.SizeName ?? "", od, choice.Template);
        }

        /// <summary>The part chooser's pick as a contract value (for the form, which holds a <see cref="PartPick"/>), with its outside diameter.</summary>
        public static ConduitPartChoice ToChoice(PartPick pick, double outsideDiameterM)
        {
            if (pick == null) return null;
            return new ConduitPartChoice(pick.PartsListId.IsNull ? default : DrawingRefs3D.Ref(pick.PartsListId), pick.FamilyGuid ?? "", pick.FamilyName ?? "", pick.SizeName ?? "",
                outsideDiameterM, DrawingRefs3D.RefOrNull(pick.TemplateId))
            {
                PartsListName = pick.PartsListName ?? "",
                Family = DrawingRefs3D.RefOrNull(pick.FamilyId),
                Size = DrawingRefs3D.RefOrNull(pick.SizeId),
                Dimensions = (pick.Values ?? new Dictionary<PartContextType, double>()).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
            };
        }

        internal static PartPick ToPick(Database db, ConduitPartChoice choice)
        {
            Dictionary<PartContextType, double> values = new Dictionary<PartContextType, double>();
            foreach (KeyValuePair<string, double> kv in choice.Dimensions)
                if (Enum.TryParse(kv.Key, out PartContextType t)) values[t] = kv.Value;
            return new PartPick
            {
                Domain = DomainType.Pipe,
                PartsListId = DrawingRefs3D.ResolveOrNull(db, choice.PartsList.IsValid ? choice.PartsList : (DrawingObjectRef?)null),
                PartsListName = choice.PartsListName ?? "",
                FamilyGuid = choice.FamilyGuid ?? "",
                FamilyName = choice.FamilyName ?? "",
                FamilyId = DrawingRefs3D.ResolveOrNull(db, choice.Family),
                SizeId = DrawingRefs3D.ResolveOrNull(db, choice.Size),
                SizeName = choice.SizeName ?? "",
                TemplateId = DrawingRefs3D.ResolveOrNull(db, choice.Template),
                Values = values
            };
        }

        internal static string Safe(Func<string> read)
        {
            try { return read() ?? ""; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { return ""; }
        }
    }

    /// <summary>A connection asked for that Civil 3D did not make (or did not keep); rolls back its encasement.</summary>
    internal sealed class ConduitConnectionException : Exception
    {
        public ConduitConnectionException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>One write: the document lock and the outer transaction, released on dispose (uncommitted work undone).</summary>
    internal sealed class ConduitWriteSession : IConduitWriteSession
    {
        /// <summary>A made conduit's ends must be within this of the plan (m), or the encasement is rolled back.</summary>
        private const double EndTolerance = 1e-4;

        private readonly Document _doc;
        private readonly DocumentLock _lock;
        private readonly Transaction _tr;
        private readonly NetworkEditOps.Counts _counts = new NetworkEditOps.Counts();
        private readonly List<OperationIssue> _notes = new List<OperationIssue>();
        private ObjectId _newNetwork = ObjectId.Null;
        private bool _done;

        public ConduitWriteSession(Document doc)
        {
            _doc = doc;
            _lock = doc.LockDocument();
            try { _tr = doc.Database.TransactionManager.StartTransaction(); }
            catch { _lock.Dispose(); throw; }
        }

        private Database Db => _doc.Database;

        public IReadOnlyList<OperationIssue> Notes => _notes.ToList();

        public EncasementSnapshot ReadEncasement(DrawingObjectRef encasement) =>
            DrawingRefs3D.TryResolve(Db, encasement, out ObjectId id) && _tr.GetObject(id, OpenMode.ForRead) is Pipe p ? ConduitCivil3DAccess.Snapshot(p) : null;

        public StructureSnapshot ReadStructure(DrawingObjectRef structure, ConduitRequest request) =>
            DrawingRefs3D.TryResolve(Db, structure, out ObjectId id) && _tr.GetObject(id, OpenMode.ForRead) is Structure st
                ? ConduitPlanner.Footprint(ConduitCivil3DAccess.StructureOf(st), request.Ends ?? new ConduitEndRules()) : null;

        public ConduitDestinationPlan ResolveDestination(ConduitSourcePlan item, ConduitRequest request) =>
            DrawingRefs3D.TryResolve(Db, item.Encasement, out ObjectId id) && _tr.GetObject(id, OpenMode.ForRead) is Pipe p
                ? ConduitCivil3DAccess.Destination(_tr, Db, p, request) : null;

        public ConduitPartSnapshot ResolvePart(ConduitSourcePlan item, ConduitRequest request) => ConduitCivil3DAccess.Part(_tr, Db, request.Part);

        public ConduitItemOutcome Fill(ConduitSourcePlan item, ConduitRequest request)
        {
            var before = _counts.Snapshot();
            ObjectId networkBefore = _newNetwork;
            List<ObjectId> made = new List<ObjectId>();
            int connections = 0;
            using (Transaction t = _tr.TransactionManager.StartTransaction())
            {
                try
                {
                    connections = MakeAll(t, item, request, made);
                    t.Commit();
                }
                catch (Exception ex)
                {
                    t.Abort();
                    _counts.Restore(before);
                    _newNetwork = networkBefore; // a network made in this unit was undone with it
                    C3DProLogger.Warn($"Conduits: {item.Name} rolled back", ex);
                    OperationIssue issue = ex is ConduitConnectionException
                        ? OperationIssue.Error(ConduitIssueCodes.ItemConnectionFailed, $"{item.Name}: {ex.Message} No conduits were made in it.", null, item.Encasement)
                        : OperationIssue.Error(ConduitIssueCodes.ItemFailed, $"{item.Name}: its conduits could not be made ({ex.Message}); none were left in the drawing.", null, item.Encasement);
                    return new ConduitItemOutcome(item.Encasement, item.Name, ItemStatus.RolledBack, null, 0, issue);
                }
            }
            foreach (string line in _counts.Log.Where(l => _notes.All(n => n.Message != l)))
                _notes.Add(OperationIssue.Info(ConduitIssueCodes.PartsListChanged, line));
            return new ConduitItemOutcome(item.Encasement, item.Name, ItemStatus.Committed, made.Select(DrawingRefs3D.Ref).ToList(), connections);
        }

        /// <summary>Makes every planned conduit of one encasement in transaction <paramref name="t"/>; throws on any failure (the caller rolls back).</summary>
        private int MakeAll(Transaction t, ConduitSourcePlan item, ConduitRequest request, List<ObjectId> made)
        {
            if (!DrawingRefs3D.TryResolve(Db, item.Encasement, out ObjectId encId) || !(t.GetObject(encId, OpenMode.ForRead) is Pipe encasement))
                throw new InvalidOperationException("the encasement is no longer in the drawing");
            ObjectId targetId = Target(t, item, encasement, request);
            Network target = (Network)t.GetObject(targetId, OpenMode.ForWrite);
            PartPick pick = ConduitCivil3DAccess.ToPick(Db, request.Part);
            Pipe template = !pick.TemplateId.IsNull && t.GetObject(pick.TemplateId, OpenMode.ForRead) is Pipe tp ? tp : null;
            ObjectId familyId = ObjectId.Null, sizeId = ObjectId.Null;
            if (!ConduitCivil3DAccess.CopyOnly(request.Part)) (familyId, sizeId) = _counts.PartFor(t, targetId, pick);
            bool build = PartCopy.IsFamily(t, familyId) && !sizeId.IsNull;
            if (!build && template == null) throw new InvalidOperationException($"{request.Part.Label} could not be put in the parts list of network {target.Name}");
            string layer = encasement.Layer;
            int connections = 0;

            foreach (PlannedConduit c in item.Conduits)
            {
                Point3d s = DrawingRefs3D.Point(c.Start), e = DrawingRefs3D.Point(c.End);
                ObjectId id;
                if (c.Curved)
                {
                    if (!build) throw new InvalidOperationException("a conduit part with no family cannot follow an arc");
                    (double cx, double cy, double _) = ConduitGeometry.ArcCentre(c.Start, c.End, c.Bulge);
                    using (PipeCurveGeometry g = new PipeCurveGeometry(s, new Point2d(cx, cy), e, ConduitGeometry.Clockwise(c.Bulge)))
                        id = target.AddCurvePipe(familyId, sizeId, g, false);
                }
                else id = PartCopy.Pipe(t, target, build ? familyId : ObjectId.Null, sizeId, template, s, e, out bool _, stayIfNotMovable: true);
                if (id.IsNull) throw new InvalidOperationException("Civil 3D did not return the new conduit");
                made.Add(id);
                Pipe pipe = (Pipe)t.GetObject(id, OpenMode.ForWrite);
                pipe.Layer = layer;

                // Postconditions: the conduit is where the plan put it, in the planned network, with the planned arc.
                if (pipe.StartPoint.DistanceTo(s) > EndTolerance || pipe.EndPoint.DistanceTo(e) > EndTolerance)
                    throw new InvalidOperationException($"conduit {c.Column}-{c.Row} was not placed at its planned ends");
                if (item.Destination?.Network is DrawingObjectRef planned && DrawingRefs3D.Ref(pipe.NetworkId) != planned)
                    throw new InvalidOperationException($"conduit {c.Column}-{c.Row} ended up in network {pipe.NetworkName}, not the planned one");
                if (c.Curved)
                {
                    double b = ConduitCivil3DAccess.BulgeOf(pipe);
                    if (!double.IsFinite(b) || Math.Abs(b - c.Bulge) > 1e-4) throw new InvalidOperationException($"conduit {c.Column}-{c.Row} does not follow its planned arc");
                }

                foreach ((ConduitEndPlan end, ConnectorPositionType pos) in new[] { (item.StartEnd, ConnectorPositionType.Start), (item.EndEnd, ConnectorPositionType.End) })
                {
                    if (end == null || !end.Connect || end.Structure == null) continue;
                    if (!DrawingRefs3D.TryResolve(Db, end.Structure.Structure, out ObjectId sid))
                        throw new ConduitConnectionException($"structure {end.Structure.Name} is gone, so conduit {c.Column}-{c.Row} could not be connected to it.");
                    Point3d keepS = pipe.StartPoint, keepE = pipe.EndPoint;
                    try { NetworkEditOps.ConnectEnd(pipe, pos, sid); }
                    catch (Exception ex) { throw new ConduitConnectionException($"conduit {c.Column}-{c.Row} could not be connected to {end.Structure.Name} ({ex.Message}).", ex); }
                    if (!pipe.StartPoint.IsEqualTo(keepS)) pipe.StartPoint = keepS;
                    if (!pipe.EndPoint.IsEqualTo(keepE)) pipe.EndPoint = keepE;
                    ObjectId now = pos == ConnectorPositionType.Start ? pipe.StartStructureId : pipe.EndStructureId;
                    if (now != sid) throw new ConduitConnectionException($"Civil 3D did not keep conduit {c.Column}-{c.Row} connected to {end.Structure.Name}.");
                    connections++;
                }
            }
            if (_counts.StayedIn.Count > 0)
                foreach (string net in _counts.StayedIn.Where(n => _notes.All(x => !x.Message.Contains(n))))
                    _notes.Add(OperationIssue.Info(ConduitIssueCodes.PartStaysInTemplateNetwork, $"Conduits copied from a part with no family stayed in network {net}."));
            return connections;
        }

        /// <summary>The network for this item's conduits; the new network is made once per session (in the unit that first needs it).</summary>
        private ObjectId Target(Transaction t, ConduitSourcePlan item, Pipe encasement, ConduitRequest request)
        {
            ConduitDestinationPlan dest = item.Destination ?? throw new InvalidOperationException("the destination network is not known");
            if (!dest.CreatedAtApply)
                return dest.Network is DrawingObjectRef n && DrawingRefs3D.TryResolve(Db, n, out ObjectId id) ? id : throw new InvalidOperationException("the destination network is gone");
            if (!_newNetwork.IsNull && !_newNetwork.IsErased) return _newNetwork;
            string name = dest.Name;
            _newNetwork = Network.Create(CivilDocument.GetCivilDocument(Db), ref name);
            Network created = (Network)t.GetObject(_newNetwork, OpenMode.ForWrite);
            Network source = (Network)t.GetObject(encasement.NetworkId, OpenMode.ForRead);
            ObjectId list = DrawingRefs3D.ResolveOrNull(Db, request.Part.PartsList.IsValid ? request.Part.PartsList : (DrawingObjectRef?)null);
            try { created.PartsListId = list.IsNull ? source.PartsListId : list; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            // A network made without a surface throws on reading it; the source's is copied only when it has one.
            try { created.ReferenceSurfaceId = source.ReferenceSurfaceId; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            return _newNetwork;
        }

        public void Commit()
        {
            _tr.Commit();
            _done = true;
        }

        public void Dispose()
        {
            try { _tr.Dispose(); }
            finally { _lock.Dispose(); }
            if (!_done) C3DProLogger.Info("Conduits: the write session ended without commit; its changes were undone.");
        }
    }
}
