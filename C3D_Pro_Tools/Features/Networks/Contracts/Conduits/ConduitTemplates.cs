using System;
using System.Collections.Generic;
using System.Linq;

// Conduit templates: the arrangement of conduits inside a duct bank (encasement), kept apart from the construction variants
// of the drawing it came from and from any drawing's parts. Plain values only; no Autodesk or UI types, no handles.
// Every length here is in metres (field names end in "M"); see ConduitLimits for how they meet drawing units.
namespace C3DProTools.Features.Networks.Contracts
{
    /// <summary>The utility a template belongs to, for grouping in the form. Has no effect on geometry.</summary>
    internal enum ConduitUtility { Ict, Mv, Custom }

    /// <summary>
    /// How the source drawing writes an arrangement label. The label is kept as written and never parsed: ICT sheets write
    /// rows × columns ("3X2" = 3 rows, 2 columns), MV sheets write columns × rows ("5 X 3" = 5 columns, 3 rows).
    /// </summary>
    internal enum ArrangementLabelOrder { RowsByColumns, ColumnsByRows, None }

    /// <summary>Where the conduit grid sits in the encasement's section, before <see cref="ConduitSection.SideShiftM"/>.</summary>
    internal enum ConduitPlacement
    {
        /// <summary>The grid's centre is on the encasement's centreline (across and up).</summary>
        SectionCentred = 0,
        /// <summary>Centred across; the lowest row's centre is <see cref="ConduitSection.BottomToLowestCentreM"/> above the encasement's outside bottom.</summary>
        BottomReferenced = 1
    }

    /// <summary>The construction context a variant is drawn for. Metadata only: it never moves or resizes an encasement.</summary>
    internal enum ConstructionContext { PavementUnderTaxiway, NormalRoadsAndUnpaved, NormalRoadCrossing, Unspecified }

    /// <summary>
    /// The conduit grid: <see cref="Columns"/> across × <see cref="Rows"/> up, at centre-to-centre pitches. Column 1 is the
    /// leftmost looking along the run's direction; row 1 is the lowest. This is the only part of a template that decides
    /// where conduits go.
    /// </summary>
    /// <param name="HorizontalPitchM">Centre to centre across (m); ignored with one column.</param>
    /// <param name="VerticalPitchM">Centre to centre up (m); ignored with one row.</param>
    internal sealed record ConduitArrangement(int Columns, int Rows, double HorizontalPitchM, double VerticalPitchM)
    {
        public int Count => Columns * Rows;

        /// <summary>Column centres from the grid's middle, left to right (m; + = right of the run's direction).</summary>
        public IReadOnlyList<double> HorizontalOffsetsM() => Centred(Columns, HorizontalPitchM);

        /// <summary>Row centres from the grid's middle, bottom to top (m; + = up).</summary>
        public IReadOnlyList<double> VerticalOffsetsM() => Centred(Rows, VerticalPitchM);

        private static IReadOnlyList<double> Centred(int n, double pitch) =>
            Frozen.List(Enumerable.Range(0, Math.Max(0, n)).Select(i => n == 1 ? 0.0 : Math.Round((i - (n - 1) / 2.0) * pitch, 6)));
    }

    /// <summary>
    /// The section placement the user applies to a template: where the grid sits in each encasement. A template carries a
    /// default; a request may change it (the preview notes the difference).
    /// </summary>
    /// <param name="BottomToLowestCentreM"><see cref="ConduitPlacement.BottomReferenced"/>: height of the lowest row's centre above the encasement's outside bottom (m).</param>
    /// <param name="SideShiftM">Moves the whole grid across (m; + = right of the run's direction). 0 = centred.</param>
    internal sealed record ConduitSection(ConduitPlacement Placement, double BottomToLowestCentreM = 0, double SideShiftM = 0)
    {
        public static readonly ConduitSection Centred = new ConduitSection(ConduitPlacement.SectionCentred);
    }

    /// <summary>
    /// Design values from the source drawing used to check a plan, not to place conduits. Null means the drawing (as
    /// summarised) does not establish the value; the preview then says it was not checked, rather than assuming one.
    /// </summary>
    /// <param name="ConduitCallout">The conduit as the drawing calls it out, e.g. "110 mm OD HDPE"; display only.</param>
    /// <param name="ConduitOutsideDiameterM">The conduit's outside diameter (m), when the drawing states it.</param>
    /// <param name="MinClearGapM">Clear gap between neighbouring conduits (m), when stated.</param>
    /// <param name="EdgeClearanceM">Conduit outside to encasement edge (m), when stated.</param>
    /// <param name="EnvelopeWidthM">The encasement section's width (m), when stated; checked against each chosen encasement, never applied to it.</param>
    /// <param name="EnvelopeHeightM">The encasement section's height (m), when stated.</param>
    internal sealed record ConduitDesignReference(string ConduitCallout, double? ConduitOutsideDiameterM, double? MinClearGapM,
        double? EdgeClearanceM, double? EnvelopeWidthM, double? EnvelopeHeightM);

    /// <summary>
    /// What each conduit carries, from the drawing's notes. Metadata: subducts are not modelled in phase one, and spare
    /// capacity never removes conduits (every grid position is generated).
    /// </summary>
    internal sealed record ConduitCapacity(int SubductsPerConduit = 0, double? SubductOutsideDiameterM = null, double? SpareCapacityFraction = null, string Material = null);

    /// <summary>Where a template's values come from, for traceability. Null fields: not from a drawing (custom).</summary>
    /// <param name="DrawingNumber">e.g. "SW00670060000D-C00002-JCB-ZZ-DWG-IC-XX-006501".</param>
    /// <param name="Label">The arrangement label as written on the drawing ("3X2", "5 X 3"); never parsed (see <see cref="LabelOrder"/>).</param>
    internal sealed record ConduitSourceReference(string DrawingNumber, string Revision, string SheetTitle, string Label, ArrangementLabelOrder LabelOrder);

    /// <summary>
    /// A construction variant of an arrangement: the same conduit centres drawn for another context (pavement or normal),
    /// with its own depth, cover and reinforcement notes. Chosen for the record only; selecting one never changes geometry,
    /// elevation or cover.
    /// </summary>
    /// <param name="Id">The drawing's detail or type ID: "1A", "Type 01".</param>
    internal sealed record ConstructionVariant(string Id, string Description, ConstructionContext Context);

    /// <summary>
    /// A reusable conduit template: an arrangement with its default placement, design reference, capacity notes, source and
    /// construction variants. Holds no drawing objects, handles or part choices (those are mapped per drawing: see
    /// <see cref="ConduitPartMapping"/>). Standard templates are read-only; users change a <see cref="CustomCopy"/>.
    /// Lists are frozen when set.
    /// </summary>
    /// <param name="Key">Stable identifier ("ICT-2C-3R"); unique in a library, compared ignoring case.</param>
    /// <param name="Name">As the form lists it ("ICT 3×2 (3 rows × 2 columns)").</param>
    internal sealed record ConduitTemplate(string Key, string Name, ConduitUtility Utility, ConduitArrangement Arrangement, ConduitSection DefaultSection,
        ConduitDesignReference Design, ConduitCapacity Capacity, ConduitSourceReference Source, IReadOnlyList<ConstructionVariant> Variants)
    {
        private readonly IReadOnlyList<ConstructionVariant> _variants = Frozen.List(Variants);
        private readonly IReadOnlyList<string> _notes = Frozen.List<string>(null);

        public IReadOnlyList<ConstructionVariant> Variants { get => _variants; init => _variants = Frozen.List(value); }

        /// <summary>True for a built-in template from <see cref="ConduitTemplateLibrary"/>; it cannot be edited or deleted.</summary>
        public bool IsStandard { get; init; }

        /// <summary>The template a custom copy was made from; null for a standard template or one made from scratch.</summary>
        public string DerivedFromKey { get; init; }

        /// <summary>Review notes: uncertainties and interpretations the preview repeats (e.g. "conduit OD not stated"). Frozen when set.</summary>
        public IReadOnlyList<string> Notes { get => _notes; init => _notes = Frozen.List(value); }

        /// <summary>
        /// An editable copy under a new key and name: same values, not standard, <see cref="DerivedFromKey"/> set. The source
        /// reference is kept (the values started from that drawing) and every change is validated like any template.
        /// </summary>
        public ConduitTemplate CustomCopy(string key, string name) =>
            this with { Key = key, Name = name, IsStandard = false, DerivedFromKey = Key };
    }

    /// <summary>
    /// A drawing's part for a template's conduits, by names only so it can be remembered and reused across drawings; the
    /// adapter resolves the names in the drawing at preview and reports any it cannot find. A part to copy from the drawing
    /// (a family-less conduit) is drawing-scoped and goes in the request (<see cref="ConduitPartChoice.Template"/>), never here.
    /// </summary>
    internal sealed record ConduitPartMapping(string TemplateKey, string PartsListName, string FamilyName, string SizeName);
}
