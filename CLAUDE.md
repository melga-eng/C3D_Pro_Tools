# C3D_Pro_Tools

Civil 3D 2026 (.NET 10) plugin. C# source is in `C3D_Pro_Tools/`, the WiX installer in `C3DProInstaller/`, and the build script is `build.ps1`.

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

Use the `c3d-api` plugin skills and MCP tools (`lookup_type`, `search_api`, `get_parameters`, …) to verify Civil 3D / AutoCAD .NET API names and signatures instead of guessing.
