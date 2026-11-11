# C3D Pro Tools: rules for every agent

Civil 3D 2026 (.NET 10) plugin for dry-utility networks. One user: a civil engineer who works in Civil 3D all day, is not a
programmer, and needs working tools now. `CLAUDE.md` and `GEMINI.md` import this file and add one role each.

Read first: [docs/PRODUCT_MAP.md](docs/PRODUCT_MAP.md) (every tool, its state, its code) and
[docs/ROADMAP.md](docs/ROADMAP.md) (what is being worked on now).

## The five rules

1. **Done means it works on the user's drawing.** A task is finished when its drawing test passes on a copy of a real project
   drawing and the result matches the example the user gave. Passing unit tests and a clean build are required, not sufficient.
2. **One task in progress per agent, and only tasks from the roadmap.** A new idea goes to the roadmap's "Later" list, not
   into the code. Do not add features, options or safety layers nobody asked for.
3. **Never test in the user's working session.** Test with `tools\drawing-test.ps1` (background Civil 3D on a drawing copy).
   Ask for a live check only when the tool already passes there, and ask once, with exact steps.
4. **Fewer steps for the user beats more options.** Remember the last settings, offer presets, select first, run on many
   objects at once. If a tool needs more clicks than doing the job by hand, it is not done.
5. **Say plainly what you did and what you did not check.** No "should work". If it was not run on a drawing, say so.

## Two tracks

- **One-off job in a drawing** (move these manholes, fix this cover): do it through the C3D Bridge, on a copy first. It does
  not become plugin code. Save the script in `recipes/` with two lines on what it does.
- **Repeated job:** it becomes a plugin command when the user has needed it three times, or says so.

## How a task runs

1. The user gives a request card ([docs/REQUEST_CARD.md](docs/REQUEST_CARD.md)). If the result example or the rule numbers
   are missing, ask before writing code. Ask everything in one message.
2. The builder works on `feature/<short-name>` in their own folder and adds or updates a drawing test in `tests/drawing/`.
3. Before handing over: `.\build.ps1 -Configuration Debug`, the drawing test, `graphify update .`.
4. **One review, at the end.** The reviewer answers two questions only: can this damage the drawing, and can it give a wrong
   result? At most five findings, each with a real failure the user could hit. The builder fixes them and merges. There is no
   second review round unless a fix changed the design.
5. Handoff note, five lines at most: branch, what changed, how it was checked (which drawing test, which drawing), what is
   not done. Write it to `docs/handoff/<branch>.md` so the next agent reads it from the repo and the user does not paste it.

## Who does what

| Agent | Folder | Job |
|---|---|---|
| **Claude Code** | `C3D_Pro_Tools` | Builds features end to end (Civil 3D code, logic, wiring), writes drawing tests, promotes builds |
| **OpenAI Codex** | `C3D_Pro_Tools-codex` | The one end review of each task. Builds separate pure-logic pieces and drawing tests the user assigns |
| **Google Antigravity** | `C3D_Pro_Tools-antigravity` | Visual design and screens, only for commands that already pass their drawing test, never against fake data |

Each agent switches branches only in its own folder. Two agents never edit the same file at the same time. Merging to `main`
is allowed after the end review is clean; never push without the user asking.

## Code structure

`command → operation → drawing reader/writer → pure calculation`. Details in [ARCHITECTURE.md](ARCHITECTURE.md).

- Every operation must be runnable with no dialog, from a settings object. The form only fills that object. This is what
  makes presets, batch runs and background tests possible.
- Calculations take and return plain data (no Autodesk types), so they can be unit tested.
- Every drawing change: lock the document, one `Transaction` in `using`, one UNDO step, a per-item report of what changed
  and what failed. UNDO is the safety net; do not build stale-preview digests, fingerprints or tokens for new work.
- Project rules (cover, clearances, slopes, styles) come from `C3DPro.project.json` beside the drawing, not from code.
- Check Civil 3D API names with the `c3d-api` tools or in a drawing test before using them. Code checklist:
  `.agents/skills/civil3d-auditor/SKILL.md`.

## Builds

- **Stable:** the installed MSI. The user works with it. It is rebuilt only when a release in the roadmap passes.
- **Dev:** `C3D_Pro_Tools\bin\Debug\net10.0-windows\C3D_Pro_Tools.dll`, used by drawing tests, or loaded by the user with
  `AddInManagerManual` to try something without restarting.

## Live drawing (C3D Bridge)

MCP server inside Civil 3D at `http://127.0.0.1:47321/mcp`. Read before writing. The drawing is real work: say what will
change and get a yes before any edit, unless the user asked for exactly that edit. `c3d_run_command` and
`c3d_execute_csharp` need the `drawing` name. A timeout that says "still running" may still change the drawing; re-read first.

## Code map (graphify)

`graphify-out/` holds a map of the code. Use `graphify query`, `graphify explain "<Class>"` or the graphify MCP tools to find
callers before changing a class. The source is the truth if they disagree. Run `graphify update .` after editing `.cs` files.
