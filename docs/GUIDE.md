# C3D Pro Tools: how to use it

One tab, **C3D Pro Tools**, five panels: Model, Profiles, Produce, Standards, Command Runs. Nothing needs a file beside the drawing: each tool keeps
its settings in the drawing or remembers your last choices.

## Model: build and fix the networks

| Button | Use it to |
|---|---|
| **Network Editor** | Split, merge, swap parts, fit pipe ends to structures, rims to surface |
| **Network Organizer** | Organize parts and networks |
| **Networks from Feature Lines** | Build a pipe network from feature lines |
| **Profile & Cover Designer** | Set pipe levels along a route for cover, slope and clearances |
| **Navisworks Importer** | Import pipes and structures from Navisworks |

## Produce: profile views, labels, sheets

| Button | Use it to |
|---|---|
| **Pro Run** | One window, four tabs. **Profile views**: Fit TOP, Match views, Split long views, Cover DIMs, Pavement labels (on the proposed profile), or All. **Networks**: Repair, Delete duplicates, Rims, Fit pipe ends, Cover check, or All. **Labels**: Add (label rules), Arrange, Reset, Rename. **Pavement**: the pavement surfaces, their cover and duct type, and the shoulder band. Tick what to work on, press a step. **Check** shows what would change and changes nothing. |
| **Annotation Studio** | Add labels from your label sets (a row can be left out where a pipe starts or ends at a manhole), arrange them in the viewports, reset, rename parts with templates |
| **Sheet Manager** | List, rename, copy and create layouts by tile and part |

Every change is one UNDO step. Data references are never changed.

## Off the ribbon (still work as typed commands)

`StyleManager`, `C3DDrawingCleaner`, `ProfileViewTools`, `PROFILEMGR`, `PVMGR`, `STYLEMGR`, `PROFILEDIMS`, `C3DPROPV`,
`C3DPRONET`, `C3DPROLBL`, `C3DProWorkspace`. Their jobs are in Pro Run or not used now.

## What is being done now

Release 3 (labels) waits for your try: install, then in the test drawing run **Pro Run > Labels > Arrange** on sheet
102054 and **Annotation Studio > Add labels** with a row left out at manholes. Then sheets (release 4). New ideas go to the
"Later" list in ROADMAP.md, not into the tool.
