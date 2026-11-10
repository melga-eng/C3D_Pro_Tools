using System;
using System.Collections.Generic;
using C3DProTools.Features.Sheets.Contracts;

// Engine A (docs/sheet-production-contract.md, section 6). Signature only: implemented by Codex on feature/profile-sheet-packing,
// which owns this file from then on. Pure: plain data in and out, no Autodesk or UI types.
namespace C3DProTools.Features.Sheets.Engines
{
    internal static class ProfileSheetPacking
    {
        /// <summary>Splits and packs the views into rows and sheets, as section 6 defines. Same input, same plan.</summary>
        public static ProfileSheetPlan Pack(IReadOnlyList<ProfileViewItem> items, PackingRules rules) =>
            throw new NotImplementedException("Profile sheet packing: docs/sheet-production-contract.md section 6 (feature/profile-sheet-packing).");
    }
}
