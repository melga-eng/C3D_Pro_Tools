using System;
using System.Collections.Generic;
using C3DProTools.Features.Sheets.Contracts;

// Engine C (docs/sheet-production-contract.md, section 8). Signature only: implemented by Codex on feature/continuation-notes,
// which owns this file from then on. Pure: plain data in and out, no Autodesk or UI types.
namespace C3DProTools.Features.Sheets.Engines
{
    internal static class ContinuationNotes
    {
        /// <summary>The neighbour edges of sheet <paramref name="sheetKey"/>, as section 8 defines. Same input, same plan.</summary>
        public static ContinuationPlan Resolve(IReadOnlyList<SheetArea> sheets, string sheetKey, ContinuationRules rules) =>
            throw new NotImplementedException("Continuation notes: docs/sheet-production-contract.md section 8 (feature/continuation-notes).");
    }
}
