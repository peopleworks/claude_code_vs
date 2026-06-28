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

## If it passes — Rung 1 (the *real* unknown)

Loading a component ≠ the feature working. Rung 1: retarget this to a **Monitor-level**
component (`ComponentLevel < 100000`, runs in `msvsmon.exe`), implement
`IDkmModuleInstanceLoadNotification` filtered to the CLR runtime, and from that callback call
`DkmRuntimeClrDataBreakpoint.Create(...).Enable()` on a field of a live .NET debuggee. If a write
to that field doesn't trip a break, the feature is dead regardless of how clean the plumbing is —
so prove **that** before building any extension↔component `DkmCustomMessage` channel.

## Uninstall

Extensions → Manage Extensions → find "ClaudeCodeVS Concord Spike (throwaway)" → Uninstall;
or `VSIXInstaller.exe /uninstall:ConcordSpike.045197E3-5871-4280-9CE5-4D6B33D8E5B7`.
