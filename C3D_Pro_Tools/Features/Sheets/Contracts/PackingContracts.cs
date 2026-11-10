using System.Collections.Generic;

// Profile sheet packing (docs/sheet-production-contract.md, section 6): which profile view, or piece of one, goes in which row
// of which sheet. Station metres throughout; a view's X axis is 1:1 with stations. The engine is ProfileSheetPacking.Pack.
namespace C3DProTools.Features.Sheets.Contracts
{
    internal enum PackingOrder
    {
        /// <summary>Next-fit in item order (Order, then Key): sheets read in alignment order.</summary>
        Preserve = 0,
        /// <summary>Split pieces first, then the unsplit views longest first, each in the first row with room.</summary>
        FirstFitDecreasing = 1
    }

    /// <param name="Key">Unique (ordinal, ignoring case); the adapter uses the alignment or view handle.</param>
    internal sealed record ProfileViewItem(string Key, string AlignmentName, double StartStationM, double EndStationM, int Order);

    /// <summary>The packing rules; defaults are the user's standard (450 m rows, two per 1:750 viewport).</summary>
    internal sealed record PackingRules
    {
        /// <summary>C: stations a row holds for one continuous view.</summary>
        public double RowCapacityM { get; init; } = 450;
        public int RowsPerSheet { get; init; } = 2;
        /// <summary>T: the band titles' width along the view's X axis (station metres).</summary>
        public double TitleWidthM { get; init; }
        /// <summary>G: clear space between two views in a row (station metres).</summary>
        public double GapM { get; init; }
        /// <summary>R: split stations are multiples of this (absolute stations, as ProfileViewSplit rounds).</summary>
        public double SplitRoundM { get; init; } = 10;
        /// <summary>P: no split piece is shorter than this.</summary>
        public double MinPieceM { get; init; } = 100;
        /// <summary>O: stations a piece repeats from the end of the piece before it; counted inside its capacity.</summary>
        public double OverlapM { get; init; }
        public PackingOrder Order { get; init; } = PackingOrder.Preserve;
    }

    /// <param name="PieceNo">1-based; 1 of 1 for a view that is not split.</param>
    /// <param name="OffsetM">Where the slot's graph starts in its row, from the row start, titles and gaps included.</param>
    internal sealed record PackedSlot(string Key, int PieceNo, int PieceCount, double StartStationM, double EndStationM, double OffsetM);

    internal sealed record PackedRow(int Index, IReadOnlyList<PackedSlot> Slots, double UsedM)
    {
        private readonly IReadOnlyList<PackedSlot> _slots = Frozen.List(Slots);
        public IReadOnlyList<PackedSlot> Slots { get => _slots; init => _slots = Frozen.List(value); }
    }

    internal sealed record PackedSheet(int Index, IReadOnlyList<PackedRow> Rows)
    {
        private readonly IReadOnlyList<PackedRow> _rows = Frozen.List(Rows);
        public IReadOnlyList<PackedRow> Rows { get => _rows; init => _rows = Frozen.List(value); }
    }

    internal sealed record ProfileSheetPlan(IReadOnlyList<PackedSheet> Sheets, IReadOnlyList<OperationIssue> Issues)
    {
        private readonly IReadOnlyList<PackedSheet> _sheets = Frozen.List(Sheets);
        private readonly IReadOnlyList<OperationIssue> _issues = Frozen.List(Issues);
        public IReadOnlyList<PackedSheet> Sheets { get => _sheets; init => _sheets = Frozen.List(value); }
        public IReadOnlyList<OperationIssue> Issues { get => _issues; init => _issues = Frozen.List(value); }
    }
}
