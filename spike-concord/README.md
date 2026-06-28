# Concord Spike — Rung 0 (throwaway)

**One question this answers:** *Does VS 2026 load an unsigned, third-party managed Concord
debug-engine component at all?* If yes, the path to a real managed **data breakpoint**
("break when value changes") via `DkmRuntimeClrDataBreakpoint` is open and worth building.
If no, that whole feature is dead for us and we stop here — for the cost of one afternoon.

This is **not** shippable code and is **not** wired into the extension. It's a faithful copy of
Microsoft's `ConcordExtensibilitySamples/HelloWorld/Cs` (MIT), renamed, with a distinctive
sentinel string so we know *our* component loaded (not a cached sample).

## What it is

A Concord `IDkmCallStackFilter` component. The engine calls it once per frame while walking any
call stack; we inject one annotated frame — **`[ClaudeCodeVS Concord Spike]`** — at the very top
and pass every real frame through untouched.

```
spike-concord/
  dll/                      the component (net472 class library)
    HelloWorldService.cs    the IDkmCallStackFilter
    SpikeDataItem.cs        per-stack-walk state (DkmDataItem)
    ConcordSpike.vsdconfigxml   component registration (-> binary .vsdconfig at build)
    ConcordSpike.csproj     Concord pkgs 17.14.1051801 (Debugger.Engine + VSDConfigTool)
  vsix/                     packaging only (no code)
    source.extension.vsixmanifest   declares <Asset Type="DebuggerEngineExtension" ...>
    ConcordSpikeVsix.csproj         VSSDK.BuildTools 18.6 / SDK 17.14 (our VS2026 path)
```

## Build

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild spike-concord\vsix\ConcordSpikeVsix.csproj -restore -t:Rebuild -p:Configuration=Release
# -> spike-concord\vsix\bin\Release\ConcordSpike.vsix   (contains ConcordSpike.dll + ConcordSpike.vsdconfig)
```

Build is verified: the `.vsdconfigxml` compiles to a 207-byte binary `.vsdconfig`, and both it and
the component DLL land at the VSIX root with the `DebuggerEngineExtension` asset pointing at it.

## The make-or-break test (do this in VS 2026)

1. **Install** `spike-concord\vsix\bin\Release\ConcordSpike.vsix` — double-click it, or:
   ```powershell
   & "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\VSIXInstaller.exe" `
     "<repo>\spike-concord\vsix\bin\Release\ConcordSpike.vsix"
   ```
   It installs **alongside** the main ClaudeCodeVS extension (different Identity Id) and is
   independently removable. **Restart VS** after installing — the engine reads `.vsdconfig`s at
   startup.
2. **Debug any managed app.** A throwaway .NET console app is plenty: F5, and **break** anywhere
   (a breakpoint on any line, or Debug → Break All).
3. **Open the Call Stack window** (Debug → Windows → Call Stack, or `Ctrl+Alt+C`).
4. **Look at the top frame.**

| Outcome | Verdict |
|---|---|
| `[ClaudeCodeVS Concord Spike]` sits at the top of the call stack | ✅ **PASS** — VS 2026 loads our unsigned third-party Concord component. Door is open; build Rung 1. |
| No such frame, normal stack only | ❌ **FAIL** — the component didn't load. Check the install registered the `.vsdconfig`; if it loads but the frame's absent, the IDE-component path is blocked for third parties. |

## Rung 1a — OBSERVE (current step)

Rung 0 PASSED (the frame appeared). Loading a component ≠ the feature working, and research turned
up two blockers to arming a data breakpoint blind: (1) **static fields are unsupported** — only an
*instance field of a heap object* works; (2) `DkmRuntimeClrDataBreakpoint.Create` has **no field
parameter** and **no public sample exists**, so the field→breakpoint binding is opaque.

So before arming anything, we OBSERVE the engine doing it. The component now also implements
`IDkmBoundBreakpointHitNotification` + `IDkmDataBreakpointHitNotification` and logs every readable
property of a hit breakpoint — crucially `DkmPendingDataBreakpoint.DataElementLocation` + `.Size`
(the engine's own field binding) — to **`%TEMP%\ConcordSpike-observe.log`**.

### The observation test

1. **Rebuild + reinstall** the 0.2.0 VSIX (installs over 0.1.0, same Id). **Restart VS.**
2. **Delete** any old `%TEMP%\ConcordSpike-observe.log` so we read fresh data.
3. **Pre-req (one-time):** Tools → Options → Debugging → General → **uncheck** "Use the legacy C#
   and VB expression evaluators" (managed data BPs require the modern EE).
4. **Debug the fixture:** open `spike-concord\fixture\DataBpTarget.csproj` in VS 2026 (it's
   **.NET 8 / x64** — required; data BPs don't work on .NET Framework) and **F5**. It stops at
   `Debugger.Break()`.
5. **Set a data breakpoint the supported way:** in **Locals**, expand the `target` object,
   right-click its **`Value`** field → **Break When Value Changes**.
6. **Continue (F5).** On the first `target.Value = 100` write, VS should break (its own working
   data BP). Optionally continue a few times.
7. **Send me `%TEMP%\ConcordSpike-observe.log`.**

### What the log tells us

- **Did `==== DATA BREAKPOINT HIT ====` appear at all?** If yes, IDE-level components receive
  data-BP hits → a single IDE component can likely drive Rung 1b. If the log shows only normal
  breaks (or nothing) even though VS broke, data-BP hits aren't delivered to IDE level → Rung 1b
  needs a **monitor-level** (`<100000`) component in `msvsmon`.
- **`Pending.DataElementLocation` + `Pending.Size`** — the exact string/size the engine binds, i.e.
  what we feed `DkmPendingDataBreakpoint.Create(...)` in Rung 1b.
- **`bound.Target.Type` / `bound.Pending.Type`** — the concrete breakpoint classes, confirming
  whether `DkmRuntimeClrDataBreakpoint` is what actually backs a managed data BP.

## Rung 1b — ARM (current step)

Rung 1a observation cracked the mechanism. The managed data BP is a **`DkmRuntimeCustomDataBreakpoint`**
owned by Microsoft's own runtime monitor (`MSCustomDataBreakpointManagerId`), and the opaque field
token is minted by **`DkmSuccessEvaluationResult.GetDataBreakpointInfo()`**. So we need **no monitor
component** — one IDE-level component drives the whole public-API chain:

```
OnBoundBreakpointHit(thread)                                  // first normal breakpoint
  -> thread.GetTopStackFrame()
  -> DkmLanguageExpression.Create(C#, "target.Value")
  -> DkmInspectionContext.EvaluateExpression(...)             // async work list
  -> (DkmSuccessEvaluationResult).GetDataBreakpointInfo(out err)   // -> { Identifier, Size }
  -> DkmPendingDataBreakpoint.Create(proc, sourceId, c#Id, thread, false, Identifier, Size, null)
  -> .Enable(...)                                             // MS runtime monitor arms it
```

`SpikeService` now does exactly this on the first normal breakpoint, logging every step.

### The make-or-break test (does our programmatic arm actually break?)

1. **Close any running `DataBpTarget`** from a prior run (it holds the exe lock), then **reinstall**
   the 0.3.0 VSIX and **restart VS**.
2. **Delete** `%TEMP%\ConcordSpike-observe.log`.
3. Open `spike-concord\fixture\DataBpTarget.csproj` in VS 2026 (**.NET 8 / x64**).
4. **Set a normal breakpoint (F9)** on the line marked `>>> BREAKPOINT <<<` (the `Thread.Sleep(50);`
   line) — **do NOT set any data breakpoint yourself.**
5. **F5.** Execution stops at that line → the component evaluates `target.Value` and arms a data BP.
6. **Continue (F5).**

| Outcome | Verdict |
|---|---|
| VS **breaks again on the first `target.Value = 100` write** (with no data BP you set) | ✅ **Rung 1b PASS** — managed data breakpoints are reachable programmatically from an IDE-level component. The feature is real. |
| No break on the write; loop runs to completion | ❌ check the log — the step-by-step trace says exactly where it failed (evaluate / GetDataBreakpointInfo / Create / Enable error code). |

7. **Send me `%TEMP%\ConcordSpike-observe.log`** either way — on success it shows `ARMED OK` then a
   `DATA BREAKPOINT HIT`; on failure it pinpoints the failing call.

## VERDICT (banked 2026-06-28)

**PROVEN:** a managed data breakpoint ("break when value changes"), armed entirely from code in our
IDE-level Concord component, **fires** — demonstrated across runs 0.4.0–0.6.0 (our minted token shows
up as the `DkmRuntimeCustomDataBreakpoint` that breaks on the write). All the hard unknowns are
retired:
- VS 2026 loads an unsigned third-party Concord component (Rung 0).
- The mechanism: managed data BP = `DkmRuntimeCustomDataBreakpoint` owned by MS's own
  `MSCustomDataBreakpointManagerId` monitor; field binding minted by
  `DkmSuccessEvaluationResult.GetDataBreakpointInfo()` on the **child** result of the owning object
  (bare expressions fail — must drill owner → children → field).
- The full public-API arm chain (evaluate → children → GetDataBreakpointInfo → Create → Enable).
- `Enable` must be async (`BeginExecution`, not `Execute`) or it self-deadlocks ~67s →
  `E_XAPI_COMPLETION_ROUTINE_RELEASED`.

**KNOWN-REMAINING (the one wall left, deferred to Rung 2):** arming from inside
`IDkmBoundBreakpointHitNotification` (the **event thread**, while stopped) leaves the async `Enable`
completion undelivered — it never fires — and the dangling op crashes the debugger on continue. GC
rooting did NOT fix it (ruled lifetime out). The fix is architectural: **arm from the request/IDE
thread, not from a stop-event notification** — matching how real engines enroll/bind breakpoints
(AD7 lifecycle: enroll once on the request side, rebind on module load, decoupled from break/run).

## Rung 2 — productize (the real implementation)

A model-facing `vs_set_data_breakpoint` tool on the `vs-debug` MCP server. The open design question
the spike surfaced: **how the tool call (request/IDE side) triggers the arm inside the Concord
component on the right thread** — likely the component exposes arming via a request-thread entry
(e.g. a `DkmCustomMessage` from the extension, or arming wired into the resume/continue flow) rather
than the event-thread hit notification this spike used. Then surface the resulting break through the
existing await-break engine. Ship the Concord component (vsdconfig) inside the extension VSIX.

## Uninstall

Extensions → Manage Extensions → find "ClaudeCodeVS Concord Spike (throwaway)" → Uninstall;
or `VSIXInstaller.exe /uninstall:ConcordSpike.045197E3-5871-4280-9CE5-4D6B33D8E5B7`.
