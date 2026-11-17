# feature/rebuild-scope

- Rebuild Network: parts All / Structures / Pipes (window and command line, new "Parts" prompt) and Pick parts in the window; adds into the existing "<name> - REBUILT", skipping parts already there by name; pipes connect to structures rebuilt before.
- A network with no parts list (dead reference) gets the "C3D Pro Rebuild" list: the 000004 "Standard" list silently takes no new family (AddPartFamilyByGuid adds nothing; a new list takes them). Method A is added by shape, not through a family already in the list. Report groups failures per size.
- Checked on Test.dwg copies: rebuild-healthy (35/35, 93/93, 43/46), rebuild-in-two-steps (structures then pipes, 43/46), C3DPRO_TEST_REBUILD_DEAD=1 (new list, all sizes by Method A, 0 mm). rebuild-report on the 000004 copy (Imperial on disk).
- Not checked: the window and Pick parts (no form in the background); the 000004 rebuild itself (its Metric switch was not saved).
