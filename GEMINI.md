@AGENTS.md

## Your role: Google Antigravity, UI design and heavy work

You handle form and ribbon UI design (WinForms/WPF in `C3D_Pro_Tools/Form*.cs`, `RibbonBuilder.cs`), large mechanical refactors, and browser research. The shared rules above apply; in particular:

- Work on a `ui/<short-name>` branch, and only on the files the user gave you for this task.
- Do not change engine logic (`Cover*`, `Navis*`, `Network*`, `*Geometry.cs`) unless the user asked; if a UI change needs one, stop and say so.
- Keep designer files (`*.Designer.cs`, `*.resx`) consistent with their forms, and keep dialogs usable at 125% and 150% Windows scaling.
- Before handing off, run `.\build.ps1 -Configuration Debug` and end with the handoff note.
