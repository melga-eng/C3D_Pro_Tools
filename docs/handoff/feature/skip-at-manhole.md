Branch: feature/skip-at-manhole (merged to main).
Changed: Annotation Studio label table column "Leave out at a manhole" (No / At start / At end / At either end) on pipe rows; AnnotLabels.Apply leaves the row out where that end is connected to a structure; Replace also removes the left-out row's old labels. drawing-test.ps1 deletes its run folder (log and reports kept in %TEMP%\c3dpro-test-logs).
Checked: probe calling AnnotLabels.Apply on a TEST DRAWING copy (ICT-TILE-124: 128 pipes, 25 end at a manhole: 103 labelled, 25 left out); 742 unit tests. Codex end review: 2 findings (several-positions count, Replace keeping left-out labels), fixed.
Not done: the form column itself not clicked by anyone.
