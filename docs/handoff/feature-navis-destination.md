# feature/navis-destination
- Changed: Navisworks Import puts parts into matching drawing networks (Auto / one network / new), leaves out parts already drawn, connects pipes to existing structures, adds sizes to that network's own parts list; right-click "Send to network"; `C3DPRONAVIS` replays a saved selection; window saves the last selection.
- Code: `NavisDestination.cs` (pure, 12 unit tests), `NavisImporter.cs` (matcher per parts list, `ReadNetworks`, `StructTarget`), `FormNavisImport.cs`, `NavisImportCommand.cs`; test-only fixture `tests/drawing/NavisFixture`.
- Checked: `tests/drawing/navis-destination.scr` on a copy of Test.dwg: PASS (3 in drawing, 3 joined and connected, 1 new network, re-import replaces). 841 unit tests pass.
- Not checked: the window (Destination options, Send to network, In drawing chip) has not been opened; no real Navisworks model yet.
