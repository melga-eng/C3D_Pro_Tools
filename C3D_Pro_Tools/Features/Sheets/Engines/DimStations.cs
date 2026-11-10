using System;
using C3DProTools.Features.Sheets.Contracts;

// Engine B (docs/sheet-production-contract.md, section 7). Signature only: implemented by Codex on feature/dim-stations, which
// owns this file from then on. Pure: plain data in and out, no Autodesk or UI types.
namespace C3DProTools.Features.Sheets.Engines
{
    internal static class DimStations
    {
        /// <summary>The cover and clearance DIMs of one view or piece, as section 7 defines. Same input, same plan.</summary>
        public static DimPlan Plan(DimInput input, DimRules rules) =>
            throw new NotImplementedException("DIM stations: docs/sheet-production-contract.md section 7 (feature/dim-stations).");
    }
}
