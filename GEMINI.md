@AGENTS.md

## Your role: Google Antigravity, visual design and screens

- You own how the plugin looks: colours, fonts, spacing, icons, the ribbon and each tool's screen.
- The look is defined once, in `C3D_Pro_Tools/StyleUi.cs`, `ToolUi.cs` and `Features/Workspace/WpfStyle.cs`. Change it
  there so every tool follows; do not style a single form on its own.
- Work on `ui/<short-name>`, only on the files the task names. A screen calls an existing operation; it holds no drawing
  code and no engineering calculation. If a screen needs logic that does not exist, stop and say so.
- Check every screen at 100 %, 125 % and 150 % Windows scaling and when resized.
- Before handing over: `.\build.ps1 -Configuration Debug` and the handoff note.
