using System.Collections.Generic;

// DIM stations (docs/sheet-production-contract.md, section 7): the vertical cover and clearance DIMs of one profile view, or of
// one piece of a split view. True metres (stations and elevations); the adapter applies the view's vertical exaggeration. The
// engine is DimStations.Plan.
namespace C3DProTools.Features.Sheets.Contracts
{
    internal readonly record struct StationElevation(double StationM, double ElevationM);

    /// <summary>A structure along the view: its outer faces as stations.</summary>
    internal sealed record StructureSpan(string Key, double StartStationM, double EndStationM);

    /// <summary>A crossing pipe at its crossing station: outside top and bottom.</summary>
    internal sealed record CrossingPipe(string Key, double StationM, double TopElevM, double BottomElevM);

    internal sealed record DimInput
    {
        private readonly IReadOnlyList<StationElevation> _top = Frozen.List<StationElevation>(null);
        private readonly IReadOnlyList<StationElevation> _bottom = Frozen.List<StationElevation>(null);
        private readonly IReadOnlyList<IReadOnlyList<StationElevation>> _surface = Frozen.List<IReadOnlyList<StationElevation>>(null);
        private readonly IReadOnlyList<StructureSpan> _structures = Frozen.List<StructureSpan>(null);
        private readonly IReadOnlyList<CrossingPipe> _crossings = Frozen.List<CrossingPipe>(null);

        /// <summary>Outside top of the duct: vertices, stations strictly ascending.</summary>
        public IReadOnlyList<StationElevation> DuctTop { get => _top; init => _top = Frozen.List(value); }
        /// <summary>Outside bottom of the duct: the same station range as <see cref="DuctTop"/>, always below it.</summary>
        public IReadOnlyList<StationElevation> DuctBottom { get => _bottom; init => _bottom = Frozen.List(value); }
        /// <summary>The proposed surface as continuous runs in station order; a gap in the surface is a break between runs.</summary>
        public IReadOnlyList<IReadOnlyList<StationElevation>> ProposedSurface
        {
            get => _surface;
            init => _surface = Frozen.List(value == null ? null : System.Linq.Enumerable.Select(value, run => Frozen.List(run)));
        }
        public IReadOnlyList<StructureSpan> Structures { get => _structures; init => _structures = Frozen.List(value); }
        public IReadOnlyList<CrossingPipe> Crossings { get => _crossings; init => _crossings = Frozen.List(value); }

        /// <summary>The first station this view or piece dimensions (section 7, "Ownership").</summary>
        public double OwnedFromM { get; init; }
        /// <summary>The station where its owned range ends: excluded, unless <see cref="OwnsEnd"/>.</summary>
        public double OwnedToM { get; init; }
        /// <summary>True for an unsplit view and for the last piece of a split one: it also owns <see cref="OwnedToM"/>.</summary>
        public bool OwnsEnd { get; init; } = true;
    }

    /// <summary>The DIM rules. The grade change and spacing defaults are open question Q4 (to be fitted to sheet 434132).</summary>
    internal sealed record DimRules
    {
        /// <summary>|grade in − grade out| at a break, percent, for it to be major.</summary>
        public double MajorGradeChangePct { get; init; } = 2.0;
        /// <summary>Vertices within this span of a cluster's first vertex form one break.</summary>
        public double MergeWithinM { get; init; } = 5.0;
        /// <summary>No two cover DIMs closer than this.</summary>
        public double MinSpacingM { get; init; } = 15.0;
        /// <summary>A cover DIM where the duct leaves each structure face (user decision).</summary>
        public bool AtStructureFaces { get; init; } = true;
        /// <summary>Project rule: a clearance below this keeps its DIM and adds a warning.</summary>
        public double MinClearanceM { get; init; } = 0.30;
    }

    internal enum DimKind { Cover = 0, Clearance = 1 }

    /// <summary>The reasons a DIM is planned (<see cref="PlannedDim.Reason"/>).</summary>
    internal static class DimReasons
    {
        public const string StructureFace = "structureFace";
        public const string GradeBreak = "gradeBreak";
        public const string CrossingAbove = "crossingAbove";
        public const string CrossingBelow = "crossingBelow";
    }

    /// <param name="ValueM">The true distance the DIM shows: UpperElevM − LowerElevM.</param>
    /// <param name="SourceKey">The structure or crossing key; for a grade break, empty.</param>
    internal sealed record PlannedDim(DimKind Kind, double StationM, double LowerElevM, double UpperElevM, double ValueM,
        string SourceKey, string Reason);

    internal sealed record DimPlan(IReadOnlyList<PlannedDim> Dims, IReadOnlyList<OperationIssue> Issues)
    {
        private readonly IReadOnlyList<PlannedDim> _dims = Frozen.List(Dims);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List(Issues);
        public IReadOnlyList<PlannedDim> Dims { get => _dims; init => _dims = Frozen.List(value); }
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }
    }
}
