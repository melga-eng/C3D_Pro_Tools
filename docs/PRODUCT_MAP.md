# Product map

What is in the plugin, in plain words: each tool, what it does, its state, and where its code is.
Claude keeps this page current. Last update: 2026-11-12.

**State** is what is known today, mostly from the project history, not from a fresh test:

- **Works**: you said it works, or it was checked in a drawing.
- **Problem**: you reported a problem and no later check shows it fixed.
- **Not proven**: built and unit tested, never checked on a real drawing.
- **Unknown**: no record either way.

A tool moves to **Released** only when its drawing test passes on a copy of a project drawing and you accept the result.
Nothing is Released yet, because drawing tests started today.

**Ribbon (2026-11-15, every tool):** Model (Network Editor, Network Organizer, Networks from Feature Lines, Profile & Cover Designer, Rebuild Network, Navisworks Importer), Profiles (Profile View Tools, Profile Manager, Profile Views & TOP Fit, Profile DIMs), Produce (Pro Run, Annotation Studio, Sheet Manager, CAD Sheets, Workspace Palette), Standards (Style Manager, Label Style Values, Drawing Cleaner), Command Runs (Profile Views / Networks / Labels Run). How to use: [GUIDE.md](GUIDE.md).

## How the plugin is built

```
Ribbon button → Command → Form (settings) → Operation → Drawing reader/writer → Calculation
                                                          (Civil 3D API)        (plain maths, unit tested)
```

- 55,000 lines of C#, 24 commands, 636 unit tests (maths only).
- Older tools are one large form with the logic inside it (files `Form*.cs` at the top of `C3D_Pro_Tools/`).
- Newer tools are split into folders under `C3D_Pro_Tools/Features/` (`Networks`, `ProfileTool`, `Sheets`, `Workspace`).
- The look of every screen comes from three files: `StyleUi.cs`, `ToolUi.cs` (WinForms) and `Features/Workspace/WpfStyle.cs`.
- Other projects: `C3DProTools.Bridge` (live connection for agents), `C3DProTools.Navis` (Navisworks add-in),
  `C3DProInstaller` (MSI), `tests/` (unit tests and `tests/drawing/` drawing tests).

## Model: build and fix networks

| Tool (command) | What it does | State | Main code |
|---|---|---|---|
| Networks run (`C3DPRONET`) | Report / Repair / Rims / Fit / All for chosen networks, no dialog, settings in `C3DPro.project.json` (`networks`) | **Released 2026-11-12** (your live check: works). Drawing test: tile 124 copy: 48 rims set, 738 pipes fitted, re-run finds 0 to fit; Test.dwg: 29 fitted). Large moves are left alone and listed | `Features/Networks/Civil3D/NetworksRun.cs` |
| Network Editor (`NetworkEditor`) | Split, merge, swap parts, fit pipes to structures, reverse, parallel copies, generate conduits, rim to surface | Split: **Works** (checked live 8 Nov). Generate conduits: **Not proven** in the form. Fit: cause of the 1 Nov failure found (pipes connected to far structures stretched up to 140 m), now refused above 3 m; merge now checks level. Swap: **Unknown** | `FormNetworkEditor*.cs`, `NetworkEditOps.cs`, `PartSwap.cs`, `Features/Networks/` |
| Network Organizer (`NetworkOrganizer`) | Merge scattered networks, remove duplicate pipes | **Not proven**; duplicates now need matching levels (Codex fix) | `NetworkOrganizer.cs`, `NetworkMerge.cs` |
| Networks from Feature Lines (`NetworkFromFeatureLines`) | Draw pipes along feature lines, single or batch | **Problem**: failed on parts of some feature lines and on curves (1 Nov) | `NetworkFromLines.cs`, `FormNetworkFromLines.cs` |
| Navisworks Importer (`ImportNavisPipe`) | Bring pipes, structures and duct banks from Navisworks as Civil 3D parts | **Released**. Fixed 2026-11-17 after your report: Civil 3D pressure pipes come in as pipes (ends and diameter from their properties, else the model; curved ones curved), and Import as Pipe no longer reverts. Unit tests only: not yet tried on your Navisworks model | `Navis*.cs`, `C3DProTools.Navis/` |
| Rebuild Network (window `RebuildNetwork`; command line `C3DPROREBUILD`) | Window (2026-11-17): the networks listed with kind and counts, references ticked; parts All / Structures only / Pipes only / picked on screen; Check, Rebuild, report shown in the window. Parts go into the existing "- REBUILT" network, those already in it skipped (structures then pipes connect). A network with no parts list gets a new "C3D Pro Rebuild" list (the 000004 "Standard" list takes no new family). A network that cannot be edited (dead data-shortcut reference: no repair, no promote) rebuilt as an ordinary network: structures at place, rim, sump; pipes with ends, size and curve, connected; names, styles, layers. Sizes from the part's own family, else the drawing's catalog, else the any-size families (Method A). Checks itself: refuses if place, rim, pipe ends or sections differ by more than 1 mm | **Drawing test passes** on Test.dwg (2026-11-17): 35 structures, 93 pipes; place, rim, footprint, pipe ends and sections 0 mm; 43 of 46 connections (3 original ends 3.5 m from their structure are kept exact and listed unconnected); 3 sumps lowered by Civil 3D to the pipe invert, listed. Your 000004 drawing uses the US Imperial catalog: set Metric first (the report says so). Also passes: structures then pipes in two runs (93 pipes connect to the structures of the first run); acting as a dead reference (new list, every size by Method A, 0 mm). The window and Pick parts are not drawing-tested (a form cannot run in the background); its runs use the same code as the command. Not yet tried by you | `Features/Networks/Civil3D/NetworkRebuild.cs`, `FormNetworkRebuild.cs` |
| Repair pipes (`RepairNetworkPipes`) | Rebuild pipes that dropped out of their network | Runs in `C3DPRONET Repair`; a rebuild that loses a connection is undone, labelled pipes skipped. 0 orphans in the test drawings, so the write path is not drawing-tested | `NetworkRepair.cs`, `NetworkChecks.cs` |
| Advanced Network Tools (`AdvancedNetworkTools`) | Old entry point; its features moved to Network Editor | To remove from the ribbon | `NetworkToolsCommands.cs` |

## Coordinate: cover, clearances, profiles

| Tool (command) | What it does | State | Main code |
|---|---|---|---|
| Profile & Cover Designer (`ProfileCoverDesigner`, `CoverOptimizer`) | Set pipe levels along a route to keep cover, slope and clearances | **Problem**: cover not exact (31 Oct); preview crash fixed 9 Nov, result not confirmed | `Cover*.cs`, `FormRouteDesigner*.cs`, `PipeRunBuilder*.cs` |
| Parts to Profile (`PartsToProfile`) | Create the alignment and profile view for a run and project its parts | **Unknown** | `ProfilePartDetection.cs`, `ProfileCreation.cs`, `FormProfileSetup.cs` |
| Profile Manager (`PROFILEMGR`) | Profiles of every alignment by surface: delete, create on all | **Unknown**: built 11 Nov, no problem reported since | `Features/ProfileTool/` |
| Profile Views & TOP Fit (`PVMGR`) | List profile views, fit TOP profiles to duct tops, create missing ones | **Unknown**: built 11 Nov, no problem reported since | `Features/ProfileTool/TopProfile*.cs` |

## Document: labels, profile views, sheets

| Tool (command) | What it does | State | Main code |
|---|---|---|---|
| C3D Pro Run (`C3DPRORUN`, ribbon Pro Run) | One window: Profile views / Networks / Labels / Pavement tabs over the C3DPROPV / C3DPRONET / C3DPROLBL steps; settings saved in the drawing, none beside it | **Built 2026-11-14**: settings saved in a drawing copy and read back; the window itself not yet opened by you | `Features/Shared/FormProRun.cs`, `SettingsStore.cs`, `Contracts/SettingsSource.cs` |
| Labels run (`C3DPROLBL`) | Report / Add / Arrange / Reset / Rename for chosen layouts, no dialog, settings in `C3DPro.project.json` (`labels`, `labelRules`) | **Drawing test passes** for Report, Add (plan: 12 added, re-run 0; profile rules on 434132: 45 already by hand, 4 added, re-run 0; never a second label where one of the style is placed by hand), Reset, Rename (tile 124: 128 renamed with the Annotation Studio templates, re-run 0) and Arrange (102054: 31 of 33 placed). Waiting for your try | `Features/Labels/LabelsRun.cs` |
| Pavement zones (`C3DPRONET Cover`, `C3DPROPV Pavement`) | Pavement kinds from surfaces (taxiway, road): cover and duct type by pavement in the cover check; start / end of pavement labels in profile views (on the alignment's proposed profile; 000501 copy: 16 on the profile, were on the view top line); edge band a choice for the cover check and the labels | **Drawing test passes** (tile 124 copy: 37 edges in 21 views, within mm of the hand markers on most, re-run replaces its own; 13 type-B ducts under taxiway found). Waiting for your check | `PavementZones.cs`, `Features/Pavement/PavementCivil3D.cs` |
| Profile views run (`C3DPROPV`) | Report / Top / Match / Dims / All for chosen alignments, no dialog, settings in `C3DPro.project.json` (`profileViews`) | **Drawing test passes** (tile 124 copy, ICT-ALI-124*: 12 TOP fitted, 21 views matched, 90 DIMs; second report: 19/19 fit, 0 Match changes). Live check 12 Nov failed: Match reset every view's level range and cut off structures; fixed (keeps hand ranges, widens only a cut side, counts structure tops). Waiting for your re-check. Split not in it yet | `Features/ProfileTool/ProfileViewsRun.cs` |
| Profile View Tools (`ProfileViewTools`) | Match one profile view's style, bands and profiles to others; split long views | Match: cause of the 11 Nov problem found (template surface names); fixed in `C3DPROPV Match`, the form still has it. Split: **Unknown** | `ProfileViewMatch.cs`, `ProfileViewSplit.cs`, `ProfileViewRecipe.cs` |
| Profile DIMs (`PROFILEDIMS`) | Cover and clearance dimensions in profile views | **Not proven** (built 11 Nov) | `Features/ProfileTool/` |
| Annotation Studio (`AnnotationStudio`) | Add labels, arrange them inside viewports, reset, rename parts | Reset: **Works**. Arrange: 102054 copy 31 of 33 placed (2 by hand), waiting for your try. Rename: same engine as C3DPROLBL Rename, drawing test passes. Add labels: a pipe row can be left out where the pipe starts / ends / either at a manhole (tile 124 copy: 103 labelled, 25 left out) | `FormAnnotationStudio.cs`, `AnnotArranger.cs`, `LabelLayout.cs`, `AnnotNaming.cs` |
| Sheet Manager (`SheetManager`, report `C3DPROSHEETS`) | List, rename, copy, create layouts by tile and part; checks | **Released** (closed for Release 4 by you, 2026-11-17). Drawing tests pass on the tile 124 copy: reading and checks, rename, copy; drawing number follows (tag found when the saved one is missing) | `SheetManager.cs`, `SheetManagerRun.cs`, `FormSheetManager.cs` |
| CAD Sheets (`C3DPROCAD`, background steps `C3DPROCADRUN Explode/Export`) | One AutoCAD 2018 DWG per chosen layout, named after it: labels exploded as each sheet shows them (plan at the viewport twist, profile at true north) and flattened to text and lines, EXPORTTOAUTOCAD, that layout only and current, purged. Two background Civil 3D runs on a copy of the saved drawing. Replaces SplitSheets.lsp | **Released** (your yes, 2026-11-16, after your run). Drawing test passes on your 000501 copy: 7 sheets in 2.8 min, each opens on its sheet, no Civil 3D objects, DWG 2018; xrefs not bound | `Features/CadExport/` |
| Build Sheets (`C3DProBuildSheets`, `C3DProPlanLabels`) | One run that makes plan labels, profile labels and arranges them for chosen sheets | **Drawing test passes** (tile 124 copy, 102054, 2026-11-16: preview 13 changes, apply added 12 and arranged 7; a second preview adds nothing) | `Features/Sheets/` |
| Workspace Palette (`C3DProWorkspace`) | Docked panel for Build Sheets | **Merged**; sheet names shortened to the sheet number with tile-part on the pill and the full name as tooltip (your screenshot, 2026-11-17). Your try showed it loads your sheets | `Features/Workspace/`, `Features/Sheets/UI/` |

## Standards: styles and cleanup

| Tool (command) | What it does | State | Main code |
|---|---|---|---|
| Style Manager (`StyleManager`) | Find unused, duplicate and non-standard styles; merge, replace, purge | **Released** (closed for Release 4 by you, 2026-11-17). Fixed 2026-11-15 (15 defects); test drawing copy: 424 styles scanned, a duplicate merged | `Style*.cs`, `FormStyleManager.cs` |
| Label Style Values (`STYLEMGR`, no window `C3DPROSTYLEVAL`) | Batch-set text heights, arrowheads and other values of label styles | **Drawing test passes** (Test.dwg copy, 2026-11-16: 6 structure text heights 2.5 -> 3 mm, read back). The window itself not tried | `Features/ProfileTool/` |
| Drawing Cleaner (`C3DDrawingCleaner`, `CleanSubmittalDrawing`, no window `C3DPROCLEAN`) | Save a cleaned copy for submittal | **Fixed 2026-11-17** after your run (copy 753 objects short): a saved drawing is now copied exactly, not by Wblock. Tile 124 copy, Networks only: 9850 deleted, 43 sheets removed, networks kept (1731 pipes, 542 structures), 977950 unused APPIDs purged, 150 -> 29 MB, open drawing unchanged. Your try with the new installer | `DrawingCleanup.cs`, `FormDrawingCleaner.cs`, `DrawingCleanerCommand.cs` |

## Known overlaps to merge

| Job | Exists in | Keep |
|---|---|---|
| Arranging labels | Annotation Studio, Build Sheets, an unmerged Arrange screen | One arranger behind all of them: `AnnotArranger` + `LabelLayout` |
| Profile view work | Profile View Tools, Profile Manager, PVMGR, Profile DIMs, Parts to Profile | One "Profile views" tool with these as steps |
| Styles | Style Manager, Label Style Values | One tool |
| Screens | 10 WinForms forms and one WPF palette | One look now (shared style files); one screen system later |

## Unmerged work

| Branch | Holds | Plan |
|---|---|---|
| `ui/palette-real-service` (Antigravity) | Palette wired to the real Build Sheets | Merged into `feature/cad-sheet-export` 2026-11-16 |
| `feature/ict-pilot-placement`, `ui/arrange-labels-demo` | New Arrange Labels operation and screen | Dropped 2026-11-16: C3DPROLBL Arrange and Build Sheets cover it (branches kept, not merged) |
