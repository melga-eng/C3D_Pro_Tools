# Roadmap

Goal: as many tools as possible working and worth using, as fast as possible. The code exists; the work is to prove each tool
on real drawings, fix what fails, cut the steps, and give everything one new look. Replaces `redesign-roadmap.md`.

**No new tools until release 4 is out.** New ideas go to "Later" at the bottom.

## How a tool gets released

For each tool, in this order, and nothing more:

1. **Run without the form.** The tool's operation runs from a settings file (this is also the preset you reuse later).
2. **Drawing test.** It runs in the background on a copy of a project drawing and its result is checked.
3. **Fix what the test shows.** Wrong results, errors, crashes.
4. **Cut the steps.** Last settings remembered, presets, select first, sensible defaults from `C3DPro.project.json`.
5. **You try it once** with the dev build, on your work. Your yes makes it Released.

A release is a new installer with every tool that reached step 5. You get one about every two working days and use whatever
is in it, even while the rest is still being fixed.

## Releases

Days are working days and are targets: what the drawing tests find decides the real pace.

| Release | Days | Tools | Accepted when |
|---|---|---|---|
| **0. Foundation** | 1–2 | Background test bench (done), new agent rules (done), product map (done), new visual design chosen (Direction 2, Drafting Vellum, 2026-11-11) | You picked a design; a test runs without you |
| **1. Profile views** (C3DPROPV: your yes on sheet 434132 and the pavement labels, 2026-11-12; Split step built from your 124-1 example, 2026-11-12: same stations, levels differ by 1 m; profile labels still to be arranged, release 3) | 3–4 | Profile Manager, TOP Fit, Match, Split, Profile DIMs, and one run that chains them for selected alignments | Tile 124 profile sheet 434132 matches your submitted PDF |
| **2. Networks** (C3DPRONET: Repair, Rims, Fit, Duplicates released 2026-11-12, your yes; Cover check with pavement zones, taxiway edge band 16.5 m excepted and max cover 6 m, 2026-11-12: 1 type B duct left under taxiway. Split, Swap, Networks from Feature Lines and Cover Designer ship as they are in their forms, your call) | 5–6 | Network Editor (split, merge, swap, fit, conduits, rim to surface), Organizer, Repair, Networks from Feature Lines, Cover Designer | Each passes on the ICT network copy with your rules (cover 0.60, dry/dry 0.30, slope 7 %) |
| **3. Labels** (released 2026-11-14 for use: C3DPROLBL Report / Add / Arrange / Reset and Annotation Studio Arrange place labels by pattern, measured as the sheet shows them. 102054 drawing copy: 31 of 33 placed, plot close to the submitted sheet; 2 junction labels left for hand placement. Rename (C3DPROLBL reName, Annotation Studio templates, all or nothing) and profile label rules (C3DPROLBL Add) added 2026-11-14: tile 124 copy 128 renamed, re-run 0; profile rules on 434132 found 45 labels already by hand, added 4 (short pipes without a name label), re-run 0. C3D Pro Run window and settings saved in each drawing (no file beside it), 2026-11-14, your request. Not yet tried by the user) | 7–8 | Add labels, Arrange, Reset, Rename | A tile 124 plan sheet: no overlaps, every label inside its viewport |
| **4. Sheets and standards** (2026-11-16, branch `feature/cad-sheet-export`: drawing tests pass for Build Sheets (102054), Sheet Manager reading and checks (44 sheets), Label Style Values (set and read back), Drawing Cleaner (Test.dwg); Workspace Palette merged with its review fix; CAD Sheets new, your request: explode passes, export step not yet finished in a test. Still to try: palette live, Sheet Manager edits, Style Manager and Navisworks Importer by you) | 9–10 | Sheet Manager, Build Sheets with the palette, CAD sheet export, Style tools, Drawing Cleaner, Navisworks Importer | Tile 124 sheets built in one run; cleaned copy opens correctly |

## Who works on what, in parallel

| | Claude | Codex | Antigravity |
|---|---|---|---|
| Release 0 | Foundation, form-free entry points for release 1 | Drawing tests for release 2 tools (so problems are known early) | Three design directions, then the chosen one in the shared style files |
| Release 1 | Profile tools | End review of release 1; drawing tests for release 3 | New look on the profile screens; ribbon regrouped |
| Release 2 | Network tools | End review; drawing tests for release 4 | New look on network screens |
| Release 3 | Label tools | End review | New look on label screens |
| Release 4 | Sheets, standards | End review | Palette |

## New visual design

The current look is generic (default blue on white, Segoe UI). The new one is defined once in `StyleUi.cs`, `ToolUi.cs` and
`WpfStyle.cs`, which 28 screens already use, so one change restyles the whole plugin. Layout problems of single forms
(too many steps, cramped fields) are fixed tool by tool in that tool's release.

## Test drawings

| Drawing | Used for |
|---|---|
| `C:\Users\melga\Desktop\temp\Test.dwg` (1 network, 93 pipes, 35 structures) | Fast network tests |
| `C:\Users\melga\Desktop\temp\TEST DRAWING.dwg` (ICT, tile 124) | Profile, label and sheet tests |

Tests always run on copies.

## Later (not before release 4)

- Crossings engine and model health checks
- Issues list
- Other networks' rule sets (MV, LV, fence lighting, navaids, MET)
- Quantities

