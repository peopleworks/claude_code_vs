# Getting started

The practical setup: install the extension, launch Claude connected to Visual Studio, and learn the panel and the two safety toggles. For what the tools actually do once you are connected, see [`DEBUGGER.md`](DEBUGGER.md), [`TESTING.md`](TESTING.md), and [`SEMANTIC.md`](SEMANTIC.md).

**Jump to:** [Requirements](#requirements) · [Install](#install) · [First launch](#first-launch) · [The panel](#the-panel) · [The diff gate](#the-diff-gate) · [Auto-accept](#run-wild-auto-accept) · [Notifications](#notifications) · [Let Claude debug](#letting-claude-debug) · [Troubleshooting](#troubleshooting)

---

## Requirements

- **Visual Studio 2026.**
- **The Claude Code CLI**, installed and authenticated. The extension makes no model calls and does no agent work itself, so it needs the CLI. See the [Claude Code docs](https://docs.claude.com/claude-code).
- Tested against `claude` 2.1.191.

---

## Install

- **Marketplace:** search for *Claude Code for Visual Studio* in **Extensions > Manage Extensions**, or install from the [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=firish.bridgev1).
- **Sideload:** download the `.vsix` from [Releases](https://github.com/firish/claude_code_vs/releases) and double-click it.

---

## First launch

1. Open your project or solution in Visual Studio 2026. Diagnostics and the semantic tools need a loaded project, not a loose file in Open-Folder mode.
2. Open the **Claude Code** panel from **View > Other Windows > Claude Code**. It is also on the **Tools** menu.
3. Click **Launch Claude Code**. A terminal opens running `claude`, already connected to the IDE, so you do not need `/ide`. The panel pill turns green and reads **Connected**.
4. Ask Claude to make a change. Its edit opens as a diff.

On Launch, the extension writes a few helper scripts into your workspace's `.claude/` folder and registers the `vs-debug` and `vs-semantic` MCP servers in your `.mcp.json`, preserving anything already there. The full list is in the README's [Privacy and security](../README.md#privacy-and-security) section.

---

## The panel

The dockable Claude Code panel is your status and control surface.

![The Claude Code panel showing the Connected pill, the two toggles, edit and debugger stats, token and cost figures, and the activity log](images/full-panel.png)

It shows:

- **Connection.** The green **Connected** pill, the bridge port, and the workspace folder the CLI is bound to.
- **The two safety toggles.** **Auto-accept (run wild)** and **Allow Claude to drive debugger**, both off by default and both reset each session. More on each below.
- **The Notify toggle.** Mutes the in-IDE notifications ("Claude finished responding" / "Claude needs your input"). On by default, since it is a convenience rather than a safety gate. More below.
- **Edit decisions.** How many edits you accepted and rejected this session.
- **Debugger activity.** How many reads Claude made and how many drive commands it issued.
- **Token usage and estimated cost.** The latest call and the running session, in and out tokens plus cached, with a **Show est. cost** toggle. Cost is an estimate from hardcoded per-tier prices, not billing.
- **An activity log** of what crossed the bridge: permission decisions, diagnostics reads, tool calls, and connection events. When something looks off, this is the first place to check, and the **Output** button opens the full pane.

---

## The diff gate

Every edit Claude proposes opens in Visual Studio's own diff viewer, and approving there is the only step. There is no second yes/no prompt in the terminal.

![A Claude edit in the Visual Studio diff viewer, with Accept, Reject, and Reject with feedback on the InfoBar and the feedback box open](images/diff-reject.png)

You have three choices on the InfoBar:

- **Accept** applies the change and writes the file.
- **Reject** discards it.
- **Reject with feedback** opens a box where you tell Claude what to change. Your note goes back to the CLI as the reason, and Claude reconsiders with it.

Here the reviewer rejected with "Actually, can you change the range to first 7?", and Claude took the note and re-proposed with the new range.

![Claude receiving the rejection note and re-proposing the edit with the requested change](images/diff-reject-relay.png)

That round-trip is the point. You steer the edit without retyping the whole request.

---

## Run wild: auto-accept

The **Auto-accept (run wild)** toggle applies edits without opening the diff, for when you want to let it move fast. It is off by default and resets each session, so it is never left on silently. Turn it off to go back to reviewing each edit.

---

## Notifications

For working in another window while Claude cooks. When a turn finishes, a notification bar appears across the top of the Visual Studio main window ("Claude finished responding.", auto-dismissing after a few seconds). When Claude needs you — a permission prompt in the terminal, or it went idle waiting for input — the bar shows the CLI's message and stays until you dismiss it. If Visual Studio is not the foreground app, its taskbar button also flashes a few times, the standard Windows "needs attention" signal.

Only one notification shows at a time (a new one replaces the old), and the **Notify** toggle in the panel mutes all of it.

---

## Letting Claude debug

Reading runtime state (the call stack, variable values, threads) is always allowed and needs no toggle. Driving the debugger (step, set breakpoints, break at a throw, attach) is opt-in behind **Allow Claude to drive debugger**, which is off by default and resets each session.

To let Claude debug:

1. Set a breakpoint and start debugging with F5, or attach to a running process.
2. Tick **Allow Claude to drive debugger** in the panel.
3. Ask Claude to investigate. While you are paused it already sees the break state, and with the toggle on it can also drive to the fault.

The same toggle gates the test tools that launch the debugger, `vs_debug_test` and `vs_catch_flaky`. Full details are in [`DEBUGGER.md`](DEBUGGER.md) and [`TESTING.md`](TESTING.md).

---

## Troubleshooting

- **Panel says "Waiting for CLI".** Click **Launch Claude Code**, or run `/ide` in a `claude` terminal and pick *Visual Studio*.
- **The debugger, test, or semantic tools are missing.** The panel warns when the `vs-debug` and `vs-semantic` servers did not load for a session. That happens when Claude was launched outside the workspace folder, or the project MCP servers were not approved. Relaunch from the panel, which pins the working directory, and approve the project MCP servers if the CLI prompts.
- **New files land in the wrong folder.** Launch from the extension, which pins the working directory to your workspace, or run `claude` from inside the repo.
- **`getDiagnostics` returns nothing.** Open the code as a project and confirm the error appears in the Error List. Loose files in Open-Folder mode have no compiler analysis.
- **Filing a bug.** Include the **Output > Claude Code** pane contents and your `claude --version`.
