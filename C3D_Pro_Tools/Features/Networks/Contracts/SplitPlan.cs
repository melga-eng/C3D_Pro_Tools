using System;
using System.Collections.Generic;

// The preview of a split: what would change, without changing anything, and what Apply needs to tell whether the drawing
// still matches it (stale-plan detection). Every list is frozen when set, so the plan Apply receives is the plan reviewed.
namespace C3DProTools.Features.Networks.Contracts
{
    /// <summary>
    /// A pipe as it was when the plan was made: the state the split depends on. Apply reads the pipe again and compares
    /// (<see cref="SplitValidation.Compare"/>); a pipe that moved, changed part (family or size), network or connections, or
    /// became curved or straight since the preview is not split, and the user is asked to preview again.
    /// </summary>
    /// <param name="Network">The network the pipe is in.</param>
    /// <param name="StartStructure">The structure at its start; null when that end is not connected.</param>
    /// <param name="EndStructure">The structure at its end; null when not connected (a straight split moves this connection to the last piece).</param>
    /// <param name="PartFamily">The part family in the drawing (the new pieces are made from it); null for a part without a family.</param>
    /// <param name="PartSize">The part size in the drawing; null when the part has none to name.</param>
    /// <param name="PartSizeName">The part size name, as the drawing shows it (compared too, for parts without family or size objects).</param>
    /// <param name="Bulge">The pipe's bulge from start to end in plan: 0 for a straight pipe, so a changed arc is noticed too.</param>
    internal sealed record PipeSnapshot(Point3 Start, Point3 End, DrawingObjectRef Network, DrawingObjectRef? StartStructure, DrawingObjectRef? EndStructure,
        DrawingObjectRef? PartFamily, DrawingObjectRef? PartSize, string PartSizeName, bool Curved, double Bulge);

    /// <summary>
    /// One pipe of the plan and where it is split. A straight pipe keeps its first piece and gets new pipes for the rest (same
    /// family, size, style and layer, on the same 3D line); its end connection moves to the last piece. A curved pipe is broken
    /// by Civil 3D at each point. With a structure, one joins the pieces at every point.
    /// </summary>
    /// <param name="Length">The pipe's plan length (drawing units), along its arc when curved.</param>
    /// <param name="Points">
    /// The split points, ordered from the pipe's start, each on the pipe (Z on its centreline), at least
    /// <see cref="SplitLimits.MinPieceLength"/> from either end and from its neighbours. Frozen when set.
    /// </param>
    /// <param name="Snapshot">The pipe as planned, compared again before it is split.</param>
    internal sealed record SplitItemPlan(DrawingObjectRef Pipe, string PipeName, bool Curved, double Length, IReadOnlyList<Point3> Points, PipeSnapshot Snapshot)
    {
        private readonly IReadOnlyList<Point3> _points = Frozen.List(Points);
        public IReadOnlyList<Point3> Points { get => _points; init => _points = Frozen.List(value); }

        /// <summary>How many pieces the pipe becomes.</summary>
        public int PieceCount => Points.Count + 1;
    }

    /// <summary>
    /// The plan made from a <see cref="SplitRequest"/>: which pipes would be split, where, and how. Making it changes nothing.
    /// <see cref="Status"/> is <see cref="OperationStatus.Succeeded"/> (a plan to apply), <see cref="OperationStatus.NoChange"/>
    /// (valid, but no pipe can be split that way, e.g. every pipe too short, or no picked point on a pipe),
    /// <see cref="OperationStatus.Rejected"/> (invalid request or wrong drawing; <see cref="Issues"/> say why), or
    /// <see cref="OperationStatus.Cancelled"/> (planning was stopped; nothing to apply). Only a succeeded plan can be applied.
    /// </summary>
    internal sealed record SplitPreview
    {
        private readonly IReadOnlyList<SplitItemPlan> _items = Frozen.List<SplitItemPlan>(null);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List<OperationIssue>(null);

        /// <summary>Names this plan in logs and results.</summary>
        public Guid PlanId { get; init; } = Guid.NewGuid();
        public DrawingIdentity Drawing { get; init; }
        /// <summary>The request the plan was made from; Apply uses its options (structure), never newer form values.</summary>
        public SplitRequest Request { get; init; }
        public OperationStatus Status { get; init; }
        public IReadOnlyList<SplitItemPlan> Items { get => _items; init => _items = Frozen.List(value); }
        /// <summary><see cref="SplitMode.AtPoints"/>: picked points not used (not on a pipe within the pick distance, too near an end or another point); they are ignored.</summary>
        public int MissedPicks { get; init; }
        /// <summary>Each pipe is split, or left as it was, on its own: one failing does not undo or stop the others.</summary>
        public Atomicity Atomicity { get; init; } = Atomicity.PerItem;
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }
    }

    /// <summary>
    /// Apply a preview. <see cref="Chosen"/> are the pipes the user kept ticked in the preview: each must be an item of
    /// <see cref="Preview"/>. Nothing chosen is no change. Apply splits exactly the frozen items of <see cref="Preview"/>; it
    /// never plans again.
    /// </summary>
    internal sealed record SplitApplyRequest(SplitPreview Preview, IReadOnlyList<DrawingObjectRef> Chosen)
    {
        private readonly IReadOnlyList<DrawingObjectRef> _chosen = Frozen.List(Chosen);
        public IReadOnlyList<DrawingObjectRef> Chosen { get => _chosen; init => _chosen = Frozen.List(value); }
    }

    /// <summary>Progress while applying: <paramref name="Completed"/> of <paramref name="Total"/> pipes done; <paramref name="Current"/> is the one being split next.</summary>
    internal sealed record SplitProgress(int Completed, int Total, string Current);
}
