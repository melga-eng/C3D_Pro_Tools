# Generate Conduits: contract proposal (phase one)

Status: **revision 2, for Codex review.** It includes the fixes for the three findings from review 1 and the six phase-one decisions; see "Revision 2" at the end. No engine, adapter or form changes until accepted.
Branch: `feature/conduit-templates-contracts` from `main` at be5c70c. Contract owner: Claude.

Inputs: the MV/ICT drawing summary (`MV-ICT-Duct-Template-Summary.md`), Codex's conduit audit of be5c70c, `ARCHITECTURE.md`, and a read-only look at a populated ICT model (below). The PDFs remain the source of truth for every dimension.

## Files

All in `C3D_Pro_Tools/Features/Networks/Contracts/Conduits/`, namespace `C3DProTools.Features.Networks.Contracts`, reusing `DrawingIdentity`, `DrawingObjectRef`, `Point3`, `OperationIssue`, `OperationStatus`, `ItemStatus`, `Atomicity` and `Frozen` from the split pilot.

| File | Holds |
|---|---|
| `ConduitTemplates.cs` | `ConduitTemplate`, `ConduitArrangement`, `ConduitSection`, `ConduitDesignReference`, `ConduitCapacity`, `ConduitSourceReference`, `ConstructionVariant`, `ConduitPartMapping` |
| `ConduitTemplateLibrary.cs` | The seven standard templates |
| `ConduitRequest.cs` | `ConduitRequest`, `ConduitEndRules`, `StructureInnerBox`, `ConduitEndOverride`, `ConduitPartChoice`, `ConduitLimits`, enums |
| `ConduitPlan.cs` | `ConduitPreview`, `ConduitSourcePlan`, `ConduitEndPlan`, `ConduitFitCheck`, `PlannedConduit`, snapshots, `ConduitApplyRequest`, `ConduitProgress` |
| `ConduitResult.cs` | `ConduitResult`, `ConduitItemOutcome`, `IConduitOperation` |
| `ConduitValidation.cs` | Issue codes, template, library, request and apply validation, section maths, end resolution, stale comparison |

Tests: `tests/C3D_Pro_Tools.Tests/ConduitContractsTests.cs` (59 cases).

## Separation the summary asked for

| Concern | Type | Affects geometry? |
|---|---|---|
| Arrangement: columns, rows, pitches | `ConduitArrangement` | Yes, the only one that does |
| Default placement (centred or bottom-referenced, side shift) | `ConduitSection` (request may override) | Yes |
| Stated design values: OD, gap, edge clearance, envelope | `ConduitDesignReference` (nullable = not established) | No, checks only |
| Subducts, spare capacity, material | `ConduitCapacity` | No, metadata |
| Construction variants (1A/1B…, Type 01…) | `ConstructionVariant` | No, recorded only |
| Drawing number, revision, sheet, label and label order | `ConduitSourceReference` | No |
| Drawing parts | `ConduitPartMapping` (names, reusable) → `ConduitPartChoice` (resolved per drawing) | Size used for checks |

Labels are stored as written ("3X2", "5 X 3") with `ArrangementLabelOrder` and are never parsed. Columns and rows are explicit. Templates contain no handles; a copy-from-drawing conduit is drawing-scoped and lives only in the request.

## Standard templates

| Key | Label (order) | C × R | Pitch across / up (m) | Stated OD | Envelope W × H (m) | Variants |
|---|---|---|---|---|---|---|
| ICT-2C-1R | 1X2 (R×C) | 2 × 1 | 0.160 / – | 0.110 | 0.470 × 0.310 | 1A, 1B |
| ICT-2C-3R | 3X2 (R×C) | 2 × 3 | 0.160 / 0.160 | 0.110 | 0.470 × 0.630 | 2A, 2B |
| ICT-4C-6R | 6X4 (R×C) | 4 × 6 | 0.160 / 0.160 | 0.110 | 0.790 × 1.110 | 3A, 3B |
| ICT-4C-3R | 3X4 (R×C) | 4 × 3 | 0.160 / 0.160 | 0.110 | 0.790 × 0.630 | 4A, 4B |
| ICT-1C-1R-50 | 1X1 (R×C) | 1 × 1 | – | 0.050 | 0.250 × 0.250 | 1C |
| MV-5C-3R | 5 X 3 (C×R) | 5 × 3 | 0.300 / 0.500 | not stated | not stated | Type 01, Type 02 |
| MV-5C-1R | 5 X 1 (C×R) | 5 × 1 | 0.300 / – | not stated | not stated | Type 03, Type 04 |

ICT: gap 0.050, edge 0.100. `ValidateTemplate` recomputes each envelope from grid + OD + 2 × edge and finds no mismatch. The tests check every offset against section 5 of the summary.

**Review notes carried on the templates and repeated in every preview:**
- MV conduit OD/wall unknown (callout "200 mm UPVC").
- MV envelope, edge clearance and placement reference are not established; centred is a default to confirm.
- ICT 1C edge clearance of 100 mm is inferred from 250 − 50.
- ICT Note 12 depths are in an unsupplied drawing, so no depth rule is applied.
- Spare capacity never removes ducts.

## Units

Conduit contracts are in metres (fields end in `M`). Phase one accepts metre drawings only: the adapter reads `MetresPerDrawingUnit`, and anything other than 1 is rejected (`conduits.units.notMetres`). Drawing settings are never changed. Coordinates (`Point3`) are drawing units, which are then metres.

## Request semantics and defaults

- **Template** is carried by value, so editing the library after preview cannot change a plan. `VariantId` (optional) must be one of the template's variants and is metadata only.
- **Section**: `SectionCentred` places the grid's middle on the centreline. `BottomReferenced` places the lowest row's centre `BottomToLowestCentreM` above the outside bottom. `SideShiftM` is + to the right of the run direction.
- **End rules.** Geometry and connectivity are separate, as requested.
  - `AtStructure` defaults to **InnerWall**; the alternatives are OuterWall, StructureCentre and EncasementEnd (the old behaviour).
  - `ConnectToStructures` defaults to **false**. When true, connection is *required*: a failed or unverified connection rolls back that encasement.
  - `AtJoint` defaults to **Mitre**; the alternative is EncasementEnd.
  - `ConduitEndOverride` changes treatment and/or connection for one end of one encasement.
- **Each conduit's own line** meets the wall: conduits at an offset into a round or rotated manhole stop at different stations. Ends that stop short of the wall are extended to it. Z keeps the encasement's grade plus the row height.
- **End classification** in preview, independent of drawing direction:
  - *Structure*: connected; or, only when `MatchUnconnectedEnds` is on (opt-in), by position: either inside the structure's actual outer face or within `StructureSearchM` (default 0.5 m, max 2 m). More than one qualifying structure blocks the item until the user picks one (`StructureChoices`).
  - *Joint*: another chosen encasement's start or end within 0.01 m, in any direction.
  - *Free*: otherwise; conduits keep the end point.
  
  Ends matched by position are reported (`conduits.item.structureByPosition`). Containment uses the real round or rotated footprint, never the bounding box (Codex finding, line 121).
- **Footprints**: in order, the user's `StructureInnerBox` for the part size name, then the part's inner size and wall, otherwise **Unknown**. Drawn extents are never used as an inner wall. With Unknown, `WhenFootprintUnknown` defaults to **LeaveOut**: the encasement gets no conduits and the preview names the structure size to give a box for. The alternative is KeepEncasementEnd, with a warning.
- **Runs and direction**: chosen encasements linked by joints form a run. Its direction is that of the first chosen member, and members drawn the other way are `Reversed`. Columns are counted left to right along the run, so column *n* stays column *n* across mitres. This answers Codex finding line 541. Runs do not continue through structures in phase one (see open questions).
- **Destination**: the encasement's network (default) or a new network. A copied (family-less) part stays in its template pipe's network. The request warns about this, and each item's `Destination` shows the network Civil 3D will actually use. Connection with a new network is rejected.
- **Fit**: every chosen encasement is checked at its own size (`CheckSection`), not the first one's (Codex finding, line 197). Overlapping conduits always leave the encasement out. Conduits crossing the outside edge leave it out unless `AllowConduitsOutsideEncasement` is set. An envelope or part diameter that differs from the template's stated value is a warning. Encasements are never resized.
- **Curved encasements** (phase-one rule, the same in `PlannedConduit` and here): conduits follow an offset arc with a signed bulge.
  - **At a structure**, only EncasementEnd without connection is supported. A wall or centre treatment, or a connection, at a curved end is an error for that item (`conduits.item.curvedEndUnsupported`); nothing is silently skipped (Codex finding, line 632).
  - **At a joint**, a mitre is made only between two straight encasements. At any joint where either side is curved, curved-to-straight included, both sides keep their encasement ends. The end plan records `JointTreatment.EncasementEnd` with an Info note (`conduits.item.curvedJointNotMitred`), so Preview and Apply treat the junction the same way.
- **More than two chosen encasements at one point** are not paired: each keeps its end (`conduits.item.jointNotPaired`).
- **Unsupported pipes** (segmented, flex) appear as items with an error.

## Preview, apply, result

- `ConduitPreview` lists **every** chosen encasement as a `ConduitSourcePlan`, fillable or not. Each one carries:
  - run and reversal;
  - both `ConduitEndPlan`s: kind, structure snapshot, how it was found, treatment, connection, overridden;
  - its `ConduitFitCheck`;
  - its destination network;
  - the exact `PlannedConduit` points (column, row, offsets, start, end, plan length);
  - its own issues.
  
  `CanApply` is false if the item has an error. Whole-request issues and the template notes are listed separately.
- **Atomicity: `PerItem`.** Each encasement is one unit: its complete array, plus any required connections, is committed together or not at all. One outer transaction per Apply, so a single UNDO is intended; that still has to be verified live. This answers Codex finding line 684.
- **Apply** first re-checks the drawing identity (a mismatch rejects everything). Then, per encasement, it compares the encasement, its end structures and its mitre neighbours with their snapshots; any change rejects that item (`conduits.stale.*`). It never plans again. Cancellation is checked between encasements only.
- **Results** count committed items only: `EncasementsFilled`, `ConduitsCreated`, and `ConnectionsMade` (verified connections only, which addresses the Parallel Copies "Joined" bug for this workflow). Statuses come from `OperationOutcome.Summarize`. When the outcome is unknown, the result says to read the drawing again before retrying.

## Live model findings (read-only, `...-MOD-IC-XX-000501.dwg`, 2026-11-08)

- **ICT encasement part sizes match the sheet**: 1x2 is 0.47×0.31, 3x2 is 0.47×0.63, 3x4 is 0.79×0.63, 6x4 is 0.79×1.11, and the 50 mm single is 0.25×0.25. All report outer size 0, wall 0 and no family, so the outer size comes from the inner size.
- **The model also has an "ICT 4x2 way" (0.47 × 0.79, 19 pipes) and an "ICT 6x4 Reinforcement" (0.79 × 1.10)**, neither on the supplied sheet. They are not made standard; see the open questions.
- **MV encasements are 1.7 × 1.5 (5x3) and 1.7 × 0.5 (5x1).** With centred placement and a 200 mm OD, both leave 150 mm edge clearance, which is consistent with the centred default but not proof of it.
- **ICT encasements connected to a manhole end at its centre** (median distance 0). Trimming to the inner wall therefore moves every connected end by about half the manhole size.
- **Every MV manhole ("MV Air Craft Manhole") and every ICT-CENTRAL manhole (e.g. "4,600.00 x 2,300.00 x 3,300.00") reports an inner size of 0** with a wall of 0.35–0.4. Their drawn extents are axis-aligned boxes of rotated structures, for example 5.28 × 6.09 for a 4.6 × 2.3 box at 120°. This is why the Unknown footprint state and `StructureInnerBox` exist.
- **Most encasement ends are not connected to any structure** (e.g. 1294 of 2140 ends in IC-PIP-Ductbank-NCRD_ICT are more than 1 m from any structure). They are mostly joints between segments, so joint handling in either direction matters.

## Phase-one decisions (formerly the open questions; Codex's recommendations, passed on by the user)

| # | Decision | Where it shows in the contract |
|---|---|---|
| 1 | "ICT 4x2 way" becomes a **custom project template**. The seven built-ins stay tied to the supplied sheets. | No standard entry; made with `CustomCopy` (e.g. from ICT-2C-3R with 4 rows) |
| 2 | MV: **centred placement** is an editable starting point. Fit is approved only with the resolved part's **actual** outside diameter, never the callout. | `ConduitPartChoice.OutsideDiameterM` = 0 is now an **error** (`conduits.part.diameterUnknown`) |
| 3 | Column numbering carries across joined encasements within a run and **restarts at manholes**. | Runs are made of structure-free joints only (unchanged) |
| 4 | Matching unconnected ends to nearby manholes is **opt-in**, with a 0.5 m limit. The preview shows each proposed match. If more than one manhole qualifies, the user must choose. | `MatchUnconnectedEnds` (default false), `StructureSearchM` 0.5, `ConduitEndPlan.Candidates`, `StructureChoices`, `conduits.item.structureAmbiguous` |
| 5 | Settings save custom templates, part mappings by name, and verified inner dimensions **scoped to project and part identity**. Existing Parallel Copies layouts stay usable during migration. | `StructureInnerBox.Project` / `PartFamilyName`; `InnerBoxFor` prefers a family match; duplicates are checked per family + size. Persistence is engine/settings work. |
| 6 | New conduits keep the **duct bank's layer** by default. | Adapter behaviour (unchanged from today) |

## Revision 2: review 1 findings

| Finding | Change |
|---|---|
| High, `ConduitValidation.cs:284`: an oversized conduit passes the fit rule (200 mm OD on 160 mm centres overlaps by 40 mm in a wide encasement) | **Request:** a part OD larger than any used pitch is an error, `conduits.part.overlap`, reported per axis. **Item:** `ConduitFitCheck.ConduitsOverlap`, and `ConduitValidation.FitIssues` makes overlap an **error whatever `AllowConduitsOutsideEncasement` says**. That option now relaxes only the outside-edge rule. The fit uses the resolved part's diameter (`ConduitSourcePlan.Part`). |
| High, `ConduitPlan.cs:84`: a curved preview cannot describe its arc (a bend and its mirror share ends and length) | `PlannedConduit` stores a **signed `Bulge`** (tan(sweep/4), + counter-clockwise, 0 for straight). `Curved` is derived from it. Apply makes exactly that arc without recalculating. |
| Medium, `ConduitValidation.cs:342`: no stale check of the resolved part or destination | Each item records `ConduitDestinationPlan` (network ref, or "created at apply") and a `ConduitPartSnapshot` (family key, size name, actual OD, template pipe). Before each encasement, `Compare` resolves both again: a different network, part, size, diameter or template gives `conduits.stale.destination` / `conduits.stale.part`. Adding the size to a network's parts list during Apply is expected and is not treated as a change. |

**Added acceptance case:** if one side of a mitred joint is not chosen, is rejected, or rolls back, the result says the run is incomplete. `ConduitValidation.IncompleteRuns` gives a `conduits.run.incomplete` warning per joint and names the encasement to fill. Conduits already made are kept, since each encasement is its own unit.

**First live pass (engine stage):** use a disposable metre-based drawing to verify:
- rotated-manhole inner-wall trimming;
- a reversed run (column numbering and side shift);
- rollback during conduit creation;
- one UNDO.

MV inner-wall trimming needs verified inner dimensions for each MV manhole size first.

## Engine review 1: rules made strict

| Finding | Rule now |
|---|---|
| High: a required connection could become optional (a copy-only part in another network) | `conduits.item.connectionImpossible` is an **error**, so the duct bank is blocked. The user changes the destination or part, or turns connection off for that end with an override. A connection is never dropped quietly. |
| High: mitred ends met in plan but could miss in elevation | Each paired joint is planned for both sides at once. Both conduits end at the **mean of their two grades** at the mitre point, so corresponding ends meet in X, Y and Z (tested on a sloped, reversed bend). If either side would move more than `MitreLevelToleranceM` (10 mm) from its own grade, the joint is flagged (`conduits.item.mitreLevel`, error) instead of stepping or bending the conduits. One cause is bottom-referenced rows in encasements of different heights. Keeping encasement ends at joints avoids it. |
| Medium: a conduit missing the requested wall kept its end | `conduits.item.wallMissed` is an **error** that names the conduit (column-row). The user changes the layout or side shift, or chooses Keep encasement end for that end. |

## Engine review 2: collinear joints

The finding: a paired joint whose two encasement ends were up to the joint tolerance apart (e.g. 5 mm) kept separate ends when the banks were collinear. It was reported as mitred while the conduits had a gap.

The fix: the mitre is computed once per pair, about a common joint point J (the midpoint of the two encasement ends), in a fixed order so both sides get exactly the same point. For a bend it is where the two offset lines through J cross. For collinear banks it is the midpoint of the two offset points at J. Mitred ends therefore always share one point in X, Y and Z. Regression tests:
- collinear banks 5 mm apart along the line;
- banks 5 mm apart sideways, with one reversed;
- a 31° bend 5 mm apart (within 10 mm of the true mitre).

The first fails on 391594d.

## Live pass (2026-11-09, `Test Copy.dwg`, a disposable copy of the ICT model)

The engine build was loaded in isolation (installed plugin untouched). Test run: four "ICT 6x4 way" duct banks (IC-PIP-TDB-NCRD-1345, -1350, -53, -1346) between two rotated 4.6 × 2.3 manholes, ICT-STR-TMHC-2_216 at 300.6° and ICT-STR-TMHC-2_155 at 262.2°. Three of the four segments are reversed and two slope steeply (about 8% and 6.7%). Settings: template ICT-4C-6R, defaults (inner wall, no connection, mitre), part copied from a 100 mm HDPE pipe (OD 0.110, no family).

| Check | Result |
|---|---|
| Preview | Succeeded, 96 conduits, no issues. The run is oriented, the three reversed segments are recognised, and both manhole footprints come from the part size. |
| Apply | Succeeded: 4 duct banks filled, 96 conduits, all registered members of their network, on the duct bank's layer, not connected. |
| Rotated inner-wall trim | Every manhole-end conduit lies on the inner face (2.300 m along the manhole axis), with an error of 0.000 mm at both manholes. |
| Sloped reversed run | At all 3 joints, the 24 matching conduit pairs meet in X, Y and Z with a 0.000 mm gap. |
| One UNDO | The whole Apply was one undo step: one UNDO past the bridge's own snippet step removed all 96 conduits and the network was back to 6 pipes. |
| Rollback after partial creation | A forced failure after a duct bank's 24 conduits were made: that bank was rolled back with nothing left, the next bank was kept (network 6 → 30), the result was PartiallySucceeded, and both incomplete-run warnings appeared. The test was undone afterwards. |

Not tested live: MV inner-wall trimming (needs verified MV manhole inner sizes), curved encasements, connection on, a new destination network.

## Not in this proposal

Engine (end fitting with arcs, mitre in either direction, endpoint index, footprint cache), Civil 3D adapter, application workflow, form and settings persistence, subduct modelling, reinforcement or civil section, burial depth. Parallel Copies keeps its current behaviour until phase two.
