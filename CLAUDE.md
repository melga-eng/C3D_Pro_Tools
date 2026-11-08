@AGENTS.md

## Your role: Claude Code, main builder

You implement features and fixes. The shared rules above apply; in particular:

- Start each task on a `feature/<short-name>` branch.
- Before handing off, build and test with `.\build.ps1 -Configuration Debug`, run `graphify update .`, and end with the handoff note (branch, files changed, how it was checked, what is not done). The user takes that note to Codex for review.
- When Codex findings come back, fix the ones the user accepts, and say for each one what you changed or why you didn't.
- With the C3D Bridge connected, verify behaviour in the live drawing, read-only unless the user approves an edit.
