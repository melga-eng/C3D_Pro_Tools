using System;
using System.Collections.Generic;
using System.Linq;

// The split's validation and stale-plan rules, on plain values so they are tested without Civil 3D. The application layer
// calls them; they are the authoritative checks (the form's own are quick feedback only).
namespace C3DProTools.Features.Networks.Contracts
{
    /// <summary>Stable issue codes of the split (tests and logs match on these, never on message text).</summary>
    internal static class SplitIssueCodes
    {
        public const string DrawingMissing = "split.drawing.missing";
        public const string DrawingMismatch = "split.drawing.mismatch";
        public const string ModeUnknown = "split.mode.unknown";
        public const string PipesNone = "split.pipes.none";
        public const string PipeInvalid = "split.pipes.invalid";
        public const string PipeDuplicate = "split.pipes.duplicate";
        public const string PicksNone = "split.picks.none";
        public const string PickNotFinite = "split.picks.notFinite";
        public const string PickToleranceRange = "split.pickTolerance.range";
        public const string PartsRange = "split.parts.range";
        public const string EveryRange = "split.every.range";
        public const string StructureIncomplete = "split.structure.incomplete";
        /// <summary>The structure is only a template to copy (its family is in no parts list); the split does not copy structures yet.</summary>
        public const string StructureTemplateOnly = "split.structure.templateOnly";
        /// <summary>Preview: picked points not on any pipe (ignored).</summary>
        public const string PicksMissed = "split.picks.missed";
        /// <summary>Preview: no pipe can be split as asked (too short, or no point on a pipe).</summary>
        public const string NothingToSplit = "split.plan.nothing";
        /// <summary>Preview: a straight pipe without a part family (its new pieces cannot be made); it is left out.</summary>
        public const string PipeWithoutFamily = "split.pipe.noFamily";
        /// <summary>Preview: a pipe would get more than <see cref="SplitLimits.MaxPiecesPerPipe"/> pieces; it is left out.</summary>
        public const string TooManyPieces = "split.plan.tooManyPieces";
        /// <summary>Preview: a plan item's points are not valid (not finite, unordered, or leaving a piece shorter than <see cref="SplitLimits.MinPieceLength"/>).</summary>
        public const string ItemPointsInvalid = "split.plan.itemPoints";
        /// <summary>Apply: the preview is not one that can be applied (rejected, no change, or empty).</summary>
        public const string PlanNotApplicable = "split.plan.notApplicable";
        public const string ChosenNotInPlan = "split.chosen.notInPlan";
        public const string ChosenDuplicate = "split.chosen.duplicate";
        /// <summary>Apply: the pipe is gone (erased, or its handle no longer names a pipe).</summary>
        public const string PipeMissing = "split.stale.missing";
        /// <summary>Apply: the pipe changed since the preview (moved, other part family or size, reconnected, other network or curve).</summary>
        public const string PipeChanged = "split.stale.changed";
        /// <summary>Apply: the pipe could not be split; its changes were undone.</summary>
        public const string ItemFailed = "split.item.failed";
        /// <summary>Apply: a piece could not be connected (to the end structure or a split structure); the pipe's changes were undone.</summary>
        public const string ItemConnectionFailed = "split.item.connection";
        /// <summary>Apply: the structure could not be put in this network's parts list; the pipe is split without structures.</summary>
        public const string StructureUnavailable = "split.structure.unavailable";
        /// <summary>Apply: a parts list was changed on the way (family or size added, or a network switched to the chosen list).</summary>
        public const string PartsListChanged = "split.partsList.changed";
        public const string Cancelled = "split.cancelled";
        /// <summary>Apply: the run's final state is not known (an unexpected error, a failed commit, or an unconfirmed undo); read the drawing again.</summary>
        public const string OutcomeUnknown = "split.outcomeUnknown";
        /// <summary>Apply: the split was saved, but releasing the drawing (lock or transaction) reported a problem.</summary>
        public const string SessionNotClosed = "split.session.notClosed";
    }

    internal static class SplitValidation
    {
        /// <summary>A point counts as unmoved within this distance (drawing units), and a bulge as unchanged within <see cref="BulgeTolerance"/>.</summary>
        public const double PositionTolerance = 1e-6, BulgeTolerance = 1e-9;

        /// <summary>
        /// The problems of a request, empty when it can be planned. Checks the drawing is named, the mode is known, and the
        /// chosen mode's fields: pipes (valid, not repeated; needed except for picked points), picks (at least one, finite),
        /// pick distance (finite, above 0, at most <see cref="SplitLimits.MaxPickTolerance"/>), parts
        /// (<see cref="SplitLimits.MinParts"/> to <see cref="SplitLimits.MaxParts"/>), fixed length (finite, at least
        /// <see cref="SplitLimits.MinPieceLength"/>), and that a chosen structure names its parts list, family and size. All
        /// lengths in drawing units; messages name the drawing's unit.
        /// </summary>
        public static List<OperationIssue> Validate(SplitRequest request)
        {
            List<OperationIssue> issues = new List<OperationIssue>();
            if (request == null) { issues.Add(OperationIssue.Error(SplitIssueCodes.DrawingMissing, "There is no split to plan.")); return issues; }
            if (request.Drawing == null || !request.Drawing.IsComplete)
                issues.Add(OperationIssue.Error(SplitIssueCodes.DrawingMissing, "The drawing to split pipes in is not known. Open the drawing and try again.", nameof(SplitRequest.Drawing)));
            string unit = request.Drawing?.Unit ?? "drawing units";
            if (!Enum.IsDefined(typeof(SplitMode), request.Mode))
            {
                issues.Add(OperationIssue.Error(SplitIssueCodes.ModeUnknown, "Choose how to split: at points, into equal parts, or every fixed length.", nameof(SplitRequest.Mode)));
                return issues;
            }

            IReadOnlyList<DrawingObjectRef> pipes = request.Pipes ?? Array.Empty<DrawingObjectRef>();
            if (pipes.Any(x => !x.IsValid))
                issues.Add(OperationIssue.Error(SplitIssueCodes.PipeInvalid, "A chosen pipe has no handle. Choose the pipes again.", nameof(SplitRequest.Pipes)));
            foreach (DrawingObjectRef dup in pipes.Where(x => x.IsValid).GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key))
                issues.Add(OperationIssue.Error(SplitIssueCodes.PipeDuplicate, $"Pipe {dup.Handle} is chosen more than once.", nameof(SplitRequest.Pipes), dup));
            if (request.Mode != SplitMode.AtPoints && pipes.Count == 0)
                issues.Add(OperationIssue.Error(SplitIssueCodes.PipesNone, "Choose the gravity pipes to split.", nameof(SplitRequest.Pipes)));

            switch (request.Mode)
            {
                case SplitMode.AtPoints:
                    IReadOnlyList<Point3> picks = request.Picks ?? Array.Empty<Point3>();
                    if (picks.Count == 0) issues.Add(OperationIssue.Error(SplitIssueCodes.PicksNone, "Pick at least one point on a pipe.", nameof(SplitRequest.Picks)));
                    else if (picks.Any(p => !p.IsFinite)) issues.Add(OperationIssue.Error(SplitIssueCodes.PickNotFinite, "A picked point is not a valid location. Pick the points again.", nameof(SplitRequest.Picks)));
                    if (!double.IsFinite(request.PickTolerance) || request.PickTolerance <= 0 || request.PickTolerance > SplitLimits.MaxPickTolerance)
                        issues.Add(OperationIssue.Error(SplitIssueCodes.PickToleranceRange, $"Enter a pick distance above 0 and at most {SplitLimits.MaxPickTolerance:0} {unit}.", nameof(SplitRequest.PickTolerance)));
                    break;
                case SplitMode.EqualParts:
                    if (request.Parts < SplitLimits.MinParts || request.Parts > SplitLimits.MaxParts)
                        issues.Add(OperationIssue.Error(SplitIssueCodes.PartsRange, $"Enter {SplitLimits.MinParts} to {SplitLimits.MaxParts} parts.", nameof(SplitRequest.Parts)));
                    break;
                case SplitMode.FixedLength:
                    if (!double.IsFinite(request.Every) || request.Every < SplitLimits.MinPieceLength)
                        issues.Add(OperationIssue.Error(SplitIssueCodes.EveryRange, $"Enter a length of at least {SplitLimits.MinPieceLength} {unit}.", nameof(SplitRequest.Every)));
                    break;
            }

            SplitStructureChoice st = request.Structure;
            if (st != null && (!st.IsComplete || st.Dimensions.Values.Any(v => !double.IsFinite(v))))
                issues.Add(OperationIssue.Error(SplitIssueCodes.StructureIncomplete, "Choose the structure for the split points (parts list, family, size).", nameof(SplitRequest.Structure)));
            // Copying a structure from the drawing (no parts list part) is not done by the split yet: refused, rather than
            // splitting without the structures asked for.
            else if (st != null && !st.PartsList.IsValid)
                issues.Add(OperationIssue.Error(SplitIssueCodes.StructureTemplateOnly, "The split cannot copy a structure from the drawing yet. Choose a parts list structure (parts list, family, size) for the split points.", nameof(SplitRequest.Structure)));
            return issues;
        }

        /// <summary>
        /// The problems of one planned pipe, null when it is valid: its points are finite, ordered from the start along the
        /// pipe, at least <see cref="SplitLimits.MinPieceLength"/> from either end and from each other (no piece shorter), and give at most
        /// <see cref="SplitLimits.MaxPiecesPerPipe"/> pieces. Positions along the pipe are given by <paramref name="along"/>
        /// (plan distance from the start, from the adapter's measure of the pipe, curved or straight).
        /// </summary>
        public static OperationIssue CheckItem(SplitItemPlan item, Func<Point3, double> along)
        {
            if (item.PieceCount > SplitLimits.MaxPiecesPerPipe)
                return OperationIssue.Warning(SplitIssueCodes.TooManyPieces, $"{item.PipeName} would be split into {item.PieceCount} pieces (at most {SplitLimits.MaxPiecesPerPipe}); it is left out. Use a longer length.", nameof(SplitRequest.Every), item.Pipe);
            // Every piece, from the start to the first point, between points, and from the last point to the end, is at least the shortest piece.
            double last = 0;
            foreach (Point3 p in item.Points)
            {
                if (!p.IsFinite) return Bad();
                double d = along(p);
                if (!double.IsFinite(d) || d - last < SplitLimits.MinPieceLength - PositionTolerance) return Bad();
                last = d;
            }
            if (item.Points.Count > 0 && item.Length - last < SplitLimits.MinPieceLength - PositionTolerance) return Bad();
            return null;

            OperationIssue Bad() => OperationIssue.Error(SplitIssueCodes.ItemPointsInvalid, $"The split points planned for {item.PipeName} are not valid. Preview again.", null, item.Pipe);
        }

        /// <summary>
        /// The problems of an apply request as a whole, empty when it can go ahead: the preview must be an applicable plan
        /// (<see cref="OperationStatus.Succeeded"/> with items), for the drawing that is open now (<paramref name="current"/>),
        /// and every chosen pipe an item of it, chosen once.
        /// </summary>
        public static List<OperationIssue> ValidateApply(SplitApplyRequest request, DrawingIdentity current)
        {
            List<OperationIssue> issues = new List<OperationIssue>();
            SplitPreview plan = request?.Preview;
            if (plan == null || plan.Status != OperationStatus.Succeeded || plan.Items == null || plan.Items.Count == 0)
            {
                issues.Add(OperationIssue.Error(SplitIssueCodes.PlanNotApplicable, "There is no split plan to apply. Preview the split first."));
                return issues;
            }
            if (plan.Drawing == null || !plan.Drawing.Matches(current))
                issues.Add(OperationIssue.Error(SplitIssueCodes.DrawingMismatch, "The plan was made for another drawing (or before this drawing was reopened). Preview the split again in this drawing."));
            HashSet<DrawingObjectRef> planned = new HashSet<DrawingObjectRef>(plan.Items.Select(i => i.Pipe));
            IReadOnlyList<DrawingObjectRef> chosen = request.Chosen ?? Array.Empty<DrawingObjectRef>();
            foreach (DrawingObjectRef stray in chosen.Where(x => !planned.Contains(x)).Distinct())
                issues.Add(OperationIssue.Error(SplitIssueCodes.ChosenNotInPlan, $"Pipe {stray.Handle} is not in the plan. Preview the split again.", null, stray));
            foreach (DrawingObjectRef dup in chosen.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key))
                issues.Add(OperationIssue.Error(SplitIssueCodes.ChosenDuplicate, $"Pipe {dup.Handle} is chosen more than once.", null, dup));
            return issues;
        }

        /// <summary>
        /// Whether a pipe still matches its plan: null when it does; otherwise why it is stale. <paramref name="current"/> is
        /// the pipe read again just before splitting it (null when it is gone). Points and bulge are compared within
        /// <see cref="PositionTolerance"/> and <see cref="BulgeTolerance"/>; part family and size by their drawing objects, and
        /// the size name ignoring case.
        /// </summary>
        public static OperationIssue Compare(SplitItemPlan item, PipeSnapshot current)
        {
            PipeSnapshot planned = item.Snapshot;
            if (current == null)
                return OperationIssue.Error(SplitIssueCodes.PipeMissing, $"{item.PipeName} is no longer in the drawing; it was not split.", null, item.Pipe);
            List<string> what = new List<string>();
            if (planned.Start.DistanceTo(current.Start) > PositionTolerance || planned.End.DistanceTo(current.End) > PositionTolerance) what.Add("moved");
            // A bulge that could not be read (NaN) matches only another unread bulge; NaN never slips through as "unchanged".
            bool sameBulge = double.IsNaN(planned.Bulge) || double.IsNaN(current.Bulge)
                ? double.IsNaN(planned.Bulge) && double.IsNaN(current.Bulge)
                : Math.Abs(planned.Bulge - current.Bulge) <= BulgeTolerance;
            if (planned.Curved != current.Curved || !sameBulge) what.Add("its curve changed");
            if (planned.PartFamily != current.PartFamily) what.Add("its part family changed");
            if (planned.PartSize != current.PartSize || !string.Equals(planned.PartSizeName ?? "", current.PartSizeName ?? "", StringComparison.OrdinalIgnoreCase)) what.Add("its size changed");
            if (planned.Network != current.Network) what.Add("it moved to another network");
            if (planned.StartStructure != current.StartStructure || planned.EndStructure != current.EndStructure) what.Add("its connections changed");
            return what.Count == 0 ? null
                : OperationIssue.Error(SplitIssueCodes.PipeChanged, $"{item.PipeName} changed since the preview ({string.Join(", ", what)}); it was not split. Preview again.", null, item.Pipe);
        }
    }
}
