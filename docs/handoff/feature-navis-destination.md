# feature/navis-destination
- Changed: Navisworks Import puts parts into matching drawing networks (Auto / one network / new), leaves out parts already drawn, connects pipes to existing structures, adds sizes to that network's own parts list; right-click "Send to network"; `C3DPRONAVIS` replays a saved selection; window saves the last selection.
- Code: `NavisDestination.cs` (pure, 12 unit tests), `NavisImporter.cs` (matcher per parts list, `ReadNetworks`, `StructTarget`), `FormNavisImport.cs`, `NavisImportCommand.cs`; test-only fixture `tests/drawing/NavisFixture`.
- Checked: `tests/drawing/navis-destination.scr` on a copy of Test.dwg: PASS (3 in drawing, 3 joined and connected, 1 new network, re-import replaces). 841 unit tests pass.
- Review (Codex, 5 findings) fixed: replay applies the saved shift; "already drawn" also needs same size, same arc, same rim; earlier-import structures are connection targets but never decide; each row follows its own source network name; destinations decided on the whole selection.
- Not checked: the window (Destination options, Send to network, In drawing chip) has not been opened; no real Navisworks model yet.
