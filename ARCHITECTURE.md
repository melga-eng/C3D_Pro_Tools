# Architecture and coordination standard

Applies to new and refactored features. Existing features migrate one operation at a time. The user assigns work and authorizes integration; no agent assigns another agent work or merges branches. Roles and drawing permissions remain in `AGENTS.md`.

## Responsibilities and ownership

| Area | Owns | Owner |
|---|---|---|
| Presentation | Forms, control binding, layout, previews, user messages | Antigravity |
| Application | Request validation, operation orchestration, progress and result contracts | Claude |
| Civil 3D integration | Drawing reads/writes, document binding, locks, transactions, API adapters | Claude |
| Engines | Geometry, calculations, planning and engineering rules using plain data | Claude; Codex for the engines the user assigns to it |
| Verification | Independent review of the integrated diff and checks | Codex; Claude for Codex-built engines |

UI calls application operations. Application operations coordinate engines and Civil 3D adapters. Engines and contracts do not reference forms or the Autodesk API. Autodesk objects stay inside the integration boundary. UI selection and host command entry points delegate drawing access to that boundary.

Keep the existing projects initially. Introduce feature folders as code is extracted, rather than moving unrelated files. No change to frameworks, packaging or public command names is part of this standard.

## Contracts

- Before implementation, agree on request, preview, progress and result types for the operation. Record signatures, units, defaults, validation, cancellation and atomicity in its task note.
- Requests identify the intended drawing, selected objects and options. Use plain immutable values; drawing-scoped opaque identifiers are resolved and validated by the Civil 3D adapter. Do not expose `DBObject`, `Transaction`, `Editor`, forms or controls.
- Previews describe proposed changes without committing drawing edits. Apply revalidates document identity, object existence and relevant state; reject a stale plan and request a new preview.
- Pass explicit units in field names or documentation. Validate finite numeric values and engineering limits in the application layer; UI validation gives immediate feedback but is not authoritative.
- Contracts have one assigned owner (Claude by default). Both implementations use the same reviewed contract commit. A contract change requires a coordinated update before dependent work continues.

## Drawing and transaction rules

- Application operations own the transaction policy; integration code performs Autodesk calls on the host main thread. Background workers receive plain snapshots only.
- Bind every operation to its intended document. Validate it again immediately before writing. Modeless and external writes acquire a document lock, then transactions in `using` scopes.
- Declare atomicity before applying: one operation rolls back entirely, or each batch item is atomic. Batch success is reported only for committed items; partial completion is explicit.
- A failed required mutation, deletion or connection must reach the transaction owner and abort that atomic unit. Verify required postconditions before commit. Never log an error and continue to commit an incomplete unit.
- Dispose each transaction and reopen objects in subsequent transactions. Return plain results and identifiers, never opened Autodesk objects. Restore in-memory counts and caches after rollback too.
- Cancellation before execution means no changes. Check cancellation at safe boundaries; either abort the current unit or finish it and report committed items. A timeout after work starts has an unknown outcome until re-read; do not retry automatically.
- Drawing transactions do not roll back catalog/filesystem writes. Operations involving files define staging, backup or compensation explicitly. Verify UNDO behavior live rather than assuming a transaction creates one UNDO step.

## Error and result conventions

Results carry a status, committed change counts, item outcomes, warnings and issues with stable codes and object/item identifiers. User text explains the problem and next action; technical exceptions are logged with an operation identifier.

| Status | Meaning |
|---|---|
| Succeeded | All requested changes committed and required postconditions passed |
| NoChange | Valid operation; no edits needed |
| Rejected | Invalid input, wrong drawing or stale preview; nothing applied |
| Cancelled | Stopped at a known boundary; state whether anything committed |
| RolledBack | The atomic unit failed and its edits were undone |
| PartiallySucceeded | Some independent batch items committed; identify each outcome |
| OutcomeUnknown | Work started but its final state is not known; re-read before retrying |

Expected failures become structured issues at the application boundary. Unexpected exceptions abort the current atomic unit and are logged. Never display success before commit or claim rollback if rollback itself could not be verified. Lower layers do not open dialogs; the UI renders results.

## UI conventions

- Reuse `StyleUi` and `ToolUi` for fonts, colors, cards, fields and buttons. Shared helper changes are separately assigned because they affect multiple forms.
- Use consistent actions: Preview, Apply, Cancel and Close. Identify the target drawing and selected item count before applying; destructive previews state what will be removed or replaced.
- Display units beside numeric fields. Reuse existing number parsing and validate on the application side too. Explain errors beside the field where practical.
- Disable duplicate Apply actions while work is running. Keep progress and cancellation available when supported. Closing a form must not silently retry or abandon an unknown write outcome.
- Keep event handlers thin: collect input, call an operation, render its result. They do not own engineering calculations, document transactions or success/failure policy.
- Verify keyboard navigation, readable status text, resizing, and 100%, 125% and 150% scaling. Do not convey status by color alone. Keep designer/resource files consistent with their form.

## Coordinating a task

1. User assigns scope, acceptance criteria, file ownership and the contract owner.
2. Claude defines the operation contract; Codex reviews it; the user authorizes the implementation scope.
3. When the user assigns concurrent work, use separate worktrees and branches from the same contract commit. Never edit the same file concurrently, including different partial classes stored in that file. During extraction, reserve the existing form for Claude; UI redesign begins after that boundary is integrated.
4. Each handoff identifies the base commit, changed files, contract version, checks and limitations. Contract mismatches or ownership conflicts go back to the user before dependent edits.
5. Build and test each contribution and the combined result. Codex reviews the combined diff, and Claude reviews each Codex engine branch before it is combined. The user decides integration and release; a passing offline suite does not replace drawing integration tests.

## First implementation: Network Editor split

Start after the audit fixes are committed and available on the agreed integration base. Suggested branch: `feature/network-split-boundary`.

| Location | First change | Owner |
|---|---|---|
| `C3D_Pro_Tools/Features/Networks/Contracts/` | Split request, plan and result types, including drawing identity and atomicity | Claude |
| `C3D_Pro_Tools/Features/Networks/Application/` | Split workflow callable by the form; validate, preview, apply and report | Claude |
| `C3D_Pro_Tools/Features/Networks/Civil3D/` | Document binding, reads, locking and transactional split execution | Claude |
| `C3D_Pro_Tools/FormNetworkEditor.cs` | Delegate only the split operation to the workflow; preserve behavior/layout | Claude during extraction |
| `C3D_Pro_Tools/NetworkEditOps.cs` | Reuse existing split logic through the adapter; preserve other callers | Claude |
| `tests/C3D_Pro_Tools.Tests/` | Meaningful validation/result tests and existing engine regressions | Claude |

This is the target placement, not a claim those folders already exist. Keep the engine where it is until a separate move is justified. Preserve the `NetworkFromLines` caller of `ApplySplit`.

Acceptance: the form no longer owns split transactions; contracts have no Autodesk/UI dependencies; wrong-document and stale-plan requests are rejected; failed creation or reconnection restores the original pipe; a failed batch item does not corrupt later items; success and cancellation report committed state accurately. Run `build.ps1 -Configuration Debug`, update graphify after C# edits, and verify rollback, connections and UNDO in an authorized disposable Civil 3D drawing after reloading the DLL.

After this extraction passes review, the user can assign Antigravity `ui/network-split` to refine presentation against the stable contract. Use the accepted feature as the reference for the next operation.
