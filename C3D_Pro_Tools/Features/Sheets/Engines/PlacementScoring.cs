using System;
using System.Collections.Generic;
using C3DProTools.Features.Sheets.Contracts;

// Engine D (docs/sheet-production-contract.md, section 9). Signatures only: implemented by Codex on feature/placement-score,
// which owns this file from then on. Pure: ArrangeCase and PlanLabel are plain types (no Autodesk API).
namespace C3DProTools.Features.Sheets.Engines
{
    internal static class PlacementScoring
    {
        /// <summary>The score of an arrangement that is already planned, as section 9 defines.</summary>
        public static PlacementScore Score(PlacementInput input) =>
            throw new NotImplementedException("Placement score: docs/sheet-production-contract.md section 9 (feature/placement-score).");

        /// <summary>The scoring input for a case and the planner's labels for it (Spot null: not placed; Region from the case).</summary>
        public static PlacementInput FromPlanned(ArrangeCase arrangeCase, IReadOnlyList<PlanLabel> planned) =>
            throw new NotImplementedException("Placement score: docs/sheet-production-contract.md section 9 (feature/placement-score).");

        /// <summary>Which metrics of <paramref name="candidate"/> are worse than <paramref name="baseline"/>, as section 9 defines.</summary>
        public static ScoreComparison Compare(PlacementScore baseline, PlacementScore candidate) =>
            throw new NotImplementedException("Placement score: docs/sheet-production-contract.md section 9 (feature/placement-score).");
    }
}
