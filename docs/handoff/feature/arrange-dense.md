Branch feature/arrange-dense (Codex planner + Claude fallback; NOT merged).
Changed: LabelLayout column candidates, forward checking, cached checks (replay 48 s to 12 s, 33/33 placed offline, crossings 5 to 7); AnnotArranger second-best place over a leader.
Checked: ArrangeCaseTests on the 102054 case; lbl-arrange.scr on the TEST DRAWING copy gives 6 unplaced, 8 problems (main gives 7 and 7-8): no gain on the drawing.
Cause found: the gap is between the planned box and where Civil 3D draws a dragged label (leader attachment), not the search. Next: measure the dragged shape per style before planning, or plan with the measured Probe shapes.
