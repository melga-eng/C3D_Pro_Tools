# Preset runs: C3DPROPV and C3DPRONET

Two commands that run the plugin's steps without dialogs, on many objects at once, with settings you keep in a file next to
your drawing. Ribbon: **C3D Pro Tools → Preset Runs**.

Always start with **Report**: it changes nothing and tells you what each step would do. Every other step is one UNDO.

## The settings file

`C3DPro.project.json` in the same folder as the drawing. Only the settings you want to change need to be there; the rest use
the defaults below. Example for the tile 124 drawing:

```json
{
  "profileViews": {
    "alignments": "ICT-ALI-124*",
    "matchTemplate": "ICT_PROFILE_VIEW_SETTINGS",
    "existingSurface": "COMBINED NGL TILL 21-06-2026 AND NGL NOT SUBMITED",
    "proposedSurface": "CE_PROP-CONT-NCRD"
  },
  "networks": {
    "networks": "ICT-*,!*-EQUIP",
    "surface": "CE_PROP-CONT-NCRD"
  }
}
```

The first line of every run says where the settings came from. If it says `defaults: ... has no "networks" section`, the
file is there but the section is missing, so add it.

Names take wildcards (`*`, `?`), several separated by commas; a name starting with `!` is left out. When the command asks for
alignments, networks or layouts, press Enter to use the file's value or type others for this run only.

You may leave out a name's prefix: when `124*` matches nothing, the tool takes the names that have a part starting with 124
(`ICT-ALI-124-1-89-01`, not `ICT-ALI-1124-01`), lists them, and before a step that changes the drawing asks `Continue` (Enter
is Yes). If still nothing matches, it lists some of the drawing's names.

## C3DPROPV: profile views

| Step | What it does |
|---|---|
| Report | TOP status per alignment, what Match would change, the DIMs each view would get, the drawing's surfaces |
| Top | Refits each TOP profile that no longer follows its ducts (`createMissingTop: true` also creates missing ones) |
| Match | Applies a Profile View Tools template (`matchTemplate`) or a finished view (`matchSourceView`) to the views |
| Dims | Draws cover and clearance DIMs; replaces the tool's own DIMs of an earlier run, never hand-drawn ones |
| All | Top, Match, Dims |

| Setting | Default | Meaning |
|---|---|---|
| `alignments` | `*` | Which alignments |
| `existingSurface`, `proposedSurface` | names saved in Profile View Tools | Ground surfaces for bands and cover DIMs |
| `matchTemplate` / `matchSourceView` | none | What Match copies from |
| `matchOthers` | `Hide` | Profiles with no role: `Hide`, `Leave` or `Delete` (Delete only when both surfaces are found) |
| `matchElevation` | `Widen` | Level ranges: `Widen` keeps each view's own range and only widens the side that cuts off a profile, duct or structure; `Fit` refits every view; `Leave` never touches them |
| Pavement (step) | — | Labels each start and end of pavement in the views (station-elevation labels on the proposed ground), from the `pavement` section below; a re-run replaces the tool's own labels |
| `topPattern` | `TOP` | Text that marks a TOP profile name |
| `createMissingTop`, `topStyle`, `topLabelSet`, `topLayer`, `topNameTemplate` | off | For creating missing TOP profiles |
| `dims` | as Profile DIMs | `dimStyle`, `layer`, `cover`, `clearance`, `minSpacingM`, `majorGradeChangePct`, ... |

Safety: Match refuses when the drawing lacks a style the template uses. Dims refuses when cover DIMs are on and the proposed
surface is not found (it would erase earlier cover DIMs and draw none). Both commands refuse non-metre drawings.

## C3DPRONET: networks

| Step | What it does |
|---|---|
| Report | What Repair, Rims and Fit would change, per network |
| Repair | Rebuilds pipes that dropped out of their network's member list |
| Rims | Sets structure rims to `surface` (+ `aboveSurface`) |
| Fit | Trims or extends straight pipe ends to their structures' `fitFace` (InnerWall, OuterWall, Center), connecting loose ends inside a structure |
| Duplicates | Deletes true copies inside each network (same place, levels, family and size); copies with labels or profile views stay |
| Cover | Checks cover, slope, crossings and duct type along every route; changes nothing |
| All | Repair, Duplicates, Rims, Fit, Cover |

| Setting | Default | Meaning |
|---|---|---|
| `networks` | `*` | Which networks; data-shortcut networks are always left out |
| `surface` | none | Surface for rims; without it Rims is skipped |
| `aboveSurface` | 0 | Rim above the surface (m) |
| `rimMaxChange` | 1.0 | A rim that would move more than this (m) is left alone and listed |
| `fitFace` | `InnerWall` | Face pipe ends are fitted to |
| `fitBoxLength`, `fitBoxWidth` | 0 | Both above 0: fit to a box of that size instead of the structure's own |
| `fitMaxMove` | 3.0 | An end that would move more than this (m) is left alone: its structure connection is wrong |
| `connectLooseEnds` | true | Connect pipe ends lying inside a structure |

## C3DPROLBL: labels (work in progress)

| Step | What it does |
|---|---|
| Report | What Add would add, and every label shown on the layouts with an overlap or outside its viewport |
| Add | Adds the labels the project's label rules (`labelRules`) ask for on the chosen layouts; a re-run adds nothing new |
| Arrange | Places the labels of the chosen networks clear of each other and inside their viewports; locked labels stay |
| Reset | Puts the labels of the chosen networks back where their style places them; locked labels stay |

| Setting (`labels`) | Default | Meaning |
|---|---|---|
| `layouts` | `*` | Which layouts (sheets); wildcards, `!` to leave out |
| `networks` | `*` | Networks whose labels may move; the others stay and count as obstacles |
| `maxDistance`, `gap`, `margin` | as saved in Annotation Studio | Longest leader, clear space round a label, space inside the viewport edge (paper mm) |
| `ruleSet` | the only one | Which `labelRules` set Add uses |

Viewport scales and twists are never changed. On tile 124 sheet 102054 Arrange brings the problems from 30 to about 7; it
is not yet clean on crowded sheets.

## Pavement (both commands)

Each pavement kind is a surface that covers exactly its paved area (a data reference is fine). The first one listed wins where
two overlap; everywhere else is unpaved. `C3DPRONET Cover` checks each duct against the cover of the pavement over it and its
duct type (type B under taxiway fails; type A where B is asked is only noted); `C3DPROPV Pavement` labels the edges.

```json
"pavement": {
  "zones": [
    { "surface": "KSIA_TAXIWAY-PAVEMENT", "name": "Taxiway Pavement", "minCover": 1.50, "ductType": "A" },
    { "surface": "KSIA_ROAD-AIRSIDE", "name": "Road Pavement", "minCover": 0.64, "ductType": "B" }
  ],
  "unpaved": { "name": "Unpaved", "minCover": 0.60, "ductType": "B" }
}
```

A zone may set `maxCover`; without it the general `coverMax` applies, except under a zone whose minimum is already at or
above it (taxiway), where there is no maximum. The labels use the styles `startLabelStyle` / `endLabelStyle` when set (a zone
may set its own); otherwise the tool makes "C3DPRO Start Of Road Pavement" and so on from the drawing's station-elevation style
(`baseLabelStyle`), reading "Start Of Road Pavement / CH:76.184". The duct type comes from the duct name (IC-124-IC3A-06 is A)
or, failing that, from "Reinforcement" in its size.

Each run writes its full report to `%TEMP%\C3DPROPV-<step>.txt` or `%TEMP%\C3DPRONET-<step>.txt`; the command line shows the
summary and the file path.
