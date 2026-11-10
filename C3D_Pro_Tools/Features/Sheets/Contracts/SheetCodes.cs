// Stable issue codes of the sheet production feature (docs/sheet-production-contract.md). Tests, logs and the UI match these
// strings; never change one once released, add a new one instead.
namespace C3DProTools.Features.Sheets.Contracts
{
    /// <summary>The contract version these types implement. It is part of every <see cref="BuildSheetsPreview.SceneStamp"/>.</summary>
    internal static class SheetContract
    {
        public const string Version = "0.3";
    }

    /// <summary>Issue codes raised by the adapter and the application (sections 2–5 and 10).</summary>
    internal static class SheetCodes
    {
        public const string UnitsNotMetres = "sheets.units.notMetres";
        public const string Busy = "sheets.busy";
        public const string Internal = "sheets.internal";
        /// <summary>An object the scene reader could not read: left out of the scene, with a warning naming it.</summary>
        public const string SceneReadFailed = "sheets.scene.readFailed";
        /// <summary>No profile in the drawing has the existing (or proposed) role: the role's surface name is probably not the drawing's.</summary>
        public const string SceneRoleUnmatched = "sheets.scene.roleUnmatched";
        /// <summary>A style the plan uses could not be fingerprinted in full: an edit to it is caught only by the apply postcondition.</summary>
        public const string FingerprintIncomplete = "sheets.scene.fingerprintIncomplete";
        /// <summary>The scene has read errors, so nothing is planned (a part or label that was not read would be mishandled).</summary>
        public const string SceneIncomplete = "sheets.scene.incomplete";
        /// <summary>The plan worked out at Apply differs from the one previewed: nothing is written, review the new preview.</summary>
        public const string PlanChanged = "sheets.preview.planChanged";
        /// <summary>Several label rule sets and none chosen.</summary>
        public const string RulesAmbiguous = "sheets.rules.ambiguous";

        public const string RequestDrawing = "sheets.request.drawing";
        public const string RequestNoSteps = "sheets.request.noSteps";
        public const string RequestLayout = "sheets.request.layout";
        public const string RequestSettings = "sheets.request.settings";

        public const string RulesMissing = "sheets.rules.missing";
        public const string RulesName = "sheets.rules.name";
        public const string RuleId = "sheets.rule.id";
        public const string RuleIdDuplicate = "sheets.rule.idDuplicate";
        public const string RuleStyleEmpty = "sheets.rule.styleEmpty";
        public const string RuleStyleMissing = "sheets.rule.styleMissing";
        public const string RuleStyleDuplicate = "sheets.rule.styleDuplicate";
        public const string RuleAnchor = "sheets.rule.anchor";
        public const string RuleFrequency = "sheets.rule.frequency";
        public const string RuleEnum = "sheets.rule.enum";

        public const string LabelOrphanPinned = "sheets.label.orphanPinned";
        public const string LabelDuplicateTag = "sheets.label.duplicateTag";

        public const string PreviewWrongDrawing = "sheets.preview.wrongDrawing";
        public const string PreviewStale = "sheets.preview.stale";
        public const string PreviewNotApplicable = "sheets.preview.notApplicable";
        public const string ApplyUnknownItem = "sheets.apply.unknownItem";
        public const string ApplyPostconditionFailed = "sheets.apply.postconditionFailed";
    }

    /// <summary>Issue codes of the profile sheet packing engine (section 6).</summary>
    internal static class PackingCodes
    {
        public const string RulesInvalid = "packing.rules.invalid";
        public const string RulesInfeasible = "packing.rules.infeasible";
        public const string ItemInvalid = "packing.item.invalid";
        public const string ItemDuplicateKey = "packing.item.duplicateKey";
    }

    /// <summary>Issue codes of the DIM stations engine (section 7).</summary>
    internal static class DimCodes
    {
        public const string RulesInvalid = "dims.rules.invalid";
        public const string InputInvalid = "dims.input.invalid";
        public const string InputVerticalStep = "dims.input.verticalStep";
        public const string InputDuctInconsistent = "dims.input.ductInconsistent";
        public const string InputSurfaceOverlap = "dims.input.surfaceOverlap";
        public const string CrossingInvalid = "dims.crossing.invalid";
        public const string SurfaceMissing = "dims.surface.missing";
        public const string CoverNegative = "dims.cover.negative";
        public const string CoverSpacing = "dims.cover.spacing";
        public const string CrossingOutsideView = "dims.crossing.outsideView";
        public const string ClearanceClash = "dims.clearance.clash";
        public const string ClearanceBelowMinimum = "dims.clearance.belowMinimum";
    }

    /// <summary>Issue codes of the continuation notes engine (section 8).</summary>
    internal static class ContinuationCodes
    {
        public const string RulesInvalid = "continuation.rules.invalid";
        public const string SheetNotFound = "continuation.sheet.notFound";
        public const string BoundaryInvalid = "continuation.boundary.invalid";
        public const string KeyDuplicate = "continuation.key.duplicate";
    }
}
