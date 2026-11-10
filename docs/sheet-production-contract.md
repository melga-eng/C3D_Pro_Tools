# Sheet production: contract proposal (phase 1)

Status: **v0.2, for Codex review.** It answers the eight findings of review 1; see "Revision 2" at the end. No engine, adapter
or UI code until the user accepts it.
Branch: `feature/redesign-roadmap` from `main` at 1243c94. Contract owner: Claude.

Inputs:
- [redesign-roadmap.md](redesign-roadmap.md)
- the tile 124 submittal sheets: plan 102054 and profile 434132, in `D:\Pinnacle Working Files\000501\_PDF Submittals\ICT-Tile-124\V3\`
- the ICT typical details 006501
- ARCHITECTURE.md
- the user's answers of 2026-11-10 (below)

## User decisions this contract depends on

- Crossing utilities (water, fire, LV, MV) are **pipe networks in the same drawing**.
- DIMs are **always vertical**:
  - **Clearance**, crossing above the duct: from the duct top to the crossing pipe's bottom.
  - **Clearance**, crossing below the duct: from the duct bottom to the crossing pipe's top.
  - **Cover:** from the duct top to the proposed surface, where the duct leaves each manhole and at **major** grade breaks only.
- Profile sheet viewports are at **1:750**. They hold **two rows** of views, each **450 m of stations** (600 mm on paper). One
  continuous view uses the whole 450 m; each further view in a row costs its band-title width.
- Profile sheet title blocks keep **1:500**, the horizontal drawing scale. The tool never writes the viewport scale there.
- **Cable-content notes are out of scope.** The user is removing them, so the tool neither creates nor keeps them.
- The workspace is a **WPF** palette.
- Codex builds the pure-logic engines (sections 6 to 9) and reviews Claude's and Antigravity's work. Claude reviews Codex's work.

## 1. Folders, owners, branches

New feature folder `C3D_Pro_Tools/Features/Sheets/`, namespace `C3DProTools.Features.Sheets.*`.

| Folder / file | Holds | Owner | Branch |
|---|---|---|---|
| `Features/Shared/Contracts/` | `DrawingIdentity`, `DrawingObjectRef`, `Point3`, `OperationStatus`, `OperationIssue`, `ItemStatus`, `Atomicity` and `Frozen`, moved from `Features/Networks/Contracts` | Claude | `feature/sheets-contracts` |
| `Features/Sheets/Contracts/` | Every type in sections 2–5 and 10–11, plus the engine signatures of sections 6–9, with no bodies | Claude | `feature/sheets-contracts` |
| `Features/Sheets/Engines/ProfileSheetPacking.cs` | Section 6 | **Codex** | `feature/profile-sheet-packing` |
| `Features/Sheets/Engines/DimStations.cs` | Section 7 | **Codex** | `feature/dim-stations` |
| `Features/Sheets/Engines/ContinuationNotes.cs` | Section 8 | **Codex** | `feature/continuation-notes` |
| `Features/Sheets/Engines/PlacementScoring.cs` | Section 9 | **Codex** | `feature/placement-score` |
| `Features/Sheets/Application/` | Label rule evaluation, sheet ownership, `BuildSheetsWorkflow`, overview | Claude | `feature/sheet-scene`, `feature/label-rules`, `feature/build-sheets` |
| `Features/Sheets/Civil3D/` | Scene reader, crossing reader, writers for labels, views, DIMs, marks, notes and build records | Claude | as above |
| `Features/Workspace/`, `Features/Sheets/UI/` | Workspace palette, Sheets panel, `SyntheticSheetProduction` | **Antigravity** | `ui/workspace-shell` |
| `tests/C3D_Pro_Tools.Tests/Sheets/` | One test file per engine or contract file, owned by that file's owner | as above | with its file |
| `tests/C3D_Pro_Tools.Tests/Cases/` | Arrangement cases exported from the tile 124 sheets | Claude | `feature/sheet-scene` |

`feature/sheets-contracts` is merged first. Every other branch starts from that merge commit. Engines take and return plain
values only (no Autodesk, WinForms or WPF types) and never read the drawing.

**Units.** Metres only. The adapter rejects drawings where `MetresPerDrawingUnit != 1` (`sheets.units.notMetres`). Stations
and elevations are in metres. Paper values are in millimetres and their fields end in `Mm`.

**Shared conventions for every engine:**
- Inputs are validated before any work is done.
- An Error about the rules, or about the whole input, returns an empty plan.
- An Error about one item skips that item and the rest go on.
- Numbers are compared with an absolute tolerance of `1e-9` (stations, elevations, mm) unless a rule says otherwise. A test of
  "≥ x" passes at `x − 1e-9`.
- Lists come back in a defined order, so the same input always gives the same output.
- Issue codes are stable and appear in the tables below.

## 2. Sheet scene (Claude)

`ISheetScene.Read(SheetSceneRequest) → SheetScene` takes a read-only snapshot of the drawing. Planning works only from the
snapshot.

```
SheetSceneRequest(DrawingIdentity Drawing, IReadOnlyList<string> Layouts /* empty = all */)

SheetScene
  DrawingIdentity Drawing
  IReadOnlyList<SheetSnapshot> Sheets
  IReadOnlyList<NetworkSnapshot> Networks      // network name; pipes (handle, name, type code, start/end, outer width and height); structures (handle, name, insertion, type)
  IReadOnlyList<AlignmentSnapshot> Alignments  // handle, name, station range, profiles by role (ProfileRoleRules), profile views
  IReadOnlyList<ProfileViewSnapshot> ProfileViews // handle, alignment, station and elevation range, band set, drawn parts, vertical exaggeration
  IReadOnlyList<CrossingSnapshot> Crossings    // section 3
  IReadOnlyList<LabelSnapshot> Labels          // network, alignment and profile labels; build tag and placement (section 4)
  IReadOnlyList<BuildRecord> BuildRecords      // section 10
  IReadOnlyList<OperationIssue> Issues

SheetSnapshot(string Layout, int TabOrder, SheetKind Kind /* Plan | Profile | Other */, string Tile, string Part,
              string DrawingNumber, IReadOnlyList<ViewportSnapshot> Viewports)

ViewportSnapshot(DrawingObjectRef Ref, double PaperMmPerModelM /* 2.0 at 1:500, 1.333… at 1:750 */, double TwistDeg,
                 IReadOnlyList<Point2> ModelBoundary /* clip boundary if clipped */, Box2 PaperBoxMm, bool Locked)
```

**Sheet kind.**
- `Plan`: `SheetManager` reads a tile and part from the sheet (`SheetRecord.IsPart`).
- `Profile`: the sheet is not a tile part and its main viewport shows a profile view.
- `Other`: everything else.

**Scale text on profile sheets.** The build never writes the title block scale or the scale note of a `Profile` sheet.
`feature/sheet-scene` also changes Sheet Manager so that two features skip profile sheets:
- the "Write viewport scale on the sheet" button (`FormSheetManager.cs:891`)
- the title-block/scale-note mismatch fix (`SheetManager.cs:803`)

## 3. Crossings (Claude, phase 1 reader)

A limited reader that covers only what phase 1 needs. Full coordination analysis stays in roadmap phase 3. It reuses the Cover
Optimizer's crossing discovery (`CrossingGeometry`, `RouteCrossing`) and its outer heights (`CoverRoute.OuterHeightOf`).

```
CrossingSnapshot(DrawingObjectRef ProfileView, DrawingObjectRef CrossingPipe, string CrossingNetwork, string Name,
                 double StationM, double CrossingTopM, double CrossingBottomM, bool ShownInView)
```

- A crossing is a pipe of **another network** that crosses the view's alignment in plan, at a station inside the view's range.
- Top and bottom are the pipe's **outside** levels at the crossing point.
- `ShownInView` is true when the view already draws that part, as `ProfilePartDetection` reports a crossing.
- The `ProfileViews` build step adds missing crossing pipes to their views. That is how they appear on sheet 434132.

The duct's top and bottom at the crossing are not part of this record. The DIM engine takes them from the duct profiles
(section 7).

## 4. Labels, ownership and pinning (Claude)

### Which sheet labels a part

| Target | Labelled on |
|---|---|
| Structure | The plan viewport that contains its insertion point |
| Pipe | The plan viewport that shows the longest part of it |
| Joint (pipe end) | The plan viewport that contains the point |
| Alignment | Every plan viewport that shows at least `MinAlignmentShownM` (default 20 m) of it |

- Ties go to the lower tab order, then to the lower viewport handle.
- In profile, a part is labelled in each view that shows it. Views of one alignment do not overlap in station, except by the
  packing overlap. Inside an overlap, the label goes in the earlier piece.
- A part outside every viewport is not labelled. That is an Info issue only when a rule selects the part.

### Label rules

These extend the existing `LabelSet` (Annotation Studio). Rule sets are saved in the project settings file,
`<drawing folder>\C3DPro.project.json`, under `labelRules`.

```
LabelRuleSet(string Name, int Version, IReadOnlyList<LabelRule> Rules)

LabelRule(string Id, ViewKind View /* Plan | Profile */, TargetSelector Select, string LabelStyle, Anchor At,
          Frequency Every, bool Leader, SidePreference Side, int Priority)
TargetSelector(TargetKind Kind /* Structure | Pipe | Joint | Alignment | AlignmentStations */, string NetworkPattern,
               string TypeCodePattern, bool OtherNetworksOnly)
Anchor:        Insertion | Start | Middle | End | EachJoint
Frequency:     PerPart | PerRun | OncePerViewport
SidePreference: Alternate | Left | Right | AwayFromRoute
```

- The label style must exist before anything is added. A missing style is an Error, `sheets.rule.styleMissing`, and the rule is
  skipped.
- `AlignmentStations` sets the alignment's station labels from a label set named in the rule's `LabelStyle`.

### Pinning

Each label the tool creates carries XData (application `C3DPRO_BUILD`):

| Field | Holds |
|---|---|
| `RuleId` | The rule that made it |
| `SourceKey` | The part handle, plus `start` / `end` / joint index |
| `Layout` | The sheet it belongs to |
| `Pin` | `Auto` or `Pinned` |
| Recorded placement | Label style name, dragged state, dragged text location in **model** X/Y, anchor ratio along the part, text rotation |

A tool label is **pinned** when either:
- `Pin = Pinned`, or
- any recorded field differs from the label as it is now:
  - a position by more than 0.001 m (0.002 mm on paper at 1:500)
  - the ratio by more than 1e-6
  - the rotation by more than 1e-4 rad
  - the style name is different, compared ordinally

The first build that finds such a difference also writes `Pin = Pinned`, so later re-reads cannot lose it.

What a build may do with each kind of label:

| Label | Moved | Deleted | Restyled | Obstacle for placement |
|---|---|---|---|---|
| Tool label, not pinned | Yes | Yes | Yes | — |
| Tool label, pinned | Never | Never; Warning `sheets.label.orphanPinned` if its part is gone | Never | Yes |
| Label without a build tag (placed by hand) | Never | Never | Never | Yes |

**Duplicate tags.** A copied object copies its XData. When two labels carry the same `RuleId` + `SourceKey` + `Layout`, the
lowest handle keeps the tag. The others count as hand-placed, with a Warning `sheets.label.duplicateTag`.

**Pin and Release.** The workspace has a Pin action and a Release action for the selected labels or for a sheet, each with its
own preview:
- **Pin** writes `Pin = Pinned` and moves nothing.
- **Release** writes `Pin = Auto` and records the current placement, so the next build may move the label again.

### Starting rule set (from sheets 102054 and 434132)

| Id | View | Target | Content (from the label style) |
|---|---|---|---|
| `ict.plan.manhole` | Plan | ICT structures | Name, type, cover, E, N, CL, inner dimensions, height |
| `ict.plan.joint` | Plan | ICT joints | Name, type, size, IL, N, E |
| `ict.plan.alignment` | Plan | ICT alignments, once per viewport | "ALIGNMENT = name" |
| `ict.plan.stations` | Plan | ICT alignment stations | Chainage ticks and text |
| `ict.profile.manhole` | Profile | ICT structures | As in plan |
| `ict.profile.segment` | Profile | ICT pipes | Name, L, slope S:% |
| `ict.profile.joint` | Profile | ICT joints | N, E, IL |
| `other.profile.crossing` | Profile | Crossing pipes (`OtherNetworksOnly`) | Name / size ("500 mm Water Pipe-EX") |
| `other.profile.structure` | Profile | Structures of other networks in the view | Name, base elevation, N, E |

Placement uses the existing `LabelPlanner` (paper mm, viewport frame), with pinned and hand-placed labels as obstacles.

## 5. Build steps and the inventory

Every item on the target sheets maps to one step, one owner and one acceptance check.

| Item | Step | Made by | Owner | Acceptance check on tile 124 |
|---|---|---|---|---|
| Manhole, joint, alignment and station labels (plan) | `PlanLabels` | Rules in section 4 | Claude | Every ICT structure, joint and alignment in a plan viewport has exactly one label from its rule |
| Placement of all labels | `Arrange` | `LabelPlanner`, scored by section 9 | Claude; Codex for the score | Placement score: 0 unplaced, 0 overlaps, 0 outside the viewport |
| Profile views: create, split, pack, place, viewports | `ProfileViews` | Section 6 + adapter | Codex (packing), Claude | Every ICT alignment of the tile appears in exactly one slot. Rows ≤ 450 m. Every viewport at 1:750 and locked |
| Bands, datum, view title | `ProfileViews` | Existing `ProfileViewRecipe` and `ProfileViewMath.DatumRange` | Claude | The six bands present; the title reads "name / SCALE: H = 1:500, V = 1:50" |
| Crossing pipes drawn in views | `ProfileViews` | Section 3 | Claude | Every crossing in the view range is drawn |
| Profile labels: manhole, segment, slope, joint, crossing, other structure | `ProfileLabels` | Rules in section 4 | Claude | As for plan labels, per view |
| Cover and clearance DIMs | `Dims` | Section 7 + adapter | Codex (stations), Claude | Each planned DIM exists and its text equals `ValueM` to 0.01 m |
| Start / end of pavement | `ProfileLabels` | Existing `ProfileViewZones`, pavement layers in the project settings (`pavement.layers`) | Claude | One mark at each pavement edge crossed, with its chainage |
| Continuation notes | `SheetData` | Section 8 + adapter | Codex (resolver), Claude | One note per neighbour edge, naming the right drawing number |
| Title block, key plan highlight | `SheetData` | Existing `SheetManager` (`SetAttributes`, `UpdateKeyPlan`) | Claude | No Sheet Manager check fails on the tile's sheets |
| Legend, reference list | — | Out of phase 1 | — | — |
| Revision clouds, RFI notes | — | Manual, never touched | — | Unchanged after a build |

## 6. Engine A: profile sheet packing (Codex)

```
static ProfileSheetPlan ProfileSheetPacking.Pack(IReadOnlyList<ProfileViewItem> items, PackingRules rules)

ProfileViewItem(string Key, string AlignmentName, double StartStationM, double EndStationM, int Order)

PackingRules
  double RowCapacityM = 450       // C
  int    RowsPerSheet = 2
  double TitleWidthM  = 0         // T: band-title width along the view's X axis (1:1 with stations)
  double GapM         = 0         // G: clear space between views in a row
  double SplitRoundM  = 10        // R: split stations are multiples of R (absolute stations, as ProfileViewSplit)
  double MinPieceM    = 100       // P: no split piece shorter than this
  double OverlapM     = 0         // O: stations a piece repeats from the end of the piece before it
  PackingOrder Order  = Preserve  // Preserve | FirstFitDecreasing

ProfileSheetPlan(IReadOnlyList<PackedSheet> Sheets, IReadOnlyList<OperationIssue> Issues)
PackedSheet(int Index, IReadOnlyList<PackedRow> Rows)
PackedRow(int Index, IReadOnlyList<PackedSlot> Slots, double UsedM)
PackedSlot(string Key, int PieceNo /* 1-based */, int PieceCount, double StartStationM, double EndStationM, double OffsetM)
```

**Validation.**
- Every number is finite.
- C > 0, `RowsPerSheet` ≥ 1, 0 < R ≤ C; T, G, P and O ≥ 0.
- **Feasibility:** C ≥ 2·P + R + O and O < C − R. This guarantees that every split moves forward and that moving a split back
  never leaves a piece shorter than P.
- T + G < C. Otherwise no second view could ever share a row.

### Splitting

A view of length L = E − S that is ≤ C is never split, even when it is shorter than P. A longer view is split greedily:

1. The piece starts at s₀ = S.
2. Each split eᵢ is the largest multiple of R that is ≤ sᵢ + C and > sᵢ. The next piece starts at sᵢ₊₁ = eᵢ − O, so the overlap
   **counts inside** the next piece's capacity.
3. When sᵢ + C ≥ E, the last piece is [sᵢ, E]. The view end is never rounded.
4. **Short last piece.** If E − sₙ₋₁ < P, the last split moves back: eₙ₋₂ becomes the largest multiple of R that is
   ≤ E − P + O. Then sₙ₋₁ = eₙ₋₂ − O. Nothing else changes. With the feasibility rule, the piece before stays ≥ P.

| # | View (S → E) | C / R / P / O | Pieces |
|---|---|---|---|
| S1 | 0 → 400 | 450 / 10 / 100 / 0 | [0, 400] |
| S2 | 0 → 500 | 450 / 10 / 100 / 0 | [0, 400], [400, 500] (greedy gave 450 + 50; 50 < 100, so the split moves back to 400) |
| S3 | 0 → 1000 | 450 / 10 / 100 / 0 | [0, 450], [450, 900], [900, 1000] |
| S4 | 12.5 → 950 | 450 / 10 / 100 / 0 | [12.5, 460], [460, 850], [850, 950] |
| S5 | 0 → 900 | 450 / 10 / 100 / 10 | [0, 450], [440, 810], [800, 900] |
| S6 | 0 → 60 | 450 / 10 / 100 / 0 | [0, 60] (short, but ≤ C, so not split) |

### Rows

- A slot's **cost** is its length (End − Start). Every slot after the first in a row also costs T + G.
- A row's `UsedM` is the sum of its slots' costs, and must be ≤ C.
- `OffsetM` is where a slot's graph starts in its row: the costs of the slots before it, plus T + G when it is not the first.
  The first slot's offset is 0.
- Every piece of a split view starts a new row. The view or piece after the last piece may share that row if it fits.
- Rows fill sheets top to bottom. A new sheet starts after `RowsPerSheet` rows.

Order of placement:
- **Preserve** (the default) is next-fit: items in `Order`, then `Key` (ordinal). Each item goes in the current row if it fits,
  otherwise in a new row.
- **FirstFitDecreasing:** pieces of split views go first, in `Order`, each on a new row. Then the unsplit views, longest first,
  then by `Order`, then `Key`. Each goes in the first row (in creation order) with room, or a new row.

| Code | Severity | Effect |
|---|---|---|
| `packing.rules.invalid` | Error | Empty plan: a value not finite or out of range |
| `packing.rules.infeasible` | Error | Empty plan: the feasibility rule or T + G < C fails |
| `packing.item.invalid` | Error | Item skipped: stations not finite, or E ≤ S |
| `packing.item.duplicateKey` | Error | The second and later items with that key are skipped (ordinal, ignoring case) |

No items gives an empty plan with no issues.

**Regression case (sheet 434132).** Preserve, C = 450, T + G = 50, items in this order: 313.56, 83.54, 54.03, 59.20, 108.21,
44.19 m.
- Row 1: 313.56 and 83.54, `UsedM` 447.10.
- Row 2: 54.03, 59.20, 108.21 and 44.19, `UsedM` 415.63.
- Both rows on sheet 1.

## 7. Engine B: DIM stations (Codex)

Chooses the vertical DIMs of one profile view. Pure.

```
static DimPlan DimStations.Plan(DimInput input, DimRules rules)

DimInput
  IReadOnlyList<StationElevation> DuctTop                       // outside top of the duct, vertices
  IReadOnlyList<StationElevation> DuctBottom                    // outside bottom, vertices (from the parts' outer heights)
  IReadOnlyList<IReadOnlyList<StationElevation>> ProposedSurface // continuous runs; a gap in the surface is a break between runs
  IReadOnlyList<StructureSpan> Structures                       // Key, StartStationM, EndStationM: the structure's outer faces
  IReadOnlyList<CrossingPipe> Crossings                         // Key, StationM, TopElevM, BottomElevM (outside)

DimRules
  double MajorGradeChangePct = 2.0
  double MergeWithinM        = 5.0
  double MinSpacingM         = 15.0
  bool   AtStructureFaces    = true
  double MinClearanceM       = 0.30

DimPlan(IReadOnlyList<PlannedDim> Dims, IReadOnlyList<OperationIssue> Issues)
PlannedDim(DimKind Kind /* Cover | Clearance */, double StationM, double LowerElevM, double UpperElevM, double ValueM,
           string SourceKey, string Reason /* "structureFace" | "gradeBreak" | "crossingAbove" | "crossingBelow" */)
```

### Validation

- Each polyline (DuctTop, DuctBottom and each surface run) needs at least 2 vertices, with stations strictly ascending.
- Two vertices within 1e-6 m of each other with the same elevation (to 1e-6) are merged.
- With different elevations they are a vertical step, which is an Error: `dims.input.verticalStep`.
- Interpolation is linear between neighbouring vertices. A station outside a polyline's range has no value there.

### Cover DIMs

1. **Grade breaks.**
   - Candidates are DuctTop's interior vertices, in station order.
   - Grade is Δelevation ÷ Δstation × 100.
   - **Clustering is by span, not by chaining.** A cluster starts at a vertex. The vertices after it join while their station
     is ≤ the cluster's first station + `MergeWithinM`. The next vertex starts a new cluster. So stations 0, 4, 8 with
     `MergeWithinM` = 5 give {0, 4} and {8}.
   - A cluster's change is |grade after its last vertex − grade before its first vertex|.
   - Its station is the vertex with the largest single |Δgrade|; on a tie, the lower station.
   - It is major when the change ≥ `MajorGradeChangePct`.
2. **Structure faces** (when `AtStructureFaces` is set): each face station that lies strictly inside DuctTop's range.
3. **Value.** At each candidate station, `ValueM` = surface − duct top.
   - No surface at that station: no DIM, Warning `dims.surface.missing`.
   - `ValueM ≤ 0`: no DIM, Error `dims.cover.negative`.
4. **Spacing.**
   - Sort the candidates: structure faces first, then the larger grade change, then the lower station.
   - Accept each one unless an accepted cover DIM lies closer than `MinSpacingM` (strictly less).
   - Each dropped candidate gives an Info `dims.cover.spacing`.

### Clearance DIMs

One per crossing whose station lies inside DuctTop's range. A crossing outside the range gives an Info
`dims.crossing.outsideView`.

| Case | DIM | Reason |
|---|---|---|
| Crossing bottom ≥ duct top | Duct top → crossing bottom | `crossingAbove` |
| Crossing top ≤ duct bottom | Crossing top → duct bottom | `crossingBelow` |
| Otherwise | No DIM, Error `dims.clearance.clash` | — |

- A value < `MinClearanceM` keeps its DIM and adds a Warning `dims.clearance.belowMinimum`.
- Clearance DIMs never take part in cover spacing.

**Output order:** by station, then Cover before Clearance, then `SourceKey` (ordinal).

| Code | Severity | Effect |
|---|---|---|
| `dims.rules.invalid` | Error | Empty plan: a value not finite, a grade change ≤ 0, or a negative merge distance, spacing or clearance |
| `dims.input.invalid` | Error | Empty plan: fewer than 2 vertices, stations not ascending, or a value not finite |
| `dims.input.verticalStep` | Error | Empty plan |
| `dims.crossing.invalid` | Error | Crossing skipped: not finite, or bottom > top |
| `dims.surface.missing` | Warning | DIM skipped |
| `dims.cover.negative` | Error | DIM skipped |
| `dims.cover.spacing` | Info | Candidate dropped |
| `dims.crossing.outsideView` | Info | Crossing skipped |
| `dims.clearance.clash` | Error | No DIM |
| `dims.clearance.belowMinimum` | Warning | DIM kept |

**Adapter note (Claude).** Values are true metres. In profile view coordinates the vertical scale is exaggerated 10×. The
adapter therefore sets each DIM's linear factor to 1 ÷ exaggeration, and checks that the displayed text equals `ValueM` to
0.01 m.

## 8. Engine C: continuation notes (Codex)

Finds which plan sheets continue from each edge of a plan sheet. Pure.

```
static ContinuationPlan ContinuationNotes.Resolve(IReadOnlyList<SheetArea> sheets, string sheetKey, ContinuationRules rules)

SheetArea(string Key, string DrawingNumber, IReadOnlyList<Point2> ModelBoundary)  // plan viewport outline, model metres, closed
ContinuationRules(double ToleranceM = 0.05, double MinSharedM = 1.0)
ContinuationPlan(IReadOnlyList<ContinuationEdge> Edges, IReadOnlyList<OperationIssue> Issues)
ContinuationEdge(string NeighbourKey, string NeighbourDrawingNumber, IReadOnlyList<Point2> PathM, Point2 MidpointM, double SharedLengthM)
```

**The shared boundary with a neighbour B** is every part of the sheet's own boundary that lies inside B, or within
`ToleranceM` of B's boundary.
- This covers sheets that just touch at a match line and sheets that overlap a little.
- Each **connected** part of the shared boundary is its own edge. Two parts closer than `ToleranceM` along the boundary count
  as one. So B can give two edges when the boundaries meet in two places.
- An edge shorter than `MinSharedM` is dropped. A neighbour that only touches at a corner therefore gives no edge.
- `PathM` follows the sheet's boundary in its vertex order. `MidpointM` is halfway along `PathM`.
- Edges are ordered by neighbour drawing number (ordinal), then by distance along the boundary from its first vertex.

A boundary is invalid when it has fewer than 3 distinct points, zero area, a non-finite point or crossing edges.

| Code | Severity | Effect |
|---|---|---|
| `continuation.rules.invalid` | Error | Empty plan: tolerance < 0, `MinSharedM` ≤ 0, or a value not finite |
| `continuation.sheet.notFound` | Error | Empty plan |
| `continuation.boundary.invalid` (the sheet itself) | Error | Empty plan |
| `continuation.boundary.invalid` (a neighbour) | Warning | That neighbour skipped |
| `continuation.key.duplicate` | Error | Later duplicates skipped |

## 9. Engine D: placement score (Codex)

Scores **an arrangement that has already been planned**. The score takes the placements explicitly; it does not read them from
`ArrangeCase` alone.

```
static PlacementScore PlacementScoring.Score(PlacementInput input)
static PlacementInput PlacementScoring.FromPlanned(ArrangeCase arrangeCase, IReadOnlyList<PlanLabel> planned)
static ScoreComparison PlacementScoring.Compare(PlacementScore baseline, PlacementScore candidate)

PlacementInput(IReadOnlyList<Point2> ViewportMm, double MarginMm,
               IReadOnlyList<ScoredObstacle> Obstacles,  // Group (Text | Leader | Network), Network, outline (2 points = segment)
               IReadOnlyList<ScoredLabel> Labels)
ScoredLabel(string Key, Point2 AnchorMm, IReadOnlySet<string> KeepClearOf /* networks */,
            bool Placed, Box2? BoxMm, Point2? LeaderStartMm, Point2? LeaderEndMm)

PlacementScore(int Unplaced, int Overlaps, int OutsideViewport, int LeaderCrossings, int LeaderOverText,
               double MeanLeaderMm, double MaxLeaderMm)
ScoreComparison(bool NoWorse, IReadOnlyList<string> Worse /* metric names */)
```

`FromPlanned` maps each `PlanLabel`:
- `Spot == null` → not placed.
- Otherwise `Spot.Box` → box, `Start` → leader start, `Spot.End` → leader end.

It takes the obstacles, viewport and margin from the case. `PlanLabel` and `ArrangeCase` are plain types with no Autodesk API,
so this stays a pure engine.

### Metrics

Placed labels only, except `Unplaced`. An overlap needs a shared area > 1e-6 mm². Touching edges do not count. Boxes are not
enlarged by the gap.

| Metric | Counts |
|---|---|
| `Unplaced` | Labels with `Placed = false` |
| `Overlaps` | Pairs of placed label boxes that overlap, plus label–obstacle pairs where the box overlaps or crosses the obstacle. Network obstacles count only for networks in the label's `KeepClearOf`. Leader obstacles count against the box, as `LabelLayout` treats them |
| `OutsideViewport` | Labels with a box corner that is not inside the viewport by at least `MarginMm` (`ArrangeCase.Inside`) |
| `LeaderCrossings` | Pairs of leaders that cross at a point that is not an endpoint of both |
| `LeaderOverText` | Leaders that cross a Text obstacle or another label's box |
| `MeanLeaderMm`, `MaxLeaderMm` | Leader lengths (0 when no label is placed) |

### No worse

`Compare` reports a metric as **worse** when:
- any of the five counts is higher than the baseline, or
- `MeanLeaderMm` is higher than baseline × 1.10 + 0.5 mm.

`NoWorse` is true when no metric is worse.

### Baselines and gate

- Baselines are saved in `tests/C3D_Pro_Tools.Tests/Snapshots/placement-scores.json`, one score per case file.
- `Cases\` is **empty today**. In `feature/sheet-scene`, Claude exports one case per viewport of the tile 124 sheets, with
  Annotation Studio's "Export arrangement case".
- Until those cases exist, Codex tests with hand-built cases.
- **Phase 1 gate:** every tile 124 case scores 0 unplaced, 0 overlaps and 0 outside the viewport. Every other case is no worse
  than its baseline.

## 10. Build operation and service (Claude; Antigravity builds against it)

These types are frozen in `feature/sheets-contracts` before any parallel work starts.

```
enum BuildStep { PlanLabels, ProfileViews, ProfileLabels, Dims, SheetData, Arrange }   // also the order of execution
enum StepState { NotApplicable, NotBuilt, Built, OutOfDate, HasIssues }
enum ChangeKind { Add, Update, Remove }
enum ChangeTarget { Label, ProfileView, Viewport, Dim, ZoneMark, Text, TitleAttribute, KeyPlan, Pin }
enum BuildItemKind { PlanSheet, ProfileSheetSet }

BuildSettings(PackingRules Packing, DimRules Dims, ContinuationRules Continuation, string PavementLayers,
              double MinAlignmentShownM)
BuildSheetsRequest(DrawingIdentity Drawing, IReadOnlyList<string> Layouts, IReadOnlySet<BuildStep> Steps,
                   LabelRuleSet Rules, BuildSettings Settings)

PlannedChange(string ChangeId, BuildStep Step, ChangeKind Kind, ChangeTarget Target, string Layout, string RuleId,
              string SourceKey, DrawingObjectRef? Existing, string Description)
BuildItem(string ItemKey, BuildItemKind Kind, IReadOnlyList<string> Layouts, IReadOnlyList<PlannedChange> Changes)

BuildSheetsPreview(string PreviewId, DrawingIdentity Drawing, string SceneStamp, BuildSheetsRequest Request,
                   IReadOnlyList<BuildItem> Items, IReadOnlyList<OperationIssue> Issues)
BuildSheetsApplyRequest(BuildSheetsPreview Preview, IReadOnlyList<string> ItemKeys /* empty = all items */)

BuildItemOutcome(string ItemKey, ItemStatus Status, int Added, int Updated, int Removed,
                 IReadOnlyList<DrawingObjectRef> Created, IReadOnlyList<OperationIssue> Issues)
BuildSheetsResult(OperationStatus Status, string OperationId, string PreviewId,
                  IReadOnlyList<BuildItemOutcome> Items, IReadOnlyList<OperationIssue> Issues)

BuildProgress(BuildPhase Phase /* Reading | Planning | Applying */, int Done, int Total, string CurrentItemKey)

SheetStatus(string Layout, SheetKind Kind, string Tile, string Part, string DrawingNumber,
            IReadOnlyDictionary<BuildStep, StepState> Steps, int IssueCount)
SheetOverview(DrawingIdentity Drawing, IReadOnlyList<SheetStatus> Sheets, IReadOnlyList<OperationIssue> Issues)

interface ISheetProductionService
{
    DrawingIdentity ActiveDrawing { get; }                       // null when no drawing is open
    event EventHandler ActiveDrawingChanged;
    Task<SheetOverview> GetOverviewAsync(DrawingIdentity drawing, CancellationToken ct);
    Task<BuildSheetsPreview> PreviewAsync(BuildSheetsRequest request, IProgress<BuildProgress> progress, CancellationToken ct);
    Task<BuildSheetsResult> ApplyAsync(BuildSheetsApplyRequest request, IProgress<BuildProgress> progress, CancellationToken ct);
    Task<BuildSheetsPreview> PreviewPinAsync(DrawingIdentity drawing, IReadOnlyList<DrawingObjectRef> labels, bool pin, CancellationToken ct);
    Task ZoomToAsync(DrawingIdentity drawing, string layout, DrawingObjectRef? target, CancellationToken ct);
}
```

### Threading

- The service runs all drawing work on the AutoCAD main thread, through the document's command context. The UI never touches
  Autodesk types.
- Tasks complete on the caller's synchronization context.
- A second Preview or Apply while one is running is **rejected** with `sheets.busy`, not queued.

### Cancellation

| When | Effect |
|---|---|
| During Preview | Nothing in the drawing changes. Returns status `Cancelled` with no items |
| During Apply | Checked **between items** only. The current item finishes or rolls back. Items not reached are `NotAttempted`. Status `Cancelled`, and the result lists the committed items |

### Atomicity

`PerItem`. An item is one plan sheet, or one tile's set of profile sheets, because packing spans several sheets. Each item
commits whole or rolls back whole.

### Stale previews: what the `SceneStamp` covers

The stamp is a SHA-256 over a canonical serialisation of:
1. the whole `SheetScene` read for the request
2. the request's `Rules`, including every rule's fields
3. its `Settings`
4. the name and drawing handle of every label style, band set and DIM style the plan uses
5. the contract version

Apply re-reads the scene for the same request and recomputes the stamp. A different drawing gives `Rejected`
`sheets.preview.wrongDrawing`. A different stamp gives `Rejected` `sheets.preview.stale`. In both cases nothing changes, and the
UI asks for a new preview.

### Running again

A build with an unchanged scene, rules and settings gives `NoChange`. Only tool objects that are not pinned are updated or
removed.

### Build records and OutOfDate

Each committed item writes a record per sheet and step: an XRecord in the named object dictionary under `C3DPRO_BUILD`, holding
the hash of that step's inputs. `GetOverviewAsync` recomputes those hashes:
- a different hash means `OutOfDate`
- no record means `NotBuilt`
- current issues mean `HasIssues`

## 11. Workspace and UI (Antigravity)

Antigravity builds against `ISheetProductionService` (section 10) and `SyntheticSheetProduction`, which Antigravity writes from
tile 124's inventory:
- 4 plan sheets (124-A to D) and 2 profile sheets
- every step state and issue severity at least once
- a slow Preview, so progress and cancellation can be tried

The palette:
- **WPF**, hosted in a `PaletteSet`.
- One tab per stage; only the Document tab is live in phase 1.
- Shared colours and fonts follow `StyleUi`.

The **Sheets panel** has:
- a sheet list with a state per step
- Build selected: Preview, showing the items and their changes, then Apply with the chosen items
- progress and Cancel
- an issues list with Zoom and Fix (Fix previews a build of that sheet's step)
- Pin and Release for selected labels

The **ribbon** gets one panel per stage plus command search. The old command names stay.

## Open questions

| # | Question | How it gets answered |
|---|---|---|
| Q1 | Are the plan joint labels pipe end labels, or structure labels on null structures? | Claude reads the tile 124 DWG through the bridge, read-only |
| Q4 | What counts as a *major* grade break (default 2 %), and how close may two cover DIMs be (default 15 m)? | Fit the defaults to the DIMs on sheet 434132 through a bridge read, then the user confirms |
| Q5 | DIM style name and text format ("1.67m") | Bridge read of an existing DIM |
| Q7 | Are the station labels (`ict.plan.stations`) an alignment label set, or separate labels? | Bridge read |

Closed in v0.1: Q2 (cable notes are out of scope), Q3 (below the duct: duct bottom to pipe top), Q6 (450 m rows at 1:750).

## Revision 2: answers to review 1

| # | Finding | Change |
|---|---|---|
| 1 | high · Placement scoring has no placement data | Section 9: `PlacementInput` carries explicit placements, and `FromPlanned` builds it from planner output. Every metric is defined, "no worse" is defined, baselines go in a snapshot file, and the empty `Cases\` folder is filled in `feature/sheet-scene` |
| 2 | high · Packing rules conflict for short remainders | Section 6: the split arithmetic is defined (absolute rounding, the overlap inside capacity, moving the split back, no "share evenly"), with feasibility rules, `packing.rules.infeasible` and six worked examples |
| 3 | high · The shared operation contract is incomplete | Section 10: `BuildSheetsResult`, `PlannedChange`, items, progress, cancellation, threading, the `ISheetProductionService` signatures and the `SceneStamp` coverage (scene, rules, settings, styles, contract version) are all defined. They are frozen in `feature/sheets-contracts` before parallel work |
| 4 | medium · Continuation errors cannot be returned | Section 8: returns `ContinuationPlan` with issues. Disconnected shared parts give separate edges. Overlapping sheets are handled |
| 5 | medium · DIM clustering is ambiguous | Section 7: clustering by span with the 0/4/8 example. Interpolation, surface gaps, duplicate stations, vertical steps, rule validation and every issue code are defined |
| 6 | medium · Pinning has no tolerance | Section 4: the model frame, the recorded fields, the tolerances, a persistent `Pin` flag, Pin and Release actions, and duplicate tags from copied objects |
| 7 | medium · Phase 1 needs crossings that were deferred to phase 3 | Section 3: a limited phase 1 crossing reader, assigned to Claude, reusing the Cover Optimizer's crossing code. Duct top and bottom come from `DimInput.DuctTop` and `DuctBottom` |
| 8 | medium · The delivery sequence omits work | Section 5 maps every inventory item to a step, an owner and an acceptance check, including alignment labels and pavement marks. `feature/placement-score` and the case export are scheduled (section 1; roadmap) |
