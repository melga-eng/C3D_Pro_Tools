# feature/drawing-standards

- Drawing Standards (DrawingStandards window, C3DPROSTANDARDS Check/Fix): the drawing against the KSIA CAD standard (units, coordinate system, objects on 0/Defpoints, layer names, line weights, fonts, dimensions, viewport scales, xrefs, sheets via Sheet Manager + title block formats, unused items). Fixes: units, xref paths relative.
- Checked: 824 tests; tests/drawing/drawing-standards.scr on TEST DRAWING copy: 7 problems, 4 notes; xref path fixed, re-check 6 problems.
- Review (Codex): 2 fixed (a sheet check failure is reported, not silent; Fixed count only counts complete fixes, xref paths each on their own).
- Not done: window not drawing-tested; no fixes for dimensions, text styles, objects on 0 (need the user's choice of target).
