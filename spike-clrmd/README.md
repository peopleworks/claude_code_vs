# ClrMD Step-0 probe (spike)

Proving that **ClrMD** can read structured CLR state (monitor / lock ownership) out of a
live .NET process from inside the extension's exact runtime (**net48 + x64**), via a process
**snapshot** that does *not* collide with a Visual Studio debug session on the same process.

This is the **analysis half** of the post-1.4.0 concurrency plan: a strict upgrade over the
1.4.0 `lockOwnerThreadId` annotation-parse, and the foundation for async-stack reconstruction.

## What's here
- `ClrMdProbe.csproj` / `Program.cs` — net48, x64 console. `ClrMdProbe <pid>` snapshots the
  target and prints CLR threads + sync-block (monitor) ownership.
- `deadlock-fixture/` — a tiny net8 AB-BA deadlock (`Deadlock.exe`) to point the probe at.

## Step-0 results — verified 2026-06-25, *no VS attached*
net48/x64 probe vs a deadlocked net8 process:
- net48 host loaded **ClrMD 4.0.732401** cleanly (binding redirects OK — no `FileLoadException`).
- x64 host snapshotted an x64 **.NET 8** target across runtime versions.
- `CreateSnapshotAndAttach` → `Heap.EnumerateSyncBlocks()` returned the **2 held monitors**,
  each resolved to its **holder OS thread id** + waiter count — the full AB-BA cycle,
  *structured* (not parsed from annotation text):

      obj=0x..a7e8  heldBy[os=42532]  waiters=1
      obj=0x..a800  heldBy[os=23648]  waiters=1

Three of the four unknowns are **GREEN**: net48 load, x64 bitness, and the Gap-A read.

## The make-or-break test — needs VS, run this
The one thing the self-test can't cover: does the snapshot still work while **VS owns the
debug port** on the same process? Theory says yes — `CreateSnapshotAndAttach` forks via
`PssCaptureSnapshot`, which needs memory-read rights, not the debug port — but it must be
verified against the live Concord engine.

1. Build both:
   - `dotnet build deadlock-fixture/Deadlock.csproj -c Debug`
   - `dotnet build ClrMdProbe.csproj -c Debug`
2. Start `deadlock-fixture/bin/Debug/net8.0/Deadlock.exe`, then in Visual Studio
   **Debug → Attach to Process → Deadlock.exe** (or just F5 the repo's `demo/LockJam`).
   Let it hang, then **Break All**.
3. With VS still attached and at the break, run the probe against the same pid:
   - `bin\Debug\net48\ClrMdProbe.exe <pid>`     (pid: VS title bar, or `Get-Process Deadlock | Select Id`)
4. **Pass** = probe prints the held monitors (same as Step-0), no error.
   **Fail** = a snapshot/attach error ⇒ Concord holds a lock `PssCaptureSnapshot` needs;
   fallback would be to gate the probe to "VS not attached" or read a dump instead.

## Constraints (proven / noted)
- **x64 only** in-proc: the probe must match target bitness; an x86 target needs an
  out-of-process x86 helper (future work).
- net48 needs binding redirects for ClrMD's deps (already wired in `ClrMdProbe.csproj`).
- Always dispose the `DataTarget` (the `using` blocks) — ClrMD otherwise leaves the process
  suspended / temp snapshot files behind.
