Branch: feature/net-duplicate-levels; commit: pending Claude (base 6bfb69bc82d70dc85d2fb90aeb8e8f2e5a764b13); no git writes performed.
Files: C3D_Pro_Tools/NetworkMerge.cs, C3D_Pro_Tools/NetworkOrganizer.cs, tests/C3D_Pro_Tools.Tests/NetworkMergeTests.cs, this handoff.
Change: structure duplicates require rim and sump agreement within existing zTol (default 0.005 m); organizer supplies both levels; public method signatures preserved; existing pipe end-level checks retained and regression tested.
Checked: .\build.ps1 -Configuration Debug passed (0 warnings/errors); all 686 tests passed; git diff --check passed.
Not done: drawing test/real-drawing validation, commit, merge, deployment; graphify query unavailable (MCP approval blocked, local graph absent), update omitted to preserve assigned file scope.
