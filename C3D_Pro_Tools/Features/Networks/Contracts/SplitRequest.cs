using System.Collections.Generic;

// What the user asks the split to do. Built by the form (or another caller) from its fields and picks; validated by
// SplitValidation in the application layer, which is authoritative (the form's own checks are only quick feedback).
namespace C3DProTools.Features.Networks.Contracts
{
    /// <summary>Where the pipes are split. The values match the Network Editor's "Split" list (SplitMode setting).</summary>
    internal enum SplitMode
    {
        /// <summary>At points picked on the pipes: each point splits the nearest pipe within <see cref="SplitRequest.PickTolerance"/>.</summary>
        AtPoints = 0,
        /// <summary>Into <see cref="SplitRequest.Parts"/> pieces of equal plan length.</summary>
        EqualParts = 1,
        /// <summary>Every <see cref="SplitRequest.Every"/> along the pipe from its start; the last piece takes the rest.</summary>
        FixedLength = 2
    }

    /// <summary>
    /// Limits of the split. <b>Units:</b> every length in the split contracts is in <b>drawing units</b>, as Network Editor
    /// works today (no conversion; in a metric drawing they are metres). <see cref="DrawingIdentity.UnitLabel"/> names the
    /// unit for messages. The split measures in plan (X, Y) along the pipe, curved pipes along their arc.
    /// </summary>
    internal static class SplitLimits
    {
        /// <summary>
        /// No piece is shorter than this (drawing units): every split point is at least this far from both pipe ends and from
        /// its neighbouring split points. Equals NetworkEditOps.MinPipeLength. Today's split applies it at the pipe ends only;
        /// applying it between split points too is a deliberate safeguard (two picks closer than this on one pipe: the second
        /// is not used and counts as missed).
        /// </summary>
        public const double MinPieceLength = 0.05;
        /// <summary>The form's own range for equal parts (same as today).</summary>
        public const int MinParts = 2, MaxParts = 100;
        /// <summary>The largest pick distance accepted (drawing units). A deliberate safeguard: today any positive distance is accepted.</summary>
        public const double MaxPickTolerance = 100.0;
        /// <summary>The most pieces one pipe may be split into. A deliberate safeguard (a tiny fixed length on a long pipe): today there is no limit.</summary>
        public const int MaxPiecesPerPipe = 1000;
    }

    /// <summary>
    /// The structure placed at every split point, joining the two pieces there. The adapter finds it in the drawing's parts
    /// list (adding the family or size to the network's own list, or switching the network to this list, as Network Editor
    /// does today); when it cannot be put in a network's list, that network's pipes are split without structures, with a warning.
    /// </summary>
    /// <param name="PartsList">The parts list the structure was chosen from.</param>
    /// <param name="FamilyGuid">The catalog family's GUID; empty for a family without one (then <paramref name="FamilyName"/> is used).</param>
    /// <param name="Template">A structure in the drawing to copy when the part cannot be built (no family or catalog); null for none.</param>
    /// <param name="SwitchNetwork">True: a network on another parts list is switched to <paramref name="PartsList"/>; false: the part is added to the network's own list.</param>
    internal sealed record SplitStructureChoice(DrawingObjectRef PartsList, string FamilyGuid, string FamilyName, string SizeName,
        DrawingObjectRef? Template = null, bool SwitchNetwork = false)
    {
        /// <summary>As the form shows it: "family · size".</summary>
        public string Label => $"{FamilyName} · {SizeName}";
    }

    /// <summary>
    /// A request to plan a split (no edit is made until a plan from it is applied). Every object reference means an object in
    /// <see cref="Drawing"/>; every length is in its drawing units. Only the fields of the chosen <see cref="Mode"/> are read.
    /// Lists are copied when set (<see cref="Frozen"/>).
    /// </summary>
    internal sealed record SplitRequest
    {
        private readonly IReadOnlyList<DrawingObjectRef> _pipes = Frozen.List<DrawingObjectRef>(null);
        private readonly IReadOnlyList<Point3> _picks = Frozen.List<Point3>(null);

        public DrawingIdentity Drawing { get; init; }
        public SplitMode Mode { get; init; }

        /// <summary>
        /// The gravity pipes to split. Needed for <see cref="SplitMode.EqualParts"/> and <see cref="SplitMode.FixedLength"/>.
        /// For <see cref="SplitMode.AtPoints"/> they are the pipes a point may split; empty means every gravity pipe in the drawing.
        /// </summary>
        public IReadOnlyList<DrawingObjectRef> Pipes { get => _pipes; init => _pipes = Frozen.List(value); }

        /// <summary><see cref="SplitMode.AtPoints"/>: the picked points, in pick order (world coordinates, drawing units).</summary>
        public IReadOnlyList<Point3> Picks { get => _picks; init => _picks = Frozen.List(value); }

        /// <summary><see cref="SplitMode.AtPoints"/>: a point splits the nearest pipe lying within this plan distance of it (drawing units). Default 1.</summary>
        public double PickTolerance { get; init; } = 1.0;

        /// <summary><see cref="SplitMode.EqualParts"/>: how many pieces each pipe becomes (2 to 100). Default 2.</summary>
        public int Parts { get; init; } = 2;

        /// <summary><see cref="SplitMode.FixedLength"/>: the plan length of each piece from the pipe's start (drawing units). Default 20.</summary>
        public double Every { get; init; } = 20.0;

        /// <summary>The structure at every split point; null leaves the pieces touching, without structures.</summary>
        public SplitStructureChoice Structure { get; init; }
    }
}
