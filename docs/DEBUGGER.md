# Live debugger integration

Most coding assistants see only your *source*. This extension also gives Claude your program's **runtime state** — where execution is paused, the call stack, variable values, threads — and, opt-in, lets it **drive** the debugger (continue, step, set breakpoints, start/stop a session) to corner a bug instead of guessing from the code.

The `claude` CLI does all the agent work; the extension exposes Visual Studio's live debugger to it over the same localhost bridge that powers the diff and diagnostics features.

---

## How it reaches the model: three channels

A new IDE tool wouldn't help here. Claude Code's IDE-integration protocol (the WebSocket the CLI connects to) is **CLI-curated** — it surfaces only `getDiagnostics` (+ `executeCode`) to the model and drives the rest itself, so a 13th tool added there would never be called. Debug state therefore reaches the model through the two channels that *do*: a **hook** (push) and a **user MCP server** (pull). Driving rides the same MCP server behind a safety gate.

### 1. Push — break state at prompt time (`UserPromptSubmit` hook)

When you submit a prompt while the debugger is paused, a `UserPromptSubmit` hook (`vs-debug-context-hook.ps1`) POSTs to the bridge's `/debug-context` endpoint. The bridge reads the live break state via EnvDTE on the UI thread and hands it back; the hook injects it as `hookSpecificOutput.additionalContext`. So Claude starts the turn already knowing where you're stopped and what the values are — no tool call required. Gated on break mode: if you're not paused, nothing is injected (no noise on normal turns).

### 2. Pull — inspect on demand (the `vs-debug` MCP server)

The bridge exposes a **second MCP server** at `POST /mcp` on its localhost `HttpListener`. The CLI reaches it through a tiny stdio shim (`vs-mcp-shim.ps1`) registered in your workspace `.mcp.json` under the server name **`vs-debug`**. The shim discovers the live bridge (the most-specific workspace lockfile whose port is listening — the same hardened selection the other hooks use) and proxies newline-delimited JSON-RPC to `/mcp`. The tools themselves run **in-proc in C#** against EnvDTE; the shim is a dumb pipe.

Why a separate MCP server instead of the IDE channel? Because a user-registered `.mcp.json` server is the *open plugin door* — the CLI surfaces **all** of its tools to the model — whereas the IDE channel is a closed, curated protocol. Same `McpServer` dispatch code, a different relationship with the CLI.

### 3. Drive — control execution (same server, gated)

Execution control sits behind a panel toggle, **"Allow Claude to drive debugger"** (default **OFF**, in-memory, resets each VS session). When off, the drive tools refuse and do nothing. The hard part — a drive command is asynchronous (issue "step", then *wait for the next break*) — is handled by an await-break engine: issue the EnvDTE command with `WaitForBreakOrEnd=false` (never blocks the UI thread), subscribe to `IVsDebuggerEvents.OnModeChange`, park a `TaskCompletionSource`, and complete it on the next Break (return the fresh snapshot) or Design (program ended). A 20 s timeout reports "still running" rather than hanging.

```
                 prompt-submit          on demand                control (gated)
  Claude (CLI) ──UserPromptSubmit──▶  ──stdio JSON-RPC──▶ vs-mcp-shim.ps1
       │              hook                 (.mcp.json)            │ HTTP POST /mcp
       │                │                                        ▼
       ▼                ▼                                  IdeWebSocketServer (127.0.0.1, auth)
  /debug-context ◀──────┘                                        │
       └──────────────────────────┬─────────────────────────────┘
                                   ▼
                      DebuggerReader / DebuggerDriver  ──EnvDTE / IVsDebugger──▶  VS debugger (UI thread)
```

---

## Tool catalog

All tools live on the `vs-debug` MCP server and appear to the model as `mcp__vs-debug__*`. **Reads are ungated; drives require the "Allow Claude to drive debugger" toggle.** Most require the debugger to be paused (break mode); the snapshot otherwise reports `{"mode":"run|design|unknown"}`.

### Inspect (read-only, ungated)

| Tool | What it returns |
|---|---|
| `vs_debug_state` | Mode, stop location, call stack (innermost first), current frame's args + locals with values. |
| `vs_list_breakpoints` | All breakpoints (file, line, function, enabled, hit count, condition). Works in **any** mode. |
| `vs_get_frame_locals` | Args + locals for a specific call-stack `frameIndex` (walk up to callers). |
| `vs_evaluate` | Evaluate an expression in a chosen frame → `{value, type, isValid}`. |
| `vs_expand` | Drill into an object graph (`Expression.DataMembers`) to a depth → `{name,type,value,children}` tree. |
| `vs_threads` | Every thread with its call stack + location; the current thread is flagged. |

### Drive (execution & breakpoints, gated)

| Tool | Action |
|---|---|
| `vs_continue` | Resume to the next breakpoint or program end, then return the new state. |
| `vs_step_over` / `vs_step_into` / `vs_step_out` | The three step modes, each awaiting the next break. |
| `vs_run_to_line` | Run to a `file:line` (temporary breakpoint under the hood). |
| `vs_set_breakpoint` | Set at `file:line`, with optional `condition` and `hitCount`/`hitCountType` (`equal`/`atLeast`/`multiple`). |
| `vs_remove_breakpoint` | Clear the breakpoint(s) at a `file:line`. |
| `vs_freeze_thread` | Freeze (suspend) or thaw a thread by id — isolate one thread in a race. |
| `vs_set_next_statement` | Move the execution pointer to a line without running the code in between (current method only). |
| `vs_start_debugging` / `vs_stop_debugging` | Start a session (F5, runs to the first breakpoint) / stop it (Shift+F5). |

### Push (no tool call)

The `UserPromptSubmit` hook injects the current break state (stop location, call stack, current-frame args/locals) into context whenever you submit a prompt while paused.

---

## Safety

- **Reads are always allowed**; **execution and breakpoint mutation are opt-in** via the panel toggle (default OFF, resets every VS session, so model-controlled execution is never silently left on). This mirrors the "Run wild (auto-accept)" toggle used for edits.
- Driving **runs your code** under model control — continue/step execute your program, and `vs_evaluate` of a method call has side effects (there is no read-only eval). That's why it's gated.
- `vs_set_next_statement` is genuinely powerful and risky (skipping initialization can corrupt state) — also behind the gate.

---

## Limitations

- **Managed (.NET) focused.** The debug reader targets the managed (CLR) debugger via EnvDTE. Native/C++ runtime inspection is not covered (C++ *build* diagnostics still flow through the Error List).
- **`vs_evaluate` has no LINQ / lambdas.** VS's expression evaluator rejects `list.Select(x => …)`. Prefer indexing, field/property access, `.Count`, `.Sum()`, `object.ReferenceEquals(a, b)`, arithmetic.
- **`vs_threads` shows stacks, not wait-chains.** You get each thread's call stack + location, but EnvDTE doesn't expose lock/wait ownership ("thread 5 is blocked on the lock held by thread 9") or a frozen-state flag.
- **`vs_set_next_statement` is current-method only** and moves the editor caret as a side effect (there's no direct API; it's driven through the caret + the `Debug.SetNextStatement` command).
- **Per-frame source lines are partial.** The call stack is function names; the stop file/line is the current frame only. Precise per-frame source (`IDebugStackFrame2`) is a future enhancement.
- **Output is capped, but signaled.** Large results are bounded (call stack 20 frames, locals 60, value 240 chars, threads 60, …) — but when a cap truncates, the output includes a `{"truncated": true, "note": "capped at N…"}` marker so the model knows data was cut and can narrow its query (or pass a larger `depth` to `vs_expand`). Values self-signal with a trailing `…`.
- **Break-on-thrown is not yet available.** First-chance "break where this exception originates" needs the lower-level COM debug-engine API (`IDebugEngine2.SetException`); the managed EnvDTE exception surface isn't present. Deferred — see below.
- **No native tracepoints** (log-and-continue breakpoints) yet.
- **EnvDTE is version-fragile** at the edges and throws readily during debugger transitions; every access is individually guarded, but a transient read can come back partial.

---

## Try it

Four runnable fixtures under `demo/` exercise the feature (open the `.sln`, enable the drive toggle where noted, Launch Claude Code):

- **`CheckoutBuggy`** — an integer-division discount bug; the push hook lets Claude diagnose from the paused locals.
- **`SignalScan`** — an aliasing bug confirmable at one paused point (`vs_evaluate('object.ReferenceEquals(windows[0], windows[2])')`). No bug-revealing comments.
- **`ComboScore`** — a missing state reset that's invisible in the final state; forces stepping / a conditional breakpoint to watch `combo` across the bad iteration.
- **`NullOrigin`** — an NRE thrown deep and swallowed by a generic catch; staged for break-on-thrown once it lands.

---

## Next version

- **Break-on-thrown exceptions** — first-chance break at the throw site, via the COM `IDebugEngine2.SetException` path (the `NullOrigin` fixture is ready to validate it).
- **Test-driven debugging loop** — run the test suite; on a failing test, set a breakpoint at the fault, `vs_start_debugging` that test, and drive to the failure automatically. This composes the whole surface into an autonomous diagnose loop.
- **Native tracepoints** — log-and-continue probes Claude can sprinkle without editing the file (either VS-native if reachable, or simulated in our layer).
- **CPU / memory profiling** — `dotnet-counters` (live CPU %, GC, alloc rate), `dotnet-trace` (top hot methods), and `dotnet-gcdump` (top types by size) against the debuggee PID, surfaced as tools.
- **Per-frame source** via `IDebugStackFrame2.GetDocumentContext`.
