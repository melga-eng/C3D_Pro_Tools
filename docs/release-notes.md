# Release notes

## 2026-11-10: Redesign phase 1 begins: sheet production foundations

### New
- **Redesign plan and contract:** `docs/redesign-roadmap.md` and `docs/sheet-production-contract.md` (v0.3). Codex reviewed
  the contract in three rounds.
- **Sheet production contracts as code** (`Features/Sheets/Contracts`). The shared contract types moved to
  `Features/Shared/Contracts`.
- **Four engines** (Codex, reviewed by Claude): profile sheet packing, DIM stations (2 % / 10 m), continuation notes and
  placement score.
- **Scene reader:** one read-only pass over the drawing, covering sheets, viewports, networks, profiles, profile views,
  crossings, labels and the label styles with their content fingerprints. `C3DProSceneReport` writes what it sees.
- **Plan labels step:** labels from your rules (`C3DPro.project.json` beside the drawing), one label per chosen style,
  reconciled with the labels already there. Pinned and hand-placed labels are never touched. Apply writes only the plan you
  previewed. Command: `C3DProPlanLabels` (Preview, or Apply).
- **Workspace palette** (Antigravity, reviewed by Codex): WPF, with one tab per stage and a Sheets panel. Command:
  `C3DProWorkspace`, plus a ribbon button. It runs on sample data until the build is connected.
- **Sheet Manager:**
  - a profile sheet's title block scale is never overwritten with the viewport scale
  - a plan viewport scaled on purpose is a note, not a problem
  - a scale note is fixed from the title block

### Fixed (found live on TEST DRAWING.dwg)
- **Opening the workspace palette crashed Civil 3D with a fatal error.** A progress bar was bound two-way to a read-only
  property, and WPF threw while the palette was being built. The binding is now one-way. A palette that fails to build is now
  logged and reported instead of crashing, and a new smoke test builds the whole view off screen.
- **The scene reader read only 81 of 2,684 labels** (`RotationAngle` throws on labels that cannot rotate).
- **Style fingerprints missed edits to a style's components.** They now cover everything the style owns, and this was
  checked live: an edit changes the fingerprint. Restoring the edit does not bring the old fingerprint back, because Civil 3D
  also files a change marker. That is the safe side: the next Apply asks for a fresh preview.

### Checked live
- `C3DProPlanLabels`: Preview planned 143 labels on tile 124. Apply committed four sheets (46 + 72 + 13 + 12). Every label read
  back with its tag, and none counted as pinned. Running it again planned 0 changes. One UNDO removed all 143, and a Preview
  afterwards planned the same 143 again.

### Known limitations
- **Not yet run live in Civil 3D:** a rollback when a sheet fails to write. The scene reader was
  checked live on TEST DRAWING.dwg, and its two live findings are fixed.
- **Style edits:** the fingerprint fix (owned components) still needs a live check.
- **New labels land at their style's default position.** Arranging them is the next step (`feature/build-sheets`).
- **Not built yet:** alignment and station labels, one label per run, profile labels, profile views, DIMs and sheet data.
- **Profile View Tools setting:** the existing-ground surface is set to "COMBINED NGL". In this drawing it is
  "COMBINED NGL TILL 21-06-2026 AND NGL NOT SUBMITED", so no profile gets the existing role until the setting is corrected.

## 2026-11-09: Generate Conduits page in the Network Editor

### New
- **Generate Conduits** mode in the Network Editor (`ui/generate-conduits`, 882326f), built on the conduit contracts and
  engine merged earlier (08048bc):
  - the seven standard ICT and MV templates, with a section view and placement controls;
  - a conduit part chooser that works out the outside diameter and checks the fit;
  - end treatment, connection and mitre controls, with a dialog for per-end overrides;
  - a dialog to pick the structure when an unconnected end has more than one candidate;
  - a read-only preview that shows blocked rows and warnings for the whole request;
  - per-bank failure details in the result, and confirmation that each commit is one UNDO step.
- **Cover Optimizer:** Preview no longer crashes when a route has nothing to show (b80ae38).

### Known limitations
- **Live Civil 3D form testing was deferred.** The Generate Conduits page was checked by the build and unit tests only.
  It has not yet been tried in a live drawing: preview, apply, UNDO and DPI scaling are all untested.
- **Existing custom conduit layouts are still saved but can't be opened from the new page.** Layouts saved in settings
  by the old conduit tool are kept unchanged. The Generate Conduits page lists only templates, so it can't show those
  layouts until an import or migration step is added.
- Trimming at MV manholes is not verified, because their inner sizes have not been confirmed. Curved encasements,
  connecting conduits to structures, and creating a new destination network have not been tested live.
