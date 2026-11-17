using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AcadEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using AcadTable = Autodesk.AutoCAD.DatabaseServices.Table;
using AcadViewport = Autodesk.AutoCAD.DatabaseServices.Viewport;
using CivilEntity = Autodesk.Civil.DatabaseServices.Entity;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivilTable = Autodesk.Civil.DatabaseServices.Table;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using RuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace C3DProTools
{
    internal enum XrefHandling { Keep, DetachUnreferenced, DetachAll, Bind }

    internal enum DataReferenceHandling { Leave, Strip }

    /// <summary>What the Drawing Cleaner removes.</summary>
    internal sealed class CleanupOptions
    {
        public bool Corridors, Surfaces, Alignments, CogoPoints, FeatureLines, Parcels;
        public bool LabelsTables, Annotations, Blocks, CadGeometry, Sheets, DeepPurge;
        /// <summary>Names of the networks to delete (all others are kept).</summary>
        public readonly HashSet<string> GravityNetworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> PressureNetworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public XrefHandling Xrefs = XrefHandling.Keep;
        public DataReferenceHandling DataReferences = DataReferenceHandling.Leave;
    }

    /// <summary>What the Drawing Cleaner did, including every object it could not delete (by handle and type).</summary>
    internal sealed class CleanupReport
    {
        public int Deleted, SheetsRemoved, Purged, XrefsDetached, XrefsBound, XrefsRebased;
        public readonly List<string> Notes = new List<string>();
        public readonly List<string> Failures = new List<string>();
        public readonly Dictionary<string, int> FailuresByType = new Dictionary<string, int>();

        public void Fail(ObjectId id, DBObject obj, string reason)
        {
            string type = obj?.GetType().Name ?? (id.IsNull ? "?" : id.ObjectClass.Name);
            FailuresByType[type] = FailuresByType.TryGetValue(type, out int n) ? n + 1 : 1;
            Failures.Add($"<{(id.IsNull ? "-" : id.Handle.ToString())}> {type}: {reason}");
        }

        public string FailureSummary(int maxTypes = 8) =>
            string.Join(", ", FailuresByType.OrderByDescending(kv => kv.Value).Take(maxTypes).Select(kv => $"{kv.Value} {kv.Key}"));
    }

    /// <summary>
    /// The Drawing Cleaner's work, on any database. <see cref="ExportCleanCopy"/> never changes the drawing on screen:
    /// it cleans a copy of it (unsaved edits included) in a side database and saves that.
    /// </summary>
    internal static class DrawingCleanup
    {
        // =====================================================================================
        // Entry points
        // =====================================================================================

        /// <summary>
        /// Saves a cleaned copy of the active drawing to <paramref name="targetPath"/>. The drawing on screen is only read:
        /// it is copied (with its unsaved changes) to a temporary file, that copy is opened as a side database, cleaned and
        /// saved. Nothing is written when the copy turns out incomplete or anything fails unexpectedly.
        /// </summary>
        public static CleanupReport ExportCleanCopy(Document doc, string targetPath, CleanupOptions o)
        {
            CleanupReport report = new CleanupReport();
            string source = doc.Database.Filename ?? "";
            if (Path.IsPathRooted(source) && string.Equals(Path.GetFullPath(source), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The cleaned copy cannot be saved over the drawing that is open; choose another file name.");

            string temp = Path.Combine(Path.GetTempPath(), $"c3dpro-clean-{Guid.NewGuid():N}.dwg");
            try
            {
                // 1. The copy to clean. A drawing with no unsaved changes: its saved file, an exact copy. Wblock leaves Civil 3D
                // objects out (753 of 14277 on the user's 000501 drawing, 2026-11-17), which the copy check then refuses.
                // With unsaved changes: the drawing as it is in memory (Wblock only reads the database).
                bool saved = Path.IsPathRooted(source) && File.Exists(source) &&
                             Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("DBMOD")) == 0;
                if (saved) File.Copy(source, temp, true);
                else
                    using (doc.LockDocument())
                    using (Database snapshot = doc.Database.Wblock())
                        snapshot.SaveAs(temp, DwgVersion.Current);

                // 2. Cleaned as a side database, then saved where asked.
                using (Database side = new Database(false, true))
                {
                    side.ReadDwgFile(temp, FileOpenMode.OpenForReadAndAllShare, true, null);
                    side.CloseInput(true);
                    try { VerifyCopy(doc.Database, side, report); }
                    catch (InvalidOperationException ex) when (!saved)
                    {
                        throw new InvalidOperationException(ex.Message + " Save the drawing and run again: a saved drawing is copied exactly.");
                    }

                    Database previous = HostApplicationServices.WorkingDatabase;
                    HostApplicationServices.WorkingDatabase = side;
                    try
                    {
                        string sourceFolder = Path.IsPathRooted(source) ? Path.GetDirectoryName(source) : null;
                        Clean(side, o, report, sourceFolder, Path.GetDirectoryName(Path.GetFullPath(targetPath)));
                    }
                    finally { HostApplicationServices.WorkingDatabase = previous; }
                    side.SaveAs(targetPath, DwgVersion.Current);
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return report;
        }

        /// <summary>Cleans the active drawing itself (the user's explicit choice: no copy is saved).</summary>
        public static CleanupReport CleanInPlace(Document doc, CleanupOptions o)
        {
            CleanupReport report = new CleanupReport();
            using (doc.LockDocument()) Clean(doc.Database, o, report, null, null);
            return report;
        }

        /// <summary>
        /// The copy must hold everything the drawing does; checked rather than assumed, since a copy missing something would
        /// be saved as a wrong "cleaned" drawing. Each count is compared and every difference reported exactly. Proxy objects
        /// (custom objects whose application is not loaded) are the one thing Wblock may leave out: that is noted, not stopped.
        /// </summary>
        private static void VerifyCopy(Database original, Database copy, CleanupReport report)
        {
            Dictionary<string, int> a = Census(original), b = Census(copy);
            List<string> deltas = a.Where(kv => b.TryGetValue(kv.Key, out int n) && n != kv.Value)
                                   .Select(kv => $"{kv.Key}: {kv.Value} in the drawing, {b[kv.Key]} in the copy").ToList();
            foreach (string d in deltas) report.Notes.Add("Copy check — " + d);
            List<string> lost = a.Where(kv => kv.Key != ProxyKey && b.TryGetValue(kv.Key, out int n) && n < kv.Value).Select(kv => $"{kv.Value - b[kv.Key]} {kv.Key}").ToList();
            if (lost.Count > 0)
                throw new InvalidOperationException("The copy of the drawing made for cleaning is missing " + string.Join(", ", lost) +
                                                    " (" + string.Join("; ", deltas) + "). Nothing was cleaned or saved, and the drawing on screen is unchanged.");
            if (b[ProxyKey] < a[ProxyKey])
                report.Notes.Add($"{a[ProxyKey] - b[ProxyKey]} proxy object(s) (custom objects whose application is not loaded) were left out of the copy by AutoCAD.");
        }

        private const string ProxyKey = "proxy objects";

        private static Dictionary<string, int> Census(Database db)
        {
            Dictionary<string, int> c = new Dictionary<string, int>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                List<ObjectId> all = ms.Cast<ObjectId>().ToList();
                int proxies = all.Count(id => id.ObjectClass.DxfName == "ACAD_PROXY_ENTITY");
                c["model space objects"] = all.Count - proxies;
                c[ProxyKey] = proxies;
                DBDictionary layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                c["layouts"] = layouts.Count;
                CivilDocument civil = CivilDocument.GetCivilDocument(db);
                c["alignments"] = civil.GetAlignmentIds().Count;
                c["surfaces"] = civil.GetSurfaceIds().Count;
                c["corridors"] = civil.CorridorCollection.Count;
                c["pipe networks"] = civil.GetPipeNetworkIds().Count;
                c["pressure networks"] = civil.GetPressurePipeNetworkIds().Count;
                tr.Commit();
            }
            return c;
        }

        // =====================================================================================
        // Cleaning (any database; it must be the working database for the layout steps)
        // =====================================================================================

        /// <param name="sourceFolder">The folder relative xref paths were relative to (null: leave them).</param>
        /// <param name="targetFolder">The folder the result is saved in (null: in place).</param>
        private static void Clean(Database db, CleanupOptions o, CleanupReport report, string sourceFolder, string targetFolder)
        {
            CivilDocument civil = CivilDocument.GetCivilDocument(db);
            UnlockAllLayers(db, report);
            if (o.Sheets) report.SheetsRemoved = CleanPaperSpaceLayouts(db, report);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Top-down, so nothing is erased while something built on it still exists.
                if (o.LabelsTables) Sweep(tr, db, report, obj => KindOf(obj) == Kind.LabelOrTable);
                if (o.DataReferences == DataReferenceHandling.Strip)
                    Sweep(tr, db, report, obj => obj is CivilEntity ce && IsReference(ce), includeNetworkParts: true);

                if (o.Corridors)
                {
                    foreach (ObjectId id in civil.CorridorCollection) TryErase(tr, id, report);
                    foreach (ObjectId id in civil.AssemblyCollection) TryErase(tr, id, report);
                    Sweep(tr, db, report, obj => obj is Subassembly);
                }

                EraseNetworks(tr, civil, o, report);

                if (o.Alignments)
                {
                    Sweep(tr, db, report, obj => obj is ProfileView || obj is SectionView || obj is SuperelevationView || obj is MassHaulView || IsCantView(obj));
                    foreach (ObjectId alId in civil.GetAlignmentIds())
                    {
                        if (!(OpenForRead(tr, alId) is Alignment al)) continue;
                        foreach (ObjectId id in al.GetSampleLineGroupIds()) TryErase(tr, id, report);
                        foreach (ObjectId id in al.GetProfileIds()) TryErase(tr, id, report);
                    }
                    foreach (ObjectId id in civil.GetAlignmentIds()) TryErase(tr, id, report);
                    Sweep(tr, db, report, obj => KindOf(obj) == Kind.AlignmentOrView);
                }

                if (o.FeatureLines)
                {
                    try { foreach (ObjectId id in civil.GetSitelessFeatureLineIds()) TryErase(tr, id, report); }
                    catch (RuntimeException ex) { report.Notes.Add("Feature lines outside sites could not be listed: " + ex.ErrorStatus); }
                    foreach (ObjectId siteId in civil.GetSiteIds())
                        if (OpenForRead(tr, siteId) is Site site)
                            foreach (ObjectId id in site.GetFeatureLineIds()) TryErase(tr, id, report);
                    Sweep(tr, db, report, obj => KindOf(obj) == Kind.FeatureLineOrGrading && !(obj is Site));
                }

                if (o.Surfaces) foreach (ObjectId id in civil.GetSurfaceIds()) TryErase(tr, id, report);

                if (o.CogoPoints)
                {
                    ObjectId allPoints = civil.PointGroups.AllPointsPointGroupId;
                    foreach (ObjectId id in civil.PointGroups) if (id != allPoints) TryErase(tr, id, report);
                    foreach (ObjectId id in civil.CogoPoints) TryErase(tr, id, report);
                }

                if (o.Parcels)
                {
                    foreach (ObjectId siteId in civil.GetSiteIds())
                    {
                        if (!(OpenForRead(tr, siteId) is Site site)) continue;
                        foreach (ObjectId id in site.GetParcelIds()) TryErase(tr, id, report);
                        // A site goes once nothing it holds is kept.
                        if (o.FeatureLines) TryErase(tr, siteId, report);
                    }
                }

                // Whatever is left of the chosen kinds (two passes: erasing some objects frees others).
                for (int pass = 0; pass < 2; pass++)
                    Sweep(tr, db, report, obj => Wanted(KindOf(obj), o));
                tr.Commit();
            }

            HandleXrefs(db, o, report, sourceFolder, targetFolder);
            if (o.DeepPurge) report.Purged = DeepPurgeDatabase(db, report);
        }

        // ---------------- Kinds of object ----------------

        internal enum Kind { NetworkPart, LabelOrTable, CorridorOrAssembly, Surface, AlignmentOrView, CogoPoint, FeatureLineOrGrading, ParcelOrCatchment, Annotation, Block, XrefInsert, CadGeometry, Other }

        internal static Kind KindOf(DBObject obj)
        {
            if (IsPhysicalPipeNetworkPart(obj)) return Kind.NetworkPart;
            if (IsCivilLabelOrTable(obj)) return Kind.LabelOrTable;
            if (obj is Corridor || obj is Assembly || obj is Subassembly) return Kind.CorridorOrAssembly;
            if (obj is CivilSurface) return Kind.Surface;
            if (IsAlignmentOrViewObject(obj)) return Kind.AlignmentOrView;
            if (obj is CogoPoint) return Kind.CogoPoint;
            if (IsFeatureLineOrGradingObject(obj)) return Kind.FeatureLineOrGrading;
            if (IsParcelOrCatchmentObject(obj)) return Kind.ParcelOrCatchment;
            if (IsAcadAnnotationObject(obj)) return Kind.Annotation;
            if (obj is BlockReference br && IsXrefInsert(br)) return Kind.XrefInsert;
            if (IsBlockOrReferenceObject(obj)) return Kind.Block;
            if (obj is AcadEntity) return Kind.CadGeometry;
            return Kind.Other;
        }

        private static bool Wanted(Kind k, CleanupOptions o)
        {
            switch (k)
            {
                case Kind.LabelOrTable: return o.LabelsTables;
                case Kind.CorridorOrAssembly: return o.Corridors;
                case Kind.Surface: return o.Surfaces;
                case Kind.AlignmentOrView: return o.Alignments;
                case Kind.CogoPoint: return o.CogoPoints;
                case Kind.FeatureLineOrGrading: return o.FeatureLines;
                case Kind.ParcelOrCatchment: return o.Parcels;
                case Kind.Annotation: return o.Annotations;
                case Kind.Block: return o.Blocks;
                case Kind.CadGeometry: return o.CadGeometry;
                // Network parts follow the network lists, xref inserts the xref choice.
                default: return false;
            }
        }

        /// <summary>True ONLY for physical network parts (pipes, structures, pressure parts); their labels are labels.</summary>
        internal static bool IsPhysicalPipeNetworkPart(DBObject obj) =>
            obj is Network || obj is Pipe || obj is Structure || obj is PressurePipeNetwork || obj is PressurePipe || obj is PressureFitting || obj is PressureAppurtenance;

        /// <summary>All Civil 3D labels (alignment, surface, pipe, structure, note, general...) and Civil 3D tables.</summary>
        internal static bool IsCivilLabelOrTable(DBObject obj)
        {
            if (obj is LabelBase || obj is Autodesk.Civil.DatabaseServices.Label || obj is FeatureLabel || obj is NoteLabel ||
                obj is PipeLabel || obj is StructureLabel || obj is CivilTable)
                return true;
            string typeName = obj.GetType().Name;
            string rxName = obj.GetRXClass()?.Name ?? "";
            return typeName.IndexOf("Label", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   rxName.IndexOf("Label", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (obj is CivilEntity && typeName.IndexOf("Table", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static bool IsAlignmentOrViewObject(DBObject obj) =>
            obj is Alignment || obj is Profile || obj is ProfileView || obj is SampleLine || obj is SampleLineGroup ||
            obj is Autodesk.Civil.DatabaseServices.Section || obj is SectionView || obj is SuperelevationView || obj is MassHaulLine ||
            obj is MassHaulView || obj is Intersection || obj is ViewFrame || obj is ViewFrameGroup || obj is MatchLine || IsCantView(obj);

        private static bool IsCantView(DBObject obj) => obj.GetType().Name.IndexOf("CantView", StringComparison.OrdinalIgnoreCase) >= 0;

        internal static bool IsFeatureLineOrGradingObject(DBObject obj) =>
            obj is FeatureLine || obj is Grading || obj is Site || obj.GetType().Name.IndexOf("Grading", StringComparison.OrdinalIgnoreCase) >= 0;

        internal static bool IsParcelOrCatchmentObject(DBObject obj) =>
            obj is Parcel || obj is ParcelSegment || obj is Catchment || obj is CatchmentGroup;

        internal static bool IsAcadAnnotationObject(DBObject obj) =>
            obj is DBText || obj is MText || obj is Dimension || obj is Leader || obj is MLeader || obj is AcadTable ||
            obj is AttributeDefinition || obj is FeatureControlFrame;

        internal static bool IsBlockOrReferenceObject(DBObject obj) =>
            obj is BlockReference || obj is MInsertBlock || obj is RasterImage || obj is UnderlayReference || obj is Ole2Frame;

        private static bool IsXrefInsert(BlockReference br)
        {
            try { return br.BlockTableRecord.GetObject(OpenMode.ForRead) is BlockTableRecord btr && btr.IsFromExternalReference; }
            catch (RuntimeException) { return false; }
        }

        private static bool IsReference(CivilEntity e)
        {
            try { return e.IsReferenceObject; }
            catch (RuntimeException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        // ---------------- Erasing ----------------

        private static DBObject OpenForRead(Transaction tr, ObjectId id)
        {
            if (id.IsNull || id.IsErased) return null;
            try { return tr.GetObject(id, OpenMode.ForRead); }
            catch (RuntimeException) { return null; }
        }

        /// <summary>
        /// Erases one object, counting it, or records why it could not be (handle, type, reason). Only the errors AutoCAD and
        /// Civil 3D raise for an object they refuse are caught; anything else stops the cleanup, and a copy is then not saved.
        /// </summary>
        private static bool TryErase(Transaction tr, ObjectId id, CleanupReport report)
        {
            if (id.IsNull || id.IsErased || id.IsEffectivelyErased) return false;
            DBObject obj = null;
            try
            {
                obj = tr.GetObject(id, OpenMode.ForWrite, false, true);
                obj.Erase(true);
                report.Deleted++;
                return true;
            }
            catch (RuntimeException ex) { report.Fail(id, obj, ex.ErrorStatus.ToString()); }
            catch (InvalidOperationException ex) { report.Fail(id, obj, ex.Message); }
            catch (ArgumentException ex) { report.Fail(id, obj, ex.Message); }
            return false;
        }

        /// <summary>Erases every model space object <paramref name="match"/> accepts; physical network parts only when asked.</summary>
        private static void Sweep(Transaction tr, Database db, CleanupReport report, Func<DBObject, bool> match, bool includeNetworkParts = false)
        {
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
            foreach (ObjectId id in ms.Cast<ObjectId>().ToList())
            {
                DBObject obj = OpenForRead(tr, id);
                if (obj == null || obj.IsErased) continue;
                if (!includeNetworkParts && IsPhysicalPipeNetworkPart(obj)) continue;
                if (match(obj)) TryErase(tr, id, report);
            }
        }

        private static void EraseNetworks(Transaction tr, CivilDocument civil, CleanupOptions o, CleanupReport report)
        {
            foreach (ObjectId netId in civil.GetPipeNetworkIds())
            {
                if (!(OpenForRead(tr, netId) is Network net)) continue;
                // A network whose parts were all data references is empty after stripping them; it goes too.
                bool emptied = o.DataReferences == DataReferenceHandling.Strip && net.GetPipeIds().Count == 0 && net.GetStructureIds().Count == 0;
                if (!o.GravityNetworks.Contains(net.Name) && !emptied) continue;
                foreach (ObjectId id in net.GetPipeIds().Live()) TryErase(tr, id, report);
                foreach (ObjectId id in net.GetStructureIds().Live()) TryErase(tr, id, report);
                TryErase(tr, netId, report);
            }
            foreach (ObjectId netId in civil.GetPressurePipeNetworkIds())
            {
                if (!(OpenForRead(tr, netId) is PressurePipeNetwork net)) continue;
                if (!o.PressureNetworks.Contains(net.Name)) continue;
                foreach (ObjectId id in net.GetPipeIds().Live()) TryErase(tr, id, report);
                foreach (ObjectId id in net.GetFittingIds()) TryErase(tr, id, report);
                foreach (ObjectId id in net.GetAppurtenanceIds()) TryErase(tr, id, report);
                TryErase(tr, netId, report);
            }
        }

        // ---------------- Layers and layouts ----------------

        private static void UnlockAllLayers(Database db, CleanupReport report)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId layerId in lt)
                {
                    if (!(OpenForRead(tr, layerId) is LayerTableRecord ltr) || !(ltr.IsLocked || ltr.IsFrozen)) continue;
                    try
                    {
                        ltr.UpgradeOpen();
                        ltr.IsLocked = false;
                        if (db.Clayer != layerId) ltr.IsFrozen = false;
                    }
                    catch (RuntimeException ex) { report.Notes.Add($"Layer {ltr.Name} could not be unlocked / thawed: {ex.ErrorStatus}"); }
                }
                tr.Commit();
            }
        }

        /// <summary>
        /// Keeps the first layout by tab order, deletes the others, and empties the one kept of everything but its own
        /// paper-space viewport. Returns how many layouts were deleted.
        /// </summary>
        private static int CleanPaperSpaceLayouts(Database db, CleanupReport report)
        {
            LayoutManager lm = LayoutManager.Current;
            try { if (lm.CurrentLayout != "Model") lm.CurrentLayout = "Model"; }
            catch (RuntimeException ex) { report.Notes.Add("Could not switch to Model before removing layouts: " + ex.ErrorStatus); }

            List<(string name, int tab)> layouts = new List<(string, int)>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                DBDictionary dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                    if (tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout && !layout.ModelType) layouts.Add((layout.LayoutName, layout.TabOrder));
                tr.Commit();
            }
            layouts.Sort((a, b) => a.tab.CompareTo(b.tab));

            // AutoCAD needs one layout to remain: the first tab.
            int deleted = 0;
            string current = null;
            try { current = lm.CurrentLayout; } catch (RuntimeException) { }
            for (int i = 1; i < layouts.Count; i++)
            {
                if (layouts[i].name == current) { report.Notes.Add($"Layout {layouts[i].name} kept: it is the current layout."); continue; }
                try { lm.DeleteLayout(layouts[i].name); deleted++; }
                catch (RuntimeException ex) { report.Notes.Add($"Layout {layouts[i].name} could not be deleted: {ex.ErrorStatus}"); }
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBDictionary dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout) || layout.ModelType) continue;
                    BlockTableRecord sheet = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                    ObjectId overall = OverallViewport(tr, layout, sheet);
                    foreach (ObjectId id in sheet.Cast<ObjectId>().ToList())
                        if (id != overall) TryErase(tr, id, report);
                }
                tr.Commit();
            }
            return deleted;
        }

        /// <summary>
        /// The layout's own paper-space viewport, which must stay. Viewport.Number is only set on the active layout, so it
        /// cannot tell it apart elsewhere: it is the first in the layout's viewport list, or, for a layout never shown (an empty
        /// list), the first viewport on the sheet, as AutoCAD creates it before any other.
        /// </summary>
        private static ObjectId OverallViewport(Transaction tr, Layout layout, BlockTableRecord sheet)
        {
            try
            {
                ObjectIdCollection list = layout.GetViewports();
                if (list != null && list.Count > 0) return list[0];
            }
            catch (RuntimeException) { }
            foreach (ObjectId id in sheet)
                if (OpenForRead(tr, id) is AcadViewport) return id;
            return ObjectId.Null;
        }

        // ---------------- Xrefs ----------------

        private static void HandleXrefs(Database db, CleanupOptions o, CleanupReport report, string sourceFolder, string targetFolder)
        {
            List<ObjectId> xrefs = new List<ObjectId>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in bt)
                {
                    if (!(OpenForRead(tr, id) is BlockTableRecord btr) || !btr.IsFromExternalReference) continue;
                    xrefs.Add(id);
                    // Relative paths were relative to the drawing's folder; the copy is saved elsewhere, or bound from a temporary file.
                    if (sourceFolder != null && o.Xrefs != XrefHandling.DetachAll && RebasePath(btr, sourceFolder, o.Xrefs == XrefHandling.Bind ? null : targetFolder))
                        report.XrefsRebased++;
                }
                tr.Commit();
            }
            if (xrefs.Count == 0 || o.Xrefs == XrefHandling.Keep) return;

            if (o.Xrefs == XrefHandling.Bind)
            {
                try { db.ResolveXrefs(false, false); }
                catch (RuntimeException ex) { report.Notes.Add("Xrefs could not be resolved for binding: " + ex.ErrorStatus); }
                ObjectIdCollection resolved = new ObjectIdCollection();
                using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (ObjectId id in xrefs)
                    {
                        if (!(OpenForRead(tr, id) is BlockTableRecord btr)) continue;
                        if (btr.XrefStatus == XrefStatus.Resolved) resolved.Add(id);
                        else report.Notes.Add($"Xref {btr.Name} was not found ({btr.PathName}); it stays attached.");
                    }
                    tr.Commit();
                }
                if (resolved.Count == 0) return;
                try { db.BindXrefs(resolved, true); report.XrefsBound = resolved.Count; }
                catch (RuntimeException ex) { report.Notes.Add("Binding the xrefs failed: " + ex.ErrorStatus); }
                return;
            }

            foreach (ObjectId id in xrefs)
            {
                if (o.Xrefs == XrefHandling.DetachUnreferenced && Referenced(db, id)) continue;
                try { db.DetachXref(id); report.XrefsDetached++; }
                catch (RuntimeException ex) { report.Notes.Add($"An xref could not be detached: {ex.ErrorStatus}"); }
            }
        }

        private static bool Referenced(Database db, ObjectId xrefId)
        {
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                bool used = OpenForRead(tr, xrefId) is BlockTableRecord btr && btr.GetBlockReferenceIds(true, false).Count > 0;
                tr.Commit();
                return used;
            }
        }

        /// <summary>
        /// Makes a relative xref path right for the file's new folder: relative to <paramref name="targetFolder"/>, or absolute
        /// when that is null. Returns true when the path changed.
        /// </summary>
        private static bool RebasePath(BlockTableRecord btr, string sourceFolder, string targetFolder)
        {
            string path = btr.PathName ?? "";
            if (path.Length == 0 || Path.IsPathRooted(path)) return false;
            string absolute = Path.GetFullPath(Path.Combine(sourceFolder, path));
            string rebased = targetFolder == null ? absolute : Path.GetRelativePath(targetFolder, absolute);
            if (string.Equals(rebased, path, StringComparison.OrdinalIgnoreCase)) return false;
            btr.UpgradeOpen();
            btr.PathName = rebased;
            return true;
        }

        // ---------------- Purge ----------------

        private static int DeepPurgeDatabase(Database db, CleanupReport report)
        {
            int total = 0;
            var erased = new HashSet<ObjectId>();
            var byKind = new Dictionary<string, int>();
            for (int pass = 0; pass < 5; pass++)
            {
                int purged = 0;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ObjectIdCollection candidates = new ObjectIdCollection();
                    foreach (ObjectId table in new[] { db.BlockTableId, db.LayerTableId, db.LinetypeTableId, db.TextStyleTableId, db.DimStyleTableId, db.RegAppTableId })
                        if (OpenForRead(tr, table) is SymbolTable st) foreach (ObjectId id in st) candidates.Add(id);
                    foreach (ObjectId dictId in new[] { db.MLeaderStyleDictionaryId, db.TableStyleDictionaryId, db.MaterialDictionaryId, db.GroupDictionaryId, db.PlotStyleNameDictionaryId })
                        if (OpenForRead(tr, dictId) is DBDictionary dict) foreach (DBDictionaryEntry e in dict) candidates.Add(e.Value);

                    // Purge leaves in the collection only what nothing references.
                    if (candidates.Count > 0) db.Purge(candidates);
                    foreach (ObjectId id in candidates)
                    {
                        // One that refuses (layer 0, the current layer...) is simply kept: not a failure worth reporting.
                        // Counted once, when it really goes: the count was 979961 for 786 blocks on the tile 124 copy (2026-11-17).
                        if (id.IsErased || !erased.Add(id)) continue;
                        try
                        {
                            tr.GetObject(id, OpenMode.ForWrite).Erase(true);
                            purged++;
                            string kind = id.ObjectClass.DxfName;
                            byKind[kind] = byKind.TryGetValue(kind, out int k) ? k + 1 : 1;
                        }
                        catch (RuntimeException) { erased.Remove(id); }
                    }
                    tr.Commit();
                }
                total += purged;
                if (purged == 0) break;
            }
            if (byKind.Count > 0)
                report.Notes.Add("Purged: " + string.Join(", ", byKind.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}")));
            return total;
        }
    }
}
