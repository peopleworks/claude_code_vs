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

## Rung 1b — ARM (next, once 1a is read)

Using the observed `DataElementLocation`+`Size`+`SourceId`, call `DkmPendingDataBreakpoint.Create`
(or `DkmRuntimeClrDataBreakpoint.Create`) from the right component tier at the first user break, and
confirm an *unprompted* write trips the break. Only after that goes green do we build the
extension↔component `DkmCustomMessage` channel and the model-facing `vs_set_data_breakpoint` tool.

## Uninstall

Extensions → Manage Extensions → find "ClaudeCodeVS Concord Spike (throwaway)" → Uninstall;
or `VSIXInstaller.exe /uninstall:ConcordSpike.045197E3-5871-4280-9CE5-4D6B33D8E5B7`.
