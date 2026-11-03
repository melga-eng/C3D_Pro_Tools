using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using System;
using System.Collections.Generic;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;
using Entity = Autodesk.Civil.DatabaseServices.Entity;
using Polyline = Autodesk.AutoCAD.DatabaseServices.Polyline;

namespace C3DProTools
{
    internal sealed class PartAddResult
    {
        public int Added, AlreadyShown, Failed, Labels, LabelsFailed;
    }

    internal sealed class RunProfileResult
    {
        public string AlignmentName;
        public ObjectId AlignmentId, ViewId;
        public List<ObjectId> Profiles;
        public ProfileSetup Setup;
        public readonly PartAddResult Parts = new PartAddResult();
    }

    /// <summary>The Civil 3D creation steps of the "Create alignment from parts" mode. Each runs in the caller's transaction.</summary>
    internal static class ProfileCreation
    {
        /// <summary>The creation step in progress, named in the error message when a step fails.</summary>
        public static string Step;

        /// <summary>
        /// Asks for the styles / labels and the view's insertion point, then creates the alignment along the run, its surface
        /// profiles and profile view, and draws + labels <paramref name="drawIds"/> in it. Returns null when the user cancels.
        /// Shared by Parts to Profile (create mode) and the Cover Optimizer.
        /// </summary>
        public static RunProfileResult CreateForRun(PipeRunBuilder builder, List<RunStep> steps, PipeRun run, List<ObjectId> drawIds,
            PartsToProfileSettings settings, Editor ed)
        {
            Autodesk.AutoCAD.ApplicationServices.Document acDoc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            Database db = acDoc.Database;
            CivilDocument civilDoc = CivilApplication.ActiveDocument;

            ProfileSetup setup;
            Step = "reading the drawing's styles for the setup window";
            using (FormProfileSetup setupForm = new FormProfileSetup("ALI-" + builder.Pipes[steps[0].Pipe].NetworkName, settings))
            {
                if (setupForm.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
                setup = setupForm.Result;
            }

            ViewHelper.ZoomTo(RunExtents(builder, run), 2.5, 50.0);
            PromptPointResult ppr = ed.GetPoint("\nPick the insertion point (lower-left corner) of the profile view: ");
            if (ppr.Status != PromptStatus.OK) return null;

            RunProfileResult result = new RunProfileResult { Setup = setup };
            using (acDoc.LockDocument())
            {
                ObjectId polylineId;
                Step = "drawing the run's polyline";
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    polylineId = AddRunPolyline(tr, db, run, setup.LayerId);
                    tr.Commit();
                }
                Step = "creating the alignment from the polyline";
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    result.AlignmentId = CreateAlignment(tr, civilDoc, polylineId, setup, out result.AlignmentName);
                    tr.Commit();
                }
                Step = "creating the surface profiles and the profile view";
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    result.Profiles = CreateSurfaceProfiles(result.AlignmentId, result.AlignmentName, setup, ed);
                    result.ViewId = ProfileView.Create(result.AlignmentId, ppr.Value, result.AlignmentName, setup.BandSetId, setup.ProfileViewStyleId);
                    tr.Commit();
                }
                Step = "setting the view's elevation range and bands";
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ProfileView view = (ProfileView)tr.GetObject(result.ViewId, OpenMode.ForWrite);
                    GetElevationRange(tr, builder, steps, drawIds, result.Profiles, out double lowest, out double highest);
                    try { SetElevationRange(view, lowest, highest, setup.ElevationMargin); }
                    catch (Exception ex) { ed.WriteMessage($"\n[Warning] Could not set the profile view elevation range: {ex.Message}"); }
                    try { AssignBandProfiles(view, result.Profiles); }
                    catch (Exception ex) { ed.WriteMessage($"\n[Warning] Could not assign profiles to the band set: {ex.Message}"); }
                    tr.Commit();
                }

                Step = "drawing and labelling the parts in the profile view";
                AddAndLabel(db, drawIds, result.ViewId, setup, result.Parts, ed);
            }
            return result;
        }

        public static void AddAndLabel(Database db, List<ObjectId> partIds, ObjectId viewId, ProfileSetup setup, PartAddResult result, Editor ed)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                AddParts(tr, partIds, viewId, result, ed);
                tr.Commit();
            }
            // Labels go in their own transaction, once the parts' profile-view representations exist.
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                AddLabels(tr, partIds, viewId, setup, result, ed);
                tr.Commit();
            }
        }

        private static void GetElevationRange(Transaction tr, PipeRunBuilder builder, List<RunStep> steps, List<ObjectId> drawIds,
            List<ObjectId> profileIds, out double lowest, out double highest)
        {
            lowest = double.MaxValue;
            highest = double.MinValue;
            foreach (RunStep s in steps)
            {
                RunPipe p = builder.Pipes[s.Pipe];
                lowest = Math.Min(lowest, Math.Min(p.StartInvert, p.EndInvert));
                // Crown: the centreline is as far above the invert as the crown is above the centreline.
                highest = Math.Max(highest, Math.Max(2 * p.Start.Z - p.StartInvert, 2 * p.End.Z - p.EndInvert));
            }
            foreach (ObjectId id in drawIds)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is Structure st) lowest = Math.Min(lowest, st.SumpElevation);
            }
            foreach (ObjectId profileId in profileIds)
            {
                if (tr.GetObject(profileId, OpenMode.ForRead) is Profile profile)
                {
                    try { highest = Math.Max(highest, profile.ElevationMax); lowest = Math.Min(lowest, profile.ElevationMin); }
                    catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
                }
            }
        }

        public static Extents3d RunExtents(PipeRunBuilder builder, PipeRun run)
        {
            Extents3d ext = new Extents3d(run.Vertices[0].Point, run.Vertices[0].Point);
            foreach (RunVertex v in run.Vertices) ext.AddPoint(v.Point);
            foreach (int p in run.UnusedPipes) foreach (Point3d pt in builder.Pipes[p].Path) ext.AddPoint(pt);
            return ext;
        }

        /// <summary>Draws the run as a polyline in model space; Civil 3D turns it into the alignment (and erases it).</summary>
        public static ObjectId AddRunPolyline(Transaction tr, Database db, PipeRun run, ObjectId layerId)
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            Polyline pl = PipeRunBuilder.ToPolyline(run);
            pl.LayerId = layerId;
            ObjectId id = ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            return id;
        }

        public static ObjectId CreateAlignment(Transaction tr, CivilDocument doc, ObjectId polylineId, ProfileSetup s, out string name)
        {
            name = UniqueAlignmentName(tr, doc, s.AlignmentName);
            PolylineOptions options = new PolylineOptions { PlineId = polylineId, AddCurvesBetweenTangents = false, EraseExistingEntities = true };
            ObjectId alignmentId = Alignment.Create(doc, options, name, ObjectId.Null, s.LayerId, s.AlignmentStyleId, s.AlignmentLabelSetId);
            Alignment alignment = (Alignment)tr.GetObject(alignmentId, OpenMode.ForWrite);
            alignment.ReferencePointStation = s.StartStation;
            return alignmentId;
        }

        private static string UniqueAlignmentName(Transaction tr, CivilDocument doc, string baseName)
        {
            HashSet<string> existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ObjectId> ids = new List<ObjectId>();
            foreach (ObjectId id in doc.GetAlignmentIds()) ids.Add(id);
            foreach (ObjectId siteId in doc.GetSiteIds())
            {
                if (siteId.IsErased || !siteId.IsValid) continue;
                try
                {
                    if (tr.GetObject(siteId, OpenMode.ForRead) is Site site)
                        foreach (ObjectId id in site.GetAlignmentIds()) ids.Add(id);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception) { }
            }
            foreach (ObjectId id in ids)
            {
                // The lists can hold alignments erased in this session; opening those throws ePermanentlyErased.
                if (id.IsNull || id.IsErased || !id.IsValid) continue;
                try { if (tr.GetObject(id, OpenMode.ForRead) is Alignment a) existing.Add(a.Name); }
                catch (Autodesk.AutoCAD.Runtime.Exception) { }
            }
            string name = baseName;
            for (int i = 2; existing.Contains(name); i++) name = $"{baseName} ({i})";
            return name;
        }

        public static List<ObjectId> CreateSurfaceProfiles(ObjectId alignmentId, string alignmentName, ProfileSetup s, Editor ed)
        {
            List<ObjectId> profiles = new List<ObjectId>();
            foreach (KeyValuePair<string, ObjectId> surface in s.Surfaces)
            {
                try
                {
                    profiles.Add(Profile.CreateFromSurface($"{alignmentName} - {surface.Key}", alignmentId, surface.Value,
                        s.LayerId, s.ProfileStyleId, s.ProfileLabelSetId));
                }
                catch (Exception ex)
                {
                    ed.WriteMessage($"\n[Error] Surface profile from <{surface.Key}>: {ex.Message}");
                }
            }
            return profiles;
        }

        /// <summary>
        /// Fixes the view's elevation range to cover the parts as well as the surfaces: an automatic range follows only
        /// the profiles, so pipes deeper than the ground would fall below the view. Rounded out to whole meters.
        /// </summary>
        /// <summary>The view's elevation range: the levels given plus the margin, rounded outward to whole <paramref name="increment"/>s (1, 2, 5 m...).</summary>
        public static void SetElevationRange(ProfileView view, double lowest, double highest, double margin, double increment = 1.0)
        {
            (double min, double max) = ProfileViewMath.DatumRange(lowest, highest, margin, increment);

            view.ElevationRangeMode = ElevationRangeType.UserSpecified;
            // Order matters: the view rejects a minimum above its current maximum (and vice versa).
            if (min < view.ElevationMax) { view.ElevationMin = min; view.ElevationMax = max; }
            else { view.ElevationMax = max; view.ElevationMin = min; }
        }

        /// <summary>Points the band set's profile bands at the first (and second, if any) surface profile.</summary>
        public static void AssignBandProfiles(ProfileView view, List<ObjectId> profiles)
        {
            if (profiles.Count == 0) return;
            ObjectId first = profiles[0];
            ObjectId second = profiles.Count > 1 ? profiles[1] : profiles[0];

            ProfileViewBandItemCollection bottom = view.Bands.GetBottomBandItems();
            foreach (ProfileViewBandItem item in bottom) TrySetProfiles(item, first, second);
            view.Bands.SetBottomBandItems(bottom);

            ProfileViewBandItemCollection top = view.Bands.GetTopBandItems();
            foreach (ProfileViewBandItem item in top) TrySetProfiles(item, first, second);
            view.Bands.SetTopBandItems(top);
        }

        private static void TrySetProfiles(ProfileViewBandItem item, ObjectId first, ObjectId second)
        {
            // Bands that don't use profiles (e.g. horizontal geometry) may refuse; they are simply left as they are.
            try { item.Profile1Id = first; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
            try { item.Profile2Id = second; } catch (Exception probe) when (C3DProLogger.IsProbeFailure(probe)) { }
        }

        public static void AddParts(Transaction tr, IEnumerable<ObjectId> partIds, ObjectId viewId, PartAddResult result, Editor ed)
        {
            foreach (ObjectId id in partIds)
            {
                try
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForWrite);
                    ObjectIdCollection shownIn = obj is Part p ? p.GetProfileViewsDisplayingMe() : obj is PressurePart pp ? pp.GetProfileViewsDisplayingMe() : null;
                    if (shownIn == null) continue;
                    if (shownIn.Contains(viewId)) { result.AlreadyShown++; continue; }

                    if (obj is Part part) part.AddToProfileView(viewId);
                    else ((PressurePart)obj).AddToProfileView(viewId);
                    result.Added++;
                }
                catch (Exception ex)
                {
                    ed.WriteMessage($"\n[Error] Adding part to the profile view: {ex.Message}");
                    result.Failed++;
                }
            }
        }

        /// <summary>
        /// Labels each part in the view with the styles chosen for its type. Gravity pipe and structure labels use Civil 3D's
        /// default attachment point; pressure labels have no such default in the API and sit at the part's middle.
        /// </summary>
        public static void AddLabels(Transaction tr, IEnumerable<ObjectId> partIds, ObjectId viewId, ProfileSetup s, PartAddResult result, Editor ed)
        {
            foreach (ObjectId id in partIds)
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                ObjectId viewPartId = ViewPartId(tr, obj);

                List<Func<ObjectId>> creators = new List<Func<ObjectId>>();
                if (obj is Pipe)
                {
                    foreach (ObjectId style in s.PipeLabels)
                        creators.Add(() => Restyle(tr, PipeProfileLabel.Create(viewPartId, viewId), style));
                }
                else if (obj is Structure)
                {
                    if (!s.StructureLabel.IsNull) creators.Add(() => Restyle(tr, StructureProfileLabel.Create(viewId, viewPartId), s.StructureLabel));
                }
                else if (obj is PressurePipe)
                {
                    foreach (ObjectId style in s.PressurePipeLabels)
                        creators.Add(() => PressurePipeProfileLabel.Create(viewPartId, viewId, 0.5, style));
                }
                else if (obj is PressureFitting)
                {
                    if (!s.FittingLabel.IsNull) creators.Add(() => PressureFittingProfileLabel.Create(viewPartId, viewId, 0.5, Vector3d.XAxis, s.FittingLabel));
                }
                else if (obj is PressureAppurtenance)
                {
                    if (!s.AppurtenanceLabel.IsNull) creators.Add(() => PressureAppurtenanceProfileLabel.Create(viewPartId, viewId, 0.5, Vector3d.XAxis, s.AppurtenanceLabel));
                }

                foreach (Func<ObjectId> create in creators)
                {
                    try
                    {
                        create();
                        result.Labels++;
                    }
                    catch (Exception ex)
                    {
                        ed.WriteMessage($"\n[Error] Labelling a part in the profile view: {ex.Message}");
                        result.LabelsFailed++;
                    }
                }
            }
        }

        /// <summary>The part's representation in a profile view, confirmed to belong to it; otherwise the part itself.</summary>
        private static ObjectId ViewPartId(Transaction tr, DBObject obj)
        {
            ObjectId viewPartId = obj is Part p ? p.ProfileViewPartId : obj is PressurePart pp ? pp.ProfileViewPartId : ObjectId.Null;
            if (!viewPartId.IsNull && tr.GetObject(viewPartId, OpenMode.ForRead) is ProfileViewPart vp && vp.ModelPartId == obj.ObjectId)
                return viewPartId;
            return obj.ObjectId;
        }

        private static ObjectId Restyle(Transaction tr, ObjectId labelId, ObjectId styleId)
        {
            if (tr.GetObject(labelId, OpenMode.ForWrite) is Entity label) label.StyleId = styleId;
            return labelId;
        }
    }
}
