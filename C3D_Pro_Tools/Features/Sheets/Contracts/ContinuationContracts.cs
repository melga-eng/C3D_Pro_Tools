using System.Collections.Generic;

// Continuation notes (docs/sheet-production-contract.md, section 8): which plan sheets continue from each edge of a plan sheet.
// Model plan metres. The engine is ContinuationNotes.Resolve.
namespace C3DProTools.Features.Sheets.Contracts
{
    /// <param name="ModelBoundary">The plan viewport outline, model metres, closed, in order.</param>
    internal sealed record SheetArea(string Key, string DrawingNumber, IReadOnlyList<Point2> ModelBoundary)
    {
        private readonly IReadOnlyList<Point2> _boundary = Frozen.List(ModelBoundary);
        public IReadOnlyList<Point2> ModelBoundary { get => _boundary; init => _boundary = Frozen.List(value); }
    }

    internal sealed record ContinuationRules(double ToleranceM = 0.05, double MinSharedM = 1.0);

    /// <param name="PathM">The shared part of the sheet's boundary, in the boundary's vertex order.</param>
    /// <param name="MidpointM">Halfway along <paramref name="PathM"/>: where the note goes.</param>
    internal sealed record ContinuationEdge(string NeighbourKey, string NeighbourDrawingNumber, IReadOnlyList<Point2> PathM,
        Point2 MidpointM, double SharedLengthM)
    {
        private readonly IReadOnlyList<Point2> _path = Frozen.List(PathM);
        public IReadOnlyList<Point2> PathM { get => _path; init => _path = Frozen.List(value); }
    }

    internal sealed record ContinuationPlan(IReadOnlyList<ContinuationEdge> Edges, IReadOnlyList<OperationIssue> Issues)
    {
        private readonly IReadOnlyList<ContinuationEdge> _edges = Frozen.List(Edges);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List(Issues);
        public IReadOnlyList<ContinuationEdge> Edges { get => _edges; init => _edges = Frozen.List(value); }
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }
    }
}
