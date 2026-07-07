<!--
  Visual Studio Marketplace overview. Paste this as the extension's overview/README on the Marketplace.
  It mirrors the repo README.md but: (1) every link is ABSOLUTE (the Marketplace renders with no repo
  context, so relative links 404), (2) the hero is the GIF, not a <video> tag (the Marketplace does not
  render <video>), (3) the in-page "jump to" nav and the contributor sections (Build from source /
  Contributing) are dropped. KEEP IN SYNC with README.md when the feature list changes.
-->

# Claude Code for Visual Studio

Bring [Claude Code](https://claude.com/claude-code) into **Visual Studio 2026**. The `claude` CLI does the agent work. This extension is the IDE half of Claude Code's integration protocol: a native diff window with accept and reject, automatic selection and compiler-diagnostics context, a live debugger Claude can read and drive, Roslyn code navigation with decompile, and a test runner that catches failures under the debugger.

![Claude drives the Visual Studio debugger, stepping through a scoring loop to find a bug that never shows in the output, then its fix opens in the diff viewer](https://raw.githubusercontent.com/firish/claude_code_vs/main/docs/images/debugger-hero.gif)

*A fresh Claude session driving the Visual Studio debugger to find a bug that is invisible in the output, then opening the fix in the native diff.*

**Status:** community project, not affiliated with Anthropic. Visual Studio 2026 only for now. Tested against `claude` 2.1.191.

## Why

Claude Code ships first-class IDE integration for VS Code and JetBrains, but not Visual Studio. This extension implements that same integration protocol natively for VS, so the CLI drives a real Visual Studio diff window and reads your selection and build errors instead of you copy-pasting into a terminal.

The demand for this is on the Claude Code tracker. These requests ask for what the extension provides:

- **Visual Studio support:** [#15942](https://github.com/anthropics/claude-code/issues/15942) for VS 2026, and [#70516](https://github.com/anthropics/claude-code/issues/70516) for VS 2022+.
- **A debugger Claude can use:** [#13865](https://github.com/anthropics/claude-code/issues/13865) for an interactive debug mode aimed at hard-to-reproduce runtime bugs, and [#27110](https://github.com/anthropics/claude-code/issues/27110) to expose debugger state, the variables and call stack, to Claude.
- **Reaching more IDEs, and stronger C#/.NET code intelligence:** [#1234](https://github.com/anthropics/claude-code/issues/1234) for IDEs beyond VS Code and JetBrains, and [#16360](https://github.com/anthropics/claude-code/issues/16360) for the C# code intelligence our Roslyn navigation provides.

## What you get

- **Native diff with a single accept/reject gate** - Claude's edits open in Visual Studio's diff viewer, and approving there is the only step (no duplicate y/n prompt in the terminal).
- **Reject with feedback** - reject an edit and tell Claude what to change, and it reconsiders with your note.
- **Run wild (auto-accept)** - a panel toggle that applies edits without opening the diff, for when you want to let it cook. Resets each session.
- **Diagnostics sharing** - Claude reads Visual Studio's compiler errors and warnings (C# and C++) and fixes them.
- **Semantic code navigation** - Claude asks Visual Studio's compiler (Roslyn) for the resolved meaning of your C# instead of grepping text: find-all-references, go-to-definition, find-implementations, and call/type hierarchies. These are the ground-truth answers text search gets wrong, like indirect and interface-dispatched references, the right overload, explicit interface implementations, and transitive callers for impact analysis. No debug session needed; it works whenever a C#/VB solution is loaded. Full reference: [the semantic-navigation guide](https://github.com/firish/claude_code_vs/blob/main/docs/SEMANTIC.md).
- **Live debugger** - while you are paused at a breakpoint, Claude sees your program's runtime state (call stack, variable values, threads) and, opt-in, can drive the debugger: continue, step, set breakpoints, break at the throw site of an exception, set a data breakpoint that breaks (or traces the full change history) the moment a value changes, attach to a running app (a hosted web service or desktop app, not just F5), and pause a hung process to untangle a deadlock by following the lock-ownership chain across threads to the exact cycle. Full reference: [the debugger guide](https://github.com/firish/claude_code_vs/blob/main/docs/DEBUGGER.md).
- **Test integration** - Claude discovers, runs, and debugs your unit tests through Visual Studio's Test Explorer engine: real per-test results (outcome, message, stack), re-run just the failures, and run a failing test under the debugger to stop at the fault, or hammer a flaky test until it fails and catch that iteration red-handed, paused inside the failure. Because it is the debugger's own session, a red test becomes a live investigation. Full reference: [the testing guide](https://github.com/firish/claude_code_vs/blob/main/docs/TESTING.md).
- **Selection context** - Claude automatically knows the file and lines you are looking at.
- **Live panel** - a dockable Claude Code panel: connection status, edit decisions, and token usage with estimated cost (latest call vs cumulative session).

## A closer look

### A native diff with one approval step

Claude's edits open in Visual Studio's own diff viewer, and approving there is the only step. There is no second yes/no prompt in the terminal. You can reject an edit and leave a note, and Claude reconsiders with your feedback.

![A Claude edit open in the Visual Studio diff viewer with Accept, Reject, and Reject with feedback buttons](https://raw.githubusercontent.com/firish/claude_code_vs/main/docs/images/diff-reject.png)

### A debugger Claude can drive

While you are paused at a breakpoint, Claude reads your program's runtime state and, with driving turned on, steps and sets breakpoints to corner a bug. The clip at the top is a scoring function that returns the wrong total. Claude stopped inside the loop, stepped through the rounds, and watched a counter that should reset but did not. It kept a running trace of what it saw at each round:

![The round-by-round trace Claude built while stepping, showing the combo counter failing to reset on a zero round](https://raw.githubusercontent.com/firish/claude_code_vs/main/docs/images/combo-trace.png)

It has grown past stepping. Claude can attach to a program that is already running, break at the throw site of an exception instead of the catch that swallows it, and pause a hung process to walk a deadlock back to the exact cycle. Full tool list in [the debugger guide](https://github.com/firish/claude_code_vs/blob/main/docs/DEBUGGER.md).

### Break when a value changes

Point Claude at a field and it stops the moment that field is written, or traces every value the field takes, in order. It can watch conditionally and on several fields at once. Visual Studio's own UI can set this, but there is no automation API for it, so the extension arms it through a bundled debug-engine component.

![A conditional data breakpoint stopping the instant an order total goes negative, with the change history listed](https://raw.githubusercontent.com/firish/claude_code_vs/main/docs/images/data-brk-conditional.png)

The full data-breakpoint reference is in [the debugger guide](https://github.com/firish/claude_code_vs/blob/main/docs/DEBUGGER.md#break-when-a-value-changes-data-breakpoints).

### Catch a failing test, even the flaky ones

Claude discovers, runs, and debugs your tests through Visual Studio's Test Explorer engine, with real per-test results. The test tools sit on top of the debugger, so Claude can launch one failing test and stop at the throw with the exception and locals in view, or loop a flaky test until the failing run happens and leave you paused inside it.

![The Visual Studio debugger paused inside a flaky test at the throw site, with the exception live in the frame](https://raw.githubusercontent.com/firish/claude_code_vs/main/docs/images/flaky-caught.png)

Full tool list and the worked flow are in [the testing guide](https://github.com/firish/claude_code_vs/blob/main/docs/TESTING.md#catch-a-flaky-test-red-handed).

### Read code the way the compiler does

This gives Claude Visual Studio's resolved model of your C# (Roslyn): find-all-references, go-to-definition, find-implementations, and call and type hierarchies, resolved through interfaces, overrides, and overloads. It also reads the decompiled body of a method inside a referenced DLL, which searching your repo cannot do.

![Roslyn find-all-references resolving a call through an interface that a text search would miss](https://raw.githubusercontent.com/firish/claude_code_vs/main/docs/images/semantic-result.png)

Details are in [the semantic-navigation guide](https://github.com/firish/claude_code_vs/blob/main/docs/SEMANTIC.md).

### A live panel

A dockable Claude Code panel shows connection status, edit decisions, and token usage with an estimated cost. It also holds the two safety toggles (apply edits without the diff, and allow Claude to drive the debugger), both off by default and both reset each session.

![The Claude Code panel showing the connection pill, the debugger-drive toggle, and token and cost figures](https://raw.githubusercontent.com/firish/claude_code_vs/main/docs/images/full-panel.png)

## Requirements

- **Visual Studio 2026.**
- **The Claude Code CLI**, installed and authenticated. See the [Claude Code docs](https://docs.claude.com/claude-code). This extension makes no model calls and does no agent work itself, so it needs the CLI.
- Tested against `claude` 2.1.191.

## Install

- **From here:** click **Download** or **Install**, or in Visual Studio open **Extensions > Manage Extensions** and search for *Claude Code for Visual Studio*.
- **Sideload:** download the `.vsix` from [GitHub Releases](https://github.com/firish/claude_code_vs/releases) and double-click it.

## Quickstart

1. Open your project or solution in Visual Studio 2026.
2. Open the **Claude Code** panel (**View > Other Windows > Claude Code**) and click **Launch Claude Code**. It is also on the **Tools** menu.
3. A terminal opens running `claude`, already connected to the IDE, so you do not need `/ide`. The panel pill turns green and reads **Connected**.
4. Ask Claude to make a change. Its edit opens as a diff, and you click **Accept**, **Reject**, or **Reject with feedback**.

Diagnostics need a loaded project, not a loose file in Open-Folder mode, for the compiler to analyze it.

To let Claude debug: set a breakpoint, tick **Allow Claude to drive debugger** in the panel, start debugging, then ask it to investigate. Reading runtime state works without the toggle. The toggle only gates driving execution.

## How it works

This is a protocol bridge, not a re-implementation of the agent. On Launch it starts a localhost WebSocket server, launches `claude` already connected to the IDE, and implements the IDE tools the CLI drives (the diff, diagnostics, and selection updates). A small PreToolUse hook routes proposed edits through the Visual Studio diff, so approving there is the only gate. Debugger access, the test runner, and semantic navigation are exposed as extra MCP servers (`vs-debug` carries the debugger and the test tools, since they compose; `vs-semantic` carries Roslyn) the CLI reaches through a tiny stdio shim. The CLI does all agent work, and the extension never makes model calls.

## Privacy and security

- The bridge binds to **127.0.0.1 only** and validates an auth token from a lockfile on every connection. The token is never logged.
- The extension makes no network calls and no LLM calls of its own. All AI work is the `claude` CLI, under your own authentication.
- On Launch, it writes a few helper scripts into your workspace's `.claude/` folder and merges hook entries into `.claude/settings.json`, preserving existing content: the edit-gate hook, a token-usage reporter, a break-state hook, and a stdio shim for the `vs-debug` and `vs-semantic` MCP servers (registered in your workspace `.mcp.json`).
- Token cost is an estimate from hardcoded per-tier prices, shown only when you click *Show est. cost*.

## Limitations and known issues

- Visual Studio 2026 only for now. A VS 2022 backfill is planned if there is demand.
- The integration protocol is undocumented and version-fragile, so a `claude` update could change it. Known-good: 2.1.191.
- Diagnostics need a loaded project. The Error List and Roslyn will not analyze loose files.
- Semantic navigation is C#/VB and needs a loaded project. It reads the Roslyn workspace, so it sees the solution open in Visual Studio, and it does not cover C++ navigation.
- Debugger features target managed (.NET) code. Reading runtime state is always on, and driving execution is opt-in. Native and C++ runtime inspection is not covered.
- Test integration is for managed (.NET) test projects and needs a loaded solution. Coverage works, profiling is deferred, and the debug and flaky-catch tools are opt-in behind the debugger-drive toggle.
- Token stats refresh on edits, so a chat-only turn may not update them right away.
- Cost figures are estimates, not billing.

## Troubleshooting

- **Panel says "Waiting for CLI":** click **Launch Claude Code**, or run `/ide` in a `claude` terminal and pick *Visual Studio*.
- **The debugger, test, or semantic tools are missing:** the panel warns when the `vs-debug` and `vs-semantic` servers did not load for a session. Relaunch Claude from the panel and approve the project MCP servers if the CLI prompts.
- **New files land in the wrong folder:** launch from the extension, which pins the working directory to your workspace, or run `claude` from inside the repo.
- **getDiagnostics returns nothing:** open the code as a project and confirm the error appears in the Error List.
- **Filing a bug:** include the **Output > Claude Code** pane contents and your `claude --version`.

---

Source, full documentation, and issue tracker: [github.com/firish/claude_code_vs](https://github.com/firish/claude_code_vs).

[MIT](https://github.com/firish/claude_code_vs/blob/main/LICENSE) © 2026 Rishi Gulati. Not affiliated with Anthropic. "Claude" and "Claude Code" are trademarks of Anthropic, used here only to describe interoperability.
