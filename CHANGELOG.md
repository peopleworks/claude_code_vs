# Changelog

## 1.2.0 - 2026-06-17

Headline: **live debugger integration** — Claude can now see your program's runtime state, and (opt-in) drive the debugger to corner a bug instead of guessing from source. Full reference: [`docs/DEBUGGER.md`](docs/DEBUGGER.md).

### Features

- **Debugger awareness (push)** - when you submit a prompt while paused at a breakpoint, a `UserPromptSubmit` hook injects the live break state (stop location, call stack, current-frame arguments and locals with values) into Claude's context. No tool call needed; gated on break mode so normal turns stay quiet.
- **Runtime inspection (pull)** - a second MCP server, `vs-debug`, exposes on-demand read tools the model can call mid-turn: `vs_debug_state`, `vs_evaluate`, `vs_expand` (drill into an object graph), `vs_get_frame_locals`, `vs_list_breakpoints`, `vs_threads`. Reached via a tiny stdio shim auto-registered in the workspace `.mcp.json`; the tool logic runs in-proc against EnvDTE.
- **Drive the debugger (opt-in)** - behind a new **"Allow Claude to drive debugger"** panel toggle (default off, resets each session): `vs_continue`, `vs_step_over`/`into`/`out`, `vs_run_to_line`, `vs_set_breakpoint` (with condition + hit count), `vs_remove_breakpoint`, `vs_freeze_thread`, `vs_set_next_statement`, and `vs_start_debugging`/`vs_stop_debugging`. An await-break engine (`IVsDebuggerEvents.OnModeChange` + a parked completion) returns the new state after each step without blocking the UI thread.
- **Truncation signaling** - capped results (call stack, locals, threads, expanded members) now carry a `{truncated: true, …}` marker so Claude knows data was cut and can narrow its query, instead of silently seeing a partial picture.
- **Panel debugger stat** - the dockable panel's stats card now shows session attribution: *N inspected · M driven*.
- **Multi-instance + reconnect hardening** - hooks pick the most-specific workspace lockfile whose port is actually listening (defeats parent-folder shadowing and zombie lockfiles); lockfiles record `pidStartTime` so a recycled PID can't make a dead instance look alive; orphaned diffs are rejected and closed when the CLI disconnects.

### Known limitations

- Managed (.NET) debugging only; native/C++ runtime inspection is out of scope.
- `vs_evaluate` can't evaluate LINQ/lambda expressions (VS evaluator limitation).
- `vs_threads` gives per-thread stacks but not lock/wait-chain ownership.
- Break-on-thrown exceptions and native tracepoints are not yet implemented (planned - see `ROADMAP.md`).
- Tested against `claude` 2.1.181.

---

## 1.0.1 - 2026-06-15

### Fixes

- Fixed README demo GIF path to absolute URL so it renders on the VS Marketplace listing.
- Added VS Marketplace and GitHub Releases links to README.
- Added VSIX icon and preview image.
- Corrected manifest publisher display name and version range floor for Marketplace compliance.

---

## 1.0.0 - 2026-06-15

Initial release. Implements the full Claude Code IDE-integration protocol for Visual Studio 2026.

### Features

- **Native diff with single-gate accept/reject** - Claude's proposed edits open in Visual Studio's diff viewer (not a terminal y/n). A PreToolUse hook intercepts every Edit/Write/MultiEdit and routes it through the diff; the CLI writes the file only after you accept. No duplicate terminal prompt.
- **Reject with feedback** - an InfoBar action in the diff that prompts for a reason; the reason is returned to the CLI as `permissionDecisionReason` so Claude reconsiders.
- **Run wild (auto-accept)** - a panel checkbox that allows edits without opening the diff, for unattended sessions. Resets each VS session.
- **Diagnostics sharing** - `getDiagnostics` reads the VS Error List (Roslyn for C#, MSVC toolchain for C++) and returns LSP-shaped diagnostics so Claude can see and fix your build errors.
- **Selection context** - a `selection_changed` notification (150 ms debounce) keeps Claude aware of the active file and highlighted lines in real time.
- **One-click Launch** - *Tools -> Launch Claude Code* opens a terminal pre-wired with `ENABLE_IDE_INTEGRATION` and the bridge port, working directory set to the solution root. Auto-installs the permission and usage hooks on first launch.
- **Dockable Claude Code panel** - connection status pill, accept/reject edit counts, token usage (input / output / cached) and estimated cost (latest call + cumulative session), pending-diff strip, and a curated activity feed. VS-theme-aware (dark/light).
- **Full 12-tool parity** - all tools advertised in `tools/list`: `openFile`, `openDiff`, `getCurrentSelection`, `getLatestSelection`, `getDiagnostics`, `getOpenEditors`, `getWorkspaceFolders`, `checkDocumentDirty`, `saveDocument`, `close_tab`, `closeAllDiffTabs`. `executeCode` returns an honest MCP error (no VS equivalent).
- **RDT-aware write-back** - accepting an edit updates an open editor buffer in place (no reload prompt) via `IVsRunningDocumentTable`.
- **Lockfile lifecycle** - stale (dead-PID) lockfiles are reaped on startup; the lockfile is deleted on clean shutdown and on an unexpected server fault.
- **WorkspaceWatcher** - keeps the lockfile's `workspaceFolders` in sync as solutions open/close so `/ide` always matches the current working directory.

### Known limitations

- Visual Studio 2026 only (VS 2022 backfill planned - see `ROADMAP.md`).
- Diagnostic ranges are point ranges (Error List only exposes line/column); Roslyn-precise spans are a future enhancement.
- The IDE-integration protocol is undocumented and version-fragile. Tested against `claude` 2.1.173.
- Token stats refresh on edits (the reliable hook trigger); a chat-only turn may not update them immediately.
- Cost figures are estimates (hardcoded per-tier list prices), not billing.
