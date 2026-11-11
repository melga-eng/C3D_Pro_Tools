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

Names take wildcards (`*`, `?`), several separated by commas; a name starting with `!` is left out. When the command asks for
alignments or networks, press Enter to use the file's value or type others for this run only.

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
| All | Repair, Rims, Fit |

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

Each run writes its full report to `%TEMP%\C3DPROPV-<step>.txt` or `%TEMP%\C3DPRONET-<step>.txt`; the command line shows the
summary and the file path.
