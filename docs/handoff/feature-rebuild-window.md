# feature/rebuild-window

- Rebuild Network window (`RebuildNetwork`, ribbon Model > Rebuild Network): networks listed (kind, structures, pipes, "rebuilt before"), references ticked; Check and Rebuild; report in the window and in %TEMP%\C3DPROREBUILD-*.txt.
- Shared code: `NetworkRebuild.Execute` takes the exact ticked names (no wildcards); `Inventory` lists the networks. The command line `C3DPROREBUILD` is unchanged.
- Checked: build + 770 tests; `rebuild-healthy.scr` on Test.dwg still 35/35, 93/93, 43/46 connections.
- Not checked: the window itself (a form cannot run in the background console).
