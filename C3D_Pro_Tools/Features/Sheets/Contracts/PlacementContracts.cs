using System.Collections.Generic;

// Placement score (docs/sheet-production-contract.md, section 9): how good an arrangement that is already planned is. Paper
// millimetres in the viewport's frame, as LabelLayout works. The engine is PlacementScoring.
namespace C3DProTools.Features.Sheets.Contracts
{
    /// <summary>As LabelLayout's ObstacleGroup: what a label is checked against.</summary>
    internal enum ScoredObstacleGroup { Text = 0, Leader = 1, Network = 2 }

    /// <param name="Network">The network a Network obstacle belongs to; null for other groups.</param>
    /// <param name="OutlineMm">A convex outline; two points make a segment.</param>
    internal sealed record ScoredObstacle(ScoredObstacleGroup Group, string Network, IReadOnlyList<Point2> OutlineMm)
    {
        private readonly IReadOnlyList<Point2> _outline = Frozen.List(OutlineMm);
        public IReadOnlyList<Point2> OutlineMm { get => _outline; init => _outline = Frozen.List(value); }
    }

    /// <param name="KeepClearOf">The networks whose parts the label must not cover.</param>
    /// <param name="RegionMm">The profile view the label must stay inside; null in plan.</param>
    /// <param name="BoxMm">Its box when placed; null when not.</param>
    internal sealed record ScoredLabel(string Key, Point2 AnchorMm, IReadOnlySet<string> KeepClearOf, Rect2? RegionMm,
        bool Placed, Rect2? BoxMm, Point2? LeaderStartMm, Point2? LeaderEndMm)
    {
        private readonly IReadOnlySet<string> _keepClear = Frozen.Names(KeepClearOf);
        public IReadOnlySet<string> KeepClearOf { get => _keepClear; init => _keepClear = Frozen.Names(value); }
    }

    internal sealed record PlacementInput(IReadOnlyList<Point2> ViewportMm, double MarginMm, IReadOnlyList<ScoredObstacle> Obstacles,
        IReadOnlyList<ScoredLabel> Labels)
    {
        private readonly IReadOnlyList<Point2> _viewport = Frozen.List(ViewportMm);
        private readonly IReadOnlyList<ScoredObstacle> _obstacles = Frozen.List(Obstacles);
        private readonly IReadOnlyList<ScoredLabel> _labels = Frozen.List(Labels);
        public IReadOnlyList<Point2> ViewportMm { get => _viewport; init => _viewport = Frozen.List(value); }
        public IReadOnlyList<ScoredObstacle> Obstacles { get => _obstacles; init => _obstacles = Frozen.List(value); }
        public IReadOnlyList<ScoredLabel> Labels { get => _labels; init => _labels = Frozen.List(value); }
    }

    internal sealed record PlacementScore(int Unplaced, int Overlaps, int OutsideArea, int LeaderCrossings, int LeaderOverText,
        double MeanLeaderMm, double MaxLeaderMm);

    /// <param name="Worse">The names of the metrics that got worse (nameof the PlacementScore property).</param>
    internal sealed record ScoreComparison(bool NoWorse, IReadOnlyList<string> Worse)
    {
        private readonly IReadOnlyList<string> _worse = Frozen.List(Worse);
        public IReadOnlyList<string> Worse { get => _worse; init => _worse = Frozen.List(value); }
    }
}
