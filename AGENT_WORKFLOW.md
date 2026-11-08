# Multi-agent workflow for C3D Pro Tools

You orchestrate every step. Three agents, one repo, and one live connection to Civil 3D:

```
                     ┌──────────── you (orchestrator) ────────────┐
                     │  give a task · read the handoff · decide  │
                     └──────┬───────────────┬──────────────┬──────┘
                            ▼               ▼              ▼
                     Claude Code       OpenAI Codex    Antigravity
                     main builder      reviewer /      UI design &
                     feature/<x>       auditor         heavy work, ui/<x>
                            │               │              │
         shared rules:      └──── AGENTS.md (CLAUDE.md, GEMINI.md import it) ────┘
         shared code map:         graphify-out/  (graphify MCP)
         live drawing:            C3D Bridge MCP  http://127.0.0.1:47321/mcp
                                          │
                                  Civil 3D 2026 (C3DProTools.Bridge.dll)
```

## What lives where

| File | Who reads it | What it is |
|---|---|---|
| `AGENTS.md` | everyone (Codex directly) | Shared rules, roles, branch and handoff conventions, bridge safety rules |
| `CLAUDE.md` | Claude Code | Imports `AGENTS.md` + the builder role |
| `GEMINI.md` | Antigravity | Imports `AGENTS.md` + the UI / heavy-work role |
| `.agents/skills/civil3d-auditor/` | Codex | Civil 3D code-audit checklist |
| `.mcp.json` | Claude Code | MCP servers: graphify, c3d-bridge |
| `.codex/config.toml` | Codex | MCP servers: graphify, c3d-bridge |
| `.vscode/mcp.json` | VS Code agent mode (Copilot) | MCP servers: c3d-bridge, graphify |
| `C3DProTools.Bridge/` | Civil 3D | The live bridge plugin |

## One-time setup

### 1. Load the bridge in Civil 3D

**To try it now:** in Civil 3D 2026 type `NETLOAD` and pick
`C3DProTools.Bridge\bin\Debug\net10.0-windows\C3DProTools.Bridge.dll`.
The command line shows `C3D Bridge: listening for agents on http://127.0.0.1:47321/mcp`.

**To have it always on:** close Civil 3D, then in VS Code run the task
**Install C3D Bridge (agents <-> live Civil 3D)** (Terminal → Run Task), or run:

```powershell
.\C3DProTools.Bridge\Install-Bridge.ps1
```

From then on it loads every time Civil 3D 2026 starts. Once installed, don't also `NETLOAD` the `bin\Debug` copy;
Civil 3D would see two copies of the same plugin. To update the bridge after changing its code, close Civil 3D and
run the install again. `C3DBRIDGE` in Civil 3D shows the status and can restart or
stop it. `Install-Bridge.ps1 -Uninstall` removes it.

Only one Civil 3D window can own the port. If you run two sessions, the second shows "could not start on port 47321".
Set the `C3D_BRIDGE_PORT` environment variable before starting it if you need two bridges.

### 2. VS Code: one editor for all three

- **Claude Code:** install the *Claude Code* extension. It uses `CLAUDE.md` and `.mcp.json` automatically.
- **Codex:** install the *Codex* extension from OpenAI and sign in with the same account as your Codex app. It uses
  `AGENTS.md` and `.codex/config.toml`.
- **Antigravity** is its own editor (a VS Code fork). Open this same folder in it. You can have VS Code and
  Antigravity open on the repo side by side; just don't let two agents edit the same files at once.
- **Copilot** (optional): VS Code's own agent mode picks up `.vscode/mcp.json`.

### 3. Connect each agent to the bridge

- **Claude Code:** already in `.mcp.json`. The first time, approve the `c3d-bridge` server when asked.
- **Codex:** already in `.codex/config.toml`. Restart Codex in this folder. If your Codex version doesn't load
  project-level config, copy the `[mcp_servers.c3d-bridge]` block into `%USERPROFILE%\.codex\config.toml`.
- **Antigravity:** Agent panel → `…` → **MCP Servers** → **Manage MCP Servers** → **View raw config**, and add:

  ```json
  {
    "mcpServers": {
      "c3d-bridge": { "serverUrl": "http://127.0.0.1:47321/mcp" }
    }
  }
  ```

**Check each one** with Civil 3D open: ask the agent *"Call c3d_status and tell me what drawing is open."*

## The loop (one feature)

1. **Claude: build.** *"Task: <feature>. Branch `feature/<name>`. Follow AGENTS.md."* Claude builds, tests, and
   ends with a handoff note.
2. **You: try it live.** `NETLOAD` the rebuilt `C3D_Pro_Tools.dll` in a test drawing, or ask Claude to run the command
   through the bridge.
3. **Codex: review.** Paste the handoff note and say:
   > Review branch `feature/<name>` against `main` with the civil3d-auditor skill. Read-only: don't edit files.
   > Use c3d-bridge to check findings against the open drawing. Report each finding as
   > severity · file:line · problem · failure scenario · suggested fix.
4. **You: triage.** Decide which findings are real.
5. **Claude: fix.** *"Fix Codex findings 1, 3, 4 (pasted below). Skip 2 because <reason>."*
6. **Antigravity: UI, when the feature has a form.** *"Branch `ui/<name>` from `feature/<name>`. Redesign
   `FormX.cs` layout: <what you want>. Don't touch engine files."* Then back to Codex for a quick review.
7. **You: merge** to `main` when you're happy.

## Working inside Civil 3D through an agent

Any of the three can drive the open drawing. Examples:

- *"What pipe networks are in this drawing and how many structures does each have?"* (`c3d_list_civil`)
- *"Find every polyline on layer C-PIPE-PLAN and zoom to the longest one."* (`c3d_list_objects`, `c3d_zoom_to`)
- *"Run our network repair command on the open drawing and tell me what changed."* (`c3d_run_command`)
- *"Write a snippet that lists every pipe with slope under 0.5%."* (`c3d_execute_csharp`, read-only)

The rule in `AGENTS.md`: agents read freely, but **ask before changing the drawing**. Approving an edit in the agent's
permission prompt counts. Keep work drawings backed up anyway, and try new things on a copy.

## The bridge's tools

| Tool | Changes the drawing? | Does |
|---|---|---|
| `c3d_status` | no | Version, open drawings, units, coordinate system, object counts, running command |
| `c3d_list_civil` | no | Alignments (+profiles), surfaces, pipe networks, corridors, sites, point groups |
| `c3d_list_objects` | no | Counts by type; filtered lists with handles |
| `c3d_get_object` | no | One object's properties by handle |
| `c3d_zoom_to` | view only | Zooms to and selects objects |
| `c3d_run_command` | can | Sends a command-line string, waits until Civil 3D is idle |
| `c3d_execute_csharp` | can | Compiles and runs C# inside Civil 3D with `doc`, `db`, `ed`, `civil` |

Safety: the bridge listens on `127.0.0.1` only and rejects requests from web pages (browser `Origin`) or other hosts.
Anything on your PC can still reach it while it runs, so stop it with `C3DBRIDGE` → Stop when you're not using it, if
you prefer.

## Troubleshooting

- **Agent says the server is unavailable:** Civil 3D isn't running or the bridge isn't loaded. Run the VS Code task
  **Check C3D Bridge is reachable**: `{"jsonrpc":"2.0","id":1,"result":{}}` means it's up. Then restart the agent's
  MCP connection.
- **"Civil 3D did not respond in time":** a dialog or a command is waiting for input. Finish or cancel it in
  Civil 3D (or `c3d_run_command` with `\u001b\u001b`).
- **Install script can't copy files:** close Civil 3D first; it locks the DLL.
