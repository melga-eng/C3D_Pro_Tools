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
| **4. Sheets and standards** (released 2026-11-17: CAD Sheets (your yes after your run), Build Sheets with the Workspace Palette, Sheet Manager, Label Style Values, Style Manager and Navisworks Importer (closed by you), Drawing Cleaner (fixed after your run: a saved drawing is copied exactly; tile 124 copy passes). Not yet tried by you: the new Drawing Cleaner and the palette sheet names) | 9–10 | Sheet Manager, Build Sheets with the palette, CAD sheet export, Style tools, Drawing Cleaner, Navisworks Importer | Tile 124 sheets built in one run; cleaned copy opens correctly |

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

## After release 4 (2026-11-17)

- Navisworks Importer: pressure pipes import as pipes, Import as Pipe sticks (your report). Merged.
- Rebuild Network (window `RebuildNetwork`, command line `C3DPROREBUILD`): dead data-shortcut networks rebuilt as ordinary ones (your request). Window, parts scope and pick added; works on your 000004 drawing (2026-11-17).
- New size: Method A no longer crashes; the reason is named (a drawing on the US Imperial catalog cannot make metric sizes).

- Network Layers (Uniclass / KSIA layers, your request 2026-11-18): stages 1 and 2 merged (ICT, then every network with KSIA template suggestions); needs your try. Stage 3, Drawing Standards (the other KSIA CAD rules), merged 2026-11-18; needs your try.

- Surface from Solids (your request 2026-11-18): TIN from the top or bottom of 3D solids and meshes. Merged; needs your try.

- Clash Grouper (Navisworks, your request 2026-11-20): groups by status, connected main-side elements (1 m) and span (25 m); Send to Civil 3D imports the other side's items with a revision cloud per area. Merged 2026-11-22; needs your try of the duct-into-manhole fix and Send.

- TOP profile and encasement (your request 2026-11-22): follow the encasement with a vertical step at each end of a run, or the cable inside it (a setting). Merged 2026-11-22; needs your try.

## Public release 1 (started 2026-11-19)

First package for people outside the project, shared on LinkedIn: **Navisworks Import, Surface from Solids, Rebuild Network**.
Civil 3D 2026.2.2 and later (.NET 10), metric only. Download link on a free host that counts downloads; feedback by Google Form.

| # | Task | State |
|---|---|---|
| 1 | Navisworks Import: destination (Auto into matching drawing networks, one chosen network, or new), parts already drawn left out, connect to existing structures, sizes into that network's parts list | **Merged** 2026-11-19; drawing test passes; Codex review (5 findings) fixed; waiting for your try in the window |
| 2 | Navisworks Import: styles (keep default / one for all pipes and one for all structures / per part) | To do |
| 3 | Navisworks Import: solids made interactive (import as surface, layer per Navisworks layer) | To do |
| 4 | Surface from Solids: a small window (side, name, style from a list, remembered) | To do |
| 5 | Rebuild Network: report as a table per network, shorter text | To do |
| 6 | One look for the three windows (Antigravity designs, Claude wires) | To do |
| 7 | Public build: same code, own installer (`C3DProInstaller.Public`, v1.0.0), ribbon with the three tools, Send Feedback / About, Method B never used; the two installers refuse each other | **Merged** 2026-11-21; public DLL passes the three drawing tests; MSIs not yet installed by anyone |
| 8 | Google Form for feedback; its link goes into `Edition.FeedbackUrl` | **Done** 2026-11-21; form made with `docs/public/create-feedback-form.gs`, link in Send Feedback; both MSIs rebuilt |

Tasks 2 to 6 come after the 1.0 release, as 1.1.

## Later (not before release 4)

- Crossings engine and model health checks
- Issues list
- Other networks' rule sets (MV, LV, fence lighting, navaids, MET)
- Quantities

Ideas from civil3dfactory (github.com/hellojessieluo-svg/civil3dfactory, Apache-2.0; read 2026-11-22, none of its code run):

1. **Drawing tests check numbers, not log text.** Each `C3DPRO*` run writes a small JSON report (per item: ok / changed /
   failed, with counts), rewritten after each step so a crash still leaves it. `drawing-test.ps1` checks values in it
   (rims set = 48, re-run finds 0) instead of `-Expect` text.
2. **Network issues check** (the Issues list above). Read-only, one list of `{level, code, object, message}`: pipe ends
   not connected, rim off the surface, cover below the project rule, network without a parts list, reference surface
   missing (guarded read), dead data-shortcut networks, parts on the wrong layer.
3. **Generated window for no-window commands.** Built from the tool's settings object: dropdowns of the drawing's own
   names, units, last values remembered. For small tools first (public release task 4, Surface from Solids window);
   polished tools keep their designed windows.
4. **Command list with help.** One list of the `C3DPRO*` commands, their steps, settings and whether they write the
   drawing; `GUIDE.md` and agent help generated from it.
5. **Smaller extras.** An audit line per drawing test with the DLL version; a resident background Civil 3D that keeps the
   drawing open between steps (only once tests run many steps on big drawings); their `create_plan_frames_from_alignment`,
   `restyle_profile_view_bands` and `set_block_attributes` as references when we need them.


- ICT and MV as templates (your request 2026-11-23): Rename name-pattern presets (ICT, MV) and numbering per name / across the network / per sheet, MV type codes T1-T4 and ALMH/VLMH; cover check with any duct types and ICT/MV presets; Split view From - To (one part, long view kept). Merged 2026-11-23; needs your try.

- Drawing Standards > Layer names (your request 2026-11-23): each non-KSIA layer and the objects on layer 0 / Defpoints to a KSIA layer; frozen-in-viewport layers and block contents left; choices remembered. TEST DRAWING copy: 69 objects moved, a layer merged, one renamed, a frozen layer skipped. Merged; needs your try.
