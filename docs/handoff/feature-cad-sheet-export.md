# feature/cad-sheet-export

- Branch `feature/cad-sheet-export`: Release 4 work. New CAD Sheets (`C3DPROCAD`, replaces SplitSheets.lsp); no-window runs `C3DPROCLEAN`, `C3DPROSHEETS`, `C3DPROSTYLEVAL`; Sheet Manager takes a profile sheet's tile from its title; Workspace Palette merged with its review fix.
- Checked by drawing tests on copies: `build-sheets-102054.scr` and `sheets-report.scr` on TEST DRAWING (tile 124), `clean-networks.scr` and `style-values-set.scr` on Test.dwg, `cad-explode.scr` on TEST DRAWING (2560 + 675 labels exploded).
- Not done: CAD Sheets export step has not finished in a test (EXPORTTOAUTOCAD over 25 min on the 150 MB drawing); PDF angle comparison of a CAD sheet; Drawing Cleaner on the tile drawing.
- Needs a live check: CAD Sheets window and background launch, Workspace Palette, Sheet Manager edits (rename, copy, create), Style Manager, Navisworks Importer.
- Dropped: `feature/ict-pilot-placement`, `ui/arrange-labels-demo` (C3DPROLBL Arrange and Build Sheets cover them).
