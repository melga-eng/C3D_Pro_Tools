# feature/rebuild-network

- C3DPROREBUILD rebuilds a dead data-shortcut network as an ordinary one; sizes from own family, else the drawing's catalog, else Method A; all or nothing with a self-check (place, rim, footprint, pipe ends, sections within 1 mm; wrong connections refused).
- Checked: `rebuild-healthy.scr` on Test.dwg (35/35, 93/93, 43/46 connections, 0 mm); `rebuild-report.scr` on the user's 000004 drawing (reports the US Imperial catalog as the blocker).
- A clone of a dead reference stays a reference and its parts cannot be moved out (tested); the US Imperial catalog cannot make metric or any-size parts.
- Not done: a rebuild of the user's 000004 networks (needs the drawing set to the Metric catalog); a window (command line only).
- Review: Codex, 3 findings fixed; unconnected ends far from their structure are listed rather than refused (geometry kept exact).
