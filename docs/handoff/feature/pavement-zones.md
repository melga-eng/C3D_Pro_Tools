Branch feature/pavement-zones (Claude; end review Codex, 4 of 5 findings fixed, narrow-stretch sampling left as a documented limit).
Changed: pavement section in C3DPro.project.json; C3DPRONET Cover gets pavement cover zones (own max, none under taxiway) and the duct type check; C3DPROPV Pavement labels the edges (tool-made styles from the drawing station-elevation style).
Checked: pv-pavement.scr (37 labelled, re-run replaced 37) and net-cover.scr on TEST DRAWING.dwg copy with tile124.project.json; 434132 plotted headless and compared with the hand markers.
Not done: the user has not run it; zone edges come from the surfaces, 0.3-0.55 m off the hand markers on two views; stretches narrower than the 1 m step can be missed.
