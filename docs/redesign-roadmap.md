# C3D Pro Tools redesign: roadmap

> Replaced on 2026-11-11 by [ROADMAP.md](ROADMAP.md). Kept for the product structure and the tile 124 sheet inventory.

Status: **draft 1, for the user and Codex review.** Branch `feature/redesign-roadmap` from `main` at 1243c94.
Companion: [sheet-production-contract.md](sheet-production-contract.md) (the phase 1 contract proposal).

## Why

The plugin has ten separate tools, each a large form. Each one reads the drawing on its own and has its own settings and its own
way of selecting things. You have to know which tool holds the button you want. The work itself, producing shop drawings for dry
utility networks, runs across several tools and is repeated for every sheet and after every design change.

The redesign keeps the code and engines and reorganises the product around the work.

## Product

A production tool for **dry utility networks** (ICT, MV, LV, fence lighting, navaids, MET) in Civil 3D. It has three stages and
a base layer underneath them:

| Stage | Purpose | Existing tools that move here |
|---|---|---|
| **Model** | Build and clean the networks | Navis import, Network from feature lines, Network Editor and Generate Conduits, Repair, Parts to Profile |
| **Coordinate** | Check them against each other and the ground | Cover Optimizer, Profile & Cover Designer, network checks |
| **Document** | Shop drawing sheets: labels, profile views, dimensions, sheet data | Annotation Studio, Arrange Labels, Profile View Tools, Sheet Manager |
| **Standards** (base layer) | Styles, cleanup, project rules | Style Manager, Drawing Cleaner, submittal cleanup |

Ground rules:
- **One drawing at a time.**
- **Project-specific rules** (cover, clearances, label content) live in a project settings file, not in the code.
- **Nothing changes the drawing without a preview.** Every change can be undone, and labels the user pinned are never moved.
- **The user is an expert.** Prefer presets, selecting first and dense screens over wizards.

Out of scope for now:
- Quantities. The model keeps lengths, types and sections so quantities can be added later.
- Manhole geometry and duct bank sections. Manholes have fixed dimensions by type, and the sections are ready-made details.
- Tracking individual conduits. Conduits take the name of their duct bank.

## Foundations

1. **Drawing model.** One read of the drawing gives every feature the same picture:
   - networks, then runs (pull point to pull point), then parts
   - each run's alignment, profiles, profile views, labels and sheets
   - crossings with other networks
2. **Batch engine.** Every change follows the same steps: select, group, preview, apply, report. Each item succeeds or fails on
   its own (ARCHITECTURE.md, "Error and result conventions").
3. **Issues list.** A live list of problems: overlaps, labels outside the viewport, missing labels, outdated sheets, cover and
   clearance problems. Each issue has a fix, and issues of one kind can be fixed in a batch.
4. **Workspace.** One dockable palette with a tab per stage. An explorer on the left, actions for the current selection on the
   right, issues at the bottom. It replaces the separate forms one at a time.

## Phase 1: shop drawings for one tile, end to end

Annotation and drafting take the most time, so they come first.

**Target:** produce the labels, profile views, dimensions and sheet data of tile 124 in a **copy** of the submitted drawing, and
compare the result with the submitted sheets:

- plan: `D:\Pinnacle Working Files\000501\_PDF Submittals\ICT-Tile-124\V3\…-102054.pdf`
- profile: `…-434132.pdf`

**Acceptance:**
- Every item in the inventory below is present and has the right content.
- No label overlaps another label or the linework it must keep clear of.
- Every label sits inside its viewport.
- Labels the user pinned did not move.
- Running the build again with no model change changes nothing.
- After a model change, only the affected labels and views change.

### What the sheets contain (taken from the submitted PDFs)

Contract section 5 maps each item to a build step, an owner and an acceptance check.

| Sheet | Item | Made from | Phase 1 |
|---|---|---|---|
| Plan | Manhole label in a box with a leader: name, type, cover class, E, N, CL, inner dimensions, height | Structure label style | Yes |
| Plan | Duct joint label: name, type, size, IL, N, E (about 30 per sheet) | Pipe or joint label style | Yes |
| Plan | Alignment name label, chainage | Alignment labels | Yes |
| Plan | "FOR CONTINUATION SEE DRAWING …" at each edge | The neighbouring tile parts | Yes |
| Plan | Key plan highlight, legend, reference list, title block | Tile and part, network types on the sheet | Title block and key plan: yes. Legend: later |
| Plan | Revision clouds, RFI notes | The user | Stays manual; kept safe |
| Profile | One profile view per alignment, packed in rows | The alignments in the tile | Yes |
| Profile | View title: alignment name and scale | View | Yes |
| Profile | Six bands: chainage, EGL, FGL, TOD, FGL–TOD, NGL–TOD | Band set | Yes (existing recipe) |
| Profile | Manhole, segment (name + L), slope (S:%), joint (N, E, IL) labels | Label styles | Yes |
| Profile | Crossing utilities and their labels | Other networks in the drawing | Yes |
| Profile | **Clearance DIMs**: duct top to crossing pipe bottom (vertical) | Crossing geometry | Yes, new |
| Profile | **Cover DIMs**: duct top to proposed surface at major grade breaks (vertical) | Duct and surface profiles | Yes, new |
| Profile | Start and end of pavement with chainage | Pavement polylines | Yes (`ProfileViewZones` already draws these) |
| Profile | Structures from other networks, with base elevation | Other networks | Yes |

## Phase 2: workspace and the other networks

- The workspace's **Document** tab replaces Annotation Studio, Profile View Tools and Sheet Manager.
- Rule sets for MV, LV, fence lighting, navaids and MET, from their typical details in
  `D:\Pinnacle Working Files\_General Files\04-IFC\00-Scrapped PDF 6 Dry Systems\`.
- The issues list covers the Document stage.

## Phase 3: Model and Coordinate

- **Import grouping:** turn messy linework or Navis geometry into runs between pull points. The tool proposes groups and the user
  confirms them before anything is built.
- **Crossings engine:** every crossing between networks, with clearance and separation checks and conflict reports. Phase 1
  only has a limited reader: crossings in a profile view's station range, for the DIMs (contract section 3).
- Model health checks: cover, bend budget, separation, orphan and zero-length parts, data shortcut health.
- The Standards stage: a standards check and style sync from the project settings file.

## How the three agents work in parallel

Contracts come first. Claude writes them, Codex reviews them and the user approves. After that, each agent works on its own
branch and its own files from the same contract commit. The user merges every branch.

| Agent | Phase 1 work | Never touches |
|---|---|---|
| **Claude Code** | Contracts, Civil 3D adapters, label rules, orchestration (`BuildSheets`), live checks through the bridge | Antigravity's UI files, Codex's engine files while they are assigned to Codex |
| **Codex** | Pure-logic engines with unit tests (no Autodesk API, no UI): profile view packing, DIM stations, continuation notes, placement score. Reviews Claude's and Antigravity's branches | Adapters, forms, Autodesk calls |
| **Antigravity** | The workspace palette shell, the Sheets panel and the ribbon, built against the contracts with a synthetic data provider | Engines, adapters |

**Cross review:** Codex reviews Claude's and Antigravity's branches, and Claude reviews Codex's.

### Order of work

1. **Contract** (this branch, now v0.2): Codex reviews it and the user accepts it.
2. **Claude: `feature/sheets-contracts`.** Moves the shared types and adds every contract type and engine signature, with no
   bodies. Merged first; every branch below starts from that merge.
3. **In parallel:**
   - Claude: `feature/sheet-scene` (scene and crossing readers, the profile scale-text fix, exporting the tile 124 arrangement
     cases), then `feature/label-rules`
   - Codex: `feature/profile-sheet-packing`, `feature/dim-stations`, `feature/continuation-notes`, `feature/placement-score`
   - Antigravity: `ui/workspace-shell`
4. **Cross review:** Claude reviews each Codex branch, and Codex reviews Claude's and Antigravity's.
5. **Claude: `feature/build-sheets`** joins the engines into one operation.
6. **Live acceptance** on a copy of the tile 124 drawing, against the checks in contract section 5. Then review of the combined
   branches.

## Decisions

| Decision | Status |
|---|---|
| Workspace palette | **WPF**, hosted in a PaletteSet (user, 2026-11-10). The old WinForms forms stay until each is replaced |
| Profile sheets | Viewports at 1:750, two rows of 450 m each; the title block keeps 1:500 (user, 2026-11-10) |
| Cable-content notes | Out of scope; the user is removing them from the sheets (user, 2026-11-10) |
| Cover DIMs | At the manhole faces, and at major grade breaks: a slope change of 2 % or more, with cover DIMs at least 10 m apart (user, 2026-11-10) |
