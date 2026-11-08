# C3D_Pro_Tools

Civil 3D 2026 (.NET 10) plugin. C# source is in `C3D_Pro_Tools/`, the WiX installer in `C3DProInstaller/`, and the build script is `build.ps1`.

This file holds the rules for every agent on the project: Claude Code, OpenAI Codex and Google Antigravity. `CLAUDE.md` and `GEMINI.md` import it and only add each agent's role, so put shared rules here, once.

## Team workflow (the user orchestrates every step)

Three agents work on this repo. Each has one job. None starts another agent's work or merges anything; the user moves the work from one agent to the next.

| Agent | Role | Does | Does not |
|---|---|---|---|
| **Claude Code** | Main builder | Implements features and fixes on a feature branch, builds, runs tests, commits when asked | Review its own work as final |
| **OpenAI Codex** | Reviewer and auditor | Reviews a branch's diff against `main` with the `civil3d-auditor` skill, reports findings by severity | Edit code unless the user asks it to fix a specific finding |
| **Google Antigravity** | UI design and heavy work | WinForms/WPF form layouts, ribbon/icons, large mechanical refactors, browser research | Change engine logic (`Cover*`, `Navis*`, `Network*` solvers) without a note to the user |

Rules for all three:

- **One branch per task.** Work on `feature/<short-name>` (or `ui/<short-name>` for Antigravity). Never commit to `main`.
- **Two agents never edit the same files at the same time.** If a task needs another agent's files, stop and tell the user.
- **Leave a handoff note.** When you finish, end with: the branch, what changed (files), how you checked it (build/tests/live Civil 3D), and what is not done. The user pastes this into the next agent.
- **Build and test before handing off:** `.\build.ps1 -Configuration Debug` (builds everything and runs the tests). Report failures as they are.
- **Review findings** go in the format: `severity (high/medium/low) · file:line · problem · concrete failure scenario · suggested fix`.

## Live Civil 3D connection (C3D Bridge MCP)

`C3DProTools.Bridge/` is a small plugin that runs inside Civil 3D and exposes the open drawing to agents as an MCP server at `http://127.0.0.1:47321/mcp` (localhost only). Load it in Civil 3D with `NETLOAD` (or it autoloads from the bundle); `C3DBRIDGE` shows its status.

Tools: `c3d_status`, `c3d_list_objects`, `c3d_get_object`, `c3d_list_civil`, `c3d_zoom_to`, `c3d_run_command`, `c3d_execute_csharp`.

- **Read before you write.** Use `c3d_status` and the list/get tools to see the drawing before changing anything.
- **The user's drawing is real work.** Before anything that modifies the drawing (`c3d_run_command` that edits, `c3d_execute_csharp` that writes), say what it will change and wait for the user's go-ahead, unless they already asked for exactly that change.
- **Name the drawing.** `c3d_run_command` and `c3d_execute_csharp` require `drawing` (as `c3d_status` lists it); they refuse to run if another drawing is active. A timeout that says "cancelled" means nothing ran; "still running" means it may still change the drawing, so re-read before retrying.
- **Test code changes live** by running the plugin's own commands through `c3d_run_command` after the user has reloaded the rebuilt DLL.
- `c3d_execute_csharp` snippets must follow the auditor rules below: one `Transaction` in `using`, `doc.LockDocument()` is already taken by the bridge, no UI calls.

## Knowledge graph (graphify): use it first

A graphify knowledge graph of this codebase lives in `graphify-out/`. Use it before grepping or reading whole files.

- **Orientation:** for a new task, read `graphify-out/GRAPH_REPORT.md` first. It covers the clusters, the core classes (god nodes) and the unexpected links.
- **Find code:** use the `graphify` MCP tools: `query_graph`, `get_node`, `get_neighbors`, `shortest_path`, `get_community`, `god_nodes`. If the MCP server isn't available, use the CLI instead: `graphify query "<question>"`, `graphify explain "<Class>"`, `graphify path "<A>" "<B>"`.
- **Before changing a class or method:** run `get_neighbors` on it (or `graphify explain "<Name>"`) to see every caller and dependent. Check those call sites before editing so nothing breaks.
- **Then read the code:** the graph gives file and line references (e.g. `PipeRunBuilder.cs:L85`). Open only those ranges, not whole files.
- **Graph vs code:** if the graph and the source disagree, the source is the truth. The graph may be stale.

## Keeping the graph current

- After editing `.cs` files, run `graphify update .` before finishing the task. It takes about 3 seconds, runs locally and uses no LLM.
- After big refactors that delete code, run `graphify update . --force`.
- The Obsidian vault in `graphify-out/obsidian/` is not refreshed automatically. Regenerate it with `graphify export obsidian` when the user wants to browse the latest map.
- Do not edit anything in `graphify-out/` by hand.

## Civil 3D API

Use the `c3d-api` plugin skills and MCP tools (`lookup_type`, `search_api`, `get_parameters`, …) to verify Civil 3D / AutoCAD .NET API names and signatures instead of guessing. Agents without those tools: check against the live drawing with the bridge, or say the API name is unverified.

Code rules (full checklist in `.agents/skills/civil3d-auditor/SKILL.md`): lock the document from modeless forms and ribbon handlers, wrap every `Transaction` in `using`, never reuse opened objects after commit, check style names exist before `.Add()`, Autodesk references stay `Private=False`.
