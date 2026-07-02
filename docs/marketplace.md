<!--
  Visual Studio Marketplace overview. Paste this as the extension's overview/README on the Marketplace.
  It mirrors the repo README.md but: (1) every link is ABSOLUTE (the Marketplace renders with no repo
  context, so relative links 404), and (2) contributor sections (Build from source / Contributing) are
  dropped — the audience here is deciding whether to install, not contribute.
  KEEP IN SYNC with README.md when the feature list changes.
-->

# Claude Code for Visual Studio

> Bring [Claude Code](https://claude.com/claude-code) into **Visual Studio 2026** - a native diff window with accept/reject, automatic selection + compiler-diagnostics context, **live debugger access**, **semantic code navigation + decompile** (Roslyn), **a test runner that catches failures under the debugger**, and a stats panel. The `claude` CLI does the agent work; this extension is the **IDE half** of Claude Code's integration protocol.

![Demo](https://raw.githubusercontent.com/firish/claude_code_vs/main/docs/demo.gif)

**Status:** community project, not affiliated with Anthropic. **Visual Studio 2026 only**, for now.

---

## Why

Claude Code has first-class IDE integration for VS Code and JetBrains, but not Visual Studio - see [anthropics/claude-code#15942](https://github.com/anthropics/claude-code/issues/15942). This extension implements that same IDE-integration protocol natively for VS, so the CLI drives a real Visual Studio diff window and sees your selection and build errors - instead of you copy-pasting into a terminal.

## What you get

- **Native diff with a single accept/reject gate** - Claude's edits open in Visual Studio's diff viewer, and approving *there* is the only step (no duplicate y/n prompt in the terminal).
- **Reject with feedback** - reject an edit and tell Claude what to change; it reconsiders with your note.
- **Run wild (auto-accept)** - a panel toggle to apply edits without opening the diff, for when you want to let it cook. Resets each session.
- **Diagnostics sharing** - Claude reads Visual Studio's compiler errors/warnings (C# and C++) and fixes them.
- **Semantic code navigation** - Claude asks Visual Studio's compiler (Roslyn) for the *resolved* meaning of your C# code instead of grepping text: **find-all-references**, **go-to-definition**, **find-implementations**, and **call/type hierarchies**. These are the ground-truth answers text search gets wrong - indirect and interface-dispatched references, the *right* overload, explicit interface implementations, transitive callers for impact analysis. No debug session needed; it works whenever a C#/VB solution is loaded. Full reference: **[the semantic-navigation guide](https://github.com/firish/claude_code_vs/blob/main/docs/SEMANTIC.md)**.
- **Live debugger** - while you're paused at a breakpoint, Claude sees your program's runtime state (call stack, variable values, threads) and, opt-in, can *drive* the debugger - continue, step, set breakpoints, **break at the throw site of an exception**, **set a data breakpoint that breaks (or traces the full change history) the moment a value changes**, **attach to a running app** (a hosted web service or desktop app, not just F5), and **pause a hung process to untangle a deadlock** (following the lock-ownership chain across threads to the exact cycle) - to corner a bug instead of guessing from source. Full reference: **[the debugger guide](https://github.com/firish/claude_code_vs/blob/main/docs/DEBUGGER.md)**.
- **Test integration** - Claude discovers, runs, and *debugs* your unit tests through Visual Studio's Test Explorer engine: real per-test results (outcome, message, stack), re-run just the failures, and - the headline - **run a failing test under the debugger and stop at the fault**, or **hammer a flaky test until it fails and catch that iteration red-handed**, paused inside the failure. Because it's the debugger's own session, a red test becomes a live investigation. Full reference: **[the testing guide](https://github.com/firish/claude_code_vs/blob/main/docs/TESTING.md)**.
- **Selection context** - Claude automatically knows the file and lines you're looking at.
- **Live panel** - a dockable *Claude Code* panel: connection status, edit decisions, and **token usage + estimated cost** (latest call vs cumulative session).

## Watch it debug

The headline of 1.2.0. Pause at a breakpoint, ask Claude what's wrong, and with driving turned on it sets breakpoints, steps through your code, and reads the runtime values to catch bugs that never show up in the output.

Here it is on a scoring function that returns the wrong total. It stopped inside the loop and stepped through the rounds, watching a counter that should reset but didn't:

![Claude paused in the Visual Studio debugger, reading live values](https://raw.githubusercontent.com/firish/claude_code_vs/main/docs/images/combo-debugger.png)

It kept a running trace of what it saw, which is how it cornered the bug:

![The runtime trace Claude built while stepping](https://raw.githubusercontent.com/firish/claude_code_vs/main/docs/images/combo-trace.png)

Then the fix opened in the native diff, ready to accept or reject:

![Claude's fix in the Visual Studio diff viewer](https://raw.githubusercontent.com/firish/claude_code_vs/main/docs/images/combo-diff-viewer.png)

Full walkthrough, the complete tool list, and the limitations are in **[the debugger guide](https://github.com/firish/claude_code_vs/blob/main/docs/DEBUGGER.md)**.

It's grown well past stepping since. Claude can now **attach to a running app** (debug a hosted web service or an already-running desktop app, not just F5), **break at the origin of an exception** instead of the catch that swallows it, and **pause a hung process to untangle a deadlock** — reading the lock-ownership chain across threads to pin the exact cycle.

And in 1.8.1: **managed data breakpoints** — point Claude at a field and it breaks, or traces the *complete* change history (every old→new value, in order), the instant the value changes — conditionally (`> 700`), on every change, on several values at once. That's "break when this value changes," a watch Visual Studio's own UI can't set programmatically and that has no automation API — so it's genuinely new ground for catching the write that corrupts your state.

## Read your code the way the compiler does (1.9.0)

Most assistants navigate code by **grepping text** — which misses indirect references and over-counts on comments, strings, and same-named symbols. This release gives Claude Visual Studio's **resolved semantic model** (Roslyn): the same "Find All References / Go to Definition / Find Implementations / Call Hierarchy" the IDE uses, as ground truth instead of a guess. Ask "who calls this?" or "what implements `IFoo`?" and it resolves through interfaces, overrides, explicit interface implementations, and overloads — the cases text search gets wrong.

The headline is **decompile**: the one thing reading your repo *fundamentally can't* give you — the **body of a method inside a referenced DLL**. Point Claude at a framework or NuGet call (`JsonConvert.SerializeObject`, `Enumerable.Where`) and it returns the real decompiled C#, the way Go-to-Definition does. For core .NET types it even fetches the **actual `dotnet/runtime` source** via SourceLink. No more guessing what a library call does from its name.

It needs **no debugger session** — it works any time a C#/VB solution is open. Full tool list, the addressing model, and worked workflows are in **[the semantic-navigation guide](https://github.com/firish/claude_code_vs/blob/main/docs/SEMANTIC.md)**.

## Catch a failing test — even the flaky ones (1.10.0)

`dotnet test` runs tests; the CLI can already do that. What it *can't* do is **stop inside a failing test in Visual Studio's debugger**, or **reproduce a heisenbug on purpose and pause on it**. This release gives Claude Visual Studio's own **Test Explorer engine wired to the live debugger** — a closed **fix → verify → catch** loop:

- **Run** — real per-test `outcome` + `errorMessage` + stack (data, not a text blob), one test or all, with code coverage.
- **Re-run failures** — after a fix, re-run *only* the tests that failed, not the whole suite.
- **Debug one** — launch a single failing test under the debugger and break at the **throw site** with `$exception` and locals live.
- **Catch red-handed** — loop a flaky test under the debugger until the failing iteration halts on its own, leaving you *paused inside the failure* with the state that caused it — the one motion neither `dotnet test` nor a re-run loop can do.

The run tools compose with the debugger tools (it's one session), so an intermittent red line goes from "can't reproduce" to "the debugger is paused on it." Full tool list and the worked flow are in **[the testing guide](https://github.com/firish/claude_code_vs/blob/main/docs/TESTING.md)**.

## Requirements

- **Visual Studio 2026.**
- **The Claude Code CLI**, installed and authenticated - see the [Claude Code docs](https://docs.claude.com/claude-code). *This extension makes no model calls and does no agent work itself; it requires the CLI.*
- Tested against `claude` **2.1.191**.

## Install

- **From here:** click **Download** / **Install**, or in VS open **Extensions -> Manage Extensions** and search *"Claude Code for Visual Studio"*.
- **Sideload:** download the `.vsix` from [GitHub Releases](https://github.com/firish/claude_code_vs/releases) and double-click it.

## Quickstart

1. Open your project or solution in Visual Studio 2026.
2. Open the **Claude Code** panel (**View -> Other Windows -> Claude Code**) and click **Launch Claude Code** (also available on the **Tools** menu).
3. A terminal opens running `claude`, already connected to the IDE (no `/ide` needed). The panel pill turns green - **Connected**.
4. Ask Claude to make a change. Its edit opens as a **diff** - click **Accept**, **Reject**, or **Reject with feedback…**.

> Diagnostics need a **loaded project** (not a loose file in Open-Folder mode) for the compiler to analyze it.

> To let Claude debug: set a breakpoint, tick **Allow Claude to drive debugger** in the panel, start debugging, then ask it to investigate. Reading runtime state works without the toggle; the toggle only gates *driving* execution.

## How it works

This is a **protocol bridge**, not a re-implementation of the agent. On Launch it starts a localhost WebSocket server, launches `claude` already connected to the IDE, and implements the IDE tools the CLI drives (the diff, diagnostics, selection updates). A small **PreToolUse hook** routes proposed edits through the VS diff so approving there is the only gate. Debugger access, the test runner, and semantic navigation are exposed as extra MCP servers (`vs-debug` — the debugger *and* the test tools, since they compose; `vs-semantic` — Roslyn) the CLI reaches through a tiny stdio shim. **The CLI does all agent work; the extension never makes model calls.**

## Privacy & security

- The bridge binds to **127.0.0.1 only** and validates an **auth token** (from a lockfile) on every connection. The token is never logged.
- The extension makes **no network calls and no LLM calls of its own**. All AI work is the `claude` CLI, under your own authentication.
- On **Launch**, it writes a few helper scripts into your workspace's `.claude/` folder and merges hook entries into `.claude/settings.json` (preserving existing content): the edit-gate hook, a token-usage reporter, a break-state hook, and a stdio shim for the `vs-debug` / `vs-semantic` MCP servers (registered in your workspace `.mcp.json`).
- **Token cost is an estimate** (hardcoded per-tier prices), shown only when you click *Show est. cost*.

## Limitations & known issues

- **Visual Studio 2026 only** for now (a VS 2022 backfill is planned if there's demand).
- The IDE-integration protocol is **undocumented and version-fragile** - a `claude` update could change it. Known-good: 2.1.191.
- **Diagnostics need a loaded project** (the Error List / Roslyn won't analyze loose files).
- **Semantic navigation is C#/VB and needs a loaded project** - it reads the Roslyn workspace, so it sees the solution open in VS (not the CLI's working directory if they differ), and doesn't cover C++ navigation or loose files.
- **Debugger features target managed (.NET) code.** Reading runtime state is always on; driving execution is opt-in. Native/C++ runtime inspection isn't covered.
- **Test integration is managed (.NET) test projects** and needs a loaded solution. Coverage works; profiling is deferred, and the debug/flaky-catch tools are opt-in behind the debugger-drive toggle. See **[the testing guide](https://github.com/firish/claude_code_vs/blob/main/docs/TESTING.md)**.
- Token stats refresh **on edits** (the reliable hook trigger), so a chat-only turn may not update them immediately.
- Cost figures are **estimates**, not billing.

## Troubleshooting

- **Panel says "Waiting for CLI":** click **Launch Claude Code**, or run `/ide` in a `claude` terminal and pick *Visual Studio*.
- **New files land in the wrong folder:** launch from the extension (it pins the working directory to your workspace), or run `claude` from inside the repo.
- **getDiagnostics returns nothing:** open the code as a **project** and confirm the error appears in the **Error List**.
- **Filing a bug:** include the **Output -> Claude Code** pane contents and your `claude --version`.

---

Source, full documentation, and issue tracker: **[github.com/firish/claude_code_vs](https://github.com/firish/claude_code_vs)**.

[MIT](https://github.com/firish/claude_code_vs/blob/main/LICENSE) © 2026 Rishi Gulati. Not affiliated with Anthropic. "Claude" and "Claude Code" are trademarks of Anthropic, used here only to describe interoperability.
