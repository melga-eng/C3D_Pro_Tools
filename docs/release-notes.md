# Release notes

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
