using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Runtime;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Debugging;

/// <summary>
/// Structured CLR inspection via a process SNAPSHOT (ClrMD CreateSnapshotAndAttach), NOT EnvDTE.
/// The snapshot is a PssCaptureSnapshot fork, so we read a clone and coexist with VS owning the debug
/// port (verified - see spike-clrmd/). MUST run OFF the UI thread (snapshotting is slow); callers read
/// the debuggee PID from EnvDTE on the UI thread, then hand it here on a background thread.
/// </summary>
internal static class ClrMdReader
{
    private const int MaxItems = 200;

    /// <summary>
    /// Gap A - structured monitor/lock ownership + deadlock suspects for a debuggee PID. Returns
    /// {clr, heldLocks:[{object,holder,recursion,waiters}], threads:[{osId,managedId,topFrame,blockedOn,holds}],
    /// deadlockSuspects:[...], note} or {error}.
    /// </summary>
    public static JObject ReadWaitChains(int pid)
    {
        try
        {
            using DataTarget dt = DataTarget.CreateSnapshotAndAttach(pid);
            ClrInfo? clrInfo = dt.ClrVersions.FirstOrDefault();
            if (clrInfo == null)
                return new JObject { ["error"] = "no CLR found in target (managed process? host/target bitness must match)" };

            using ClrRuntime runtime = clrInfo.CreateRuntime();
            var result = new JObject { ["clr"] = $"{clrInfo.Flavor} {clrInfo.Version}" };

            ClrThread[] threads = runtime.Threads.Where(t => t.IsAlive).ToArray();
            var byAddr = new Dictionary<ulong, ClrThread>();
            foreach (ClrThread t in threads) byAddr[t.Address] = t;

            // Held monitors: object -> holder + waiter count. Also index holder-osId -> the objects it holds.
            var heldLocks = new JArray();
            var holdsByOs = new Dictionary<uint, List<string>>();
            int lockCount = 0;
            foreach (SyncBlock sb in runtime.Heap.EnumerateSyncBlocks())
            {
                if (!sb.IsMonitorHeld) continue;
                if (lockCount++ >= MaxItems) { heldLocks.Add(Trunc($"capped at {MaxItems} locks")); break; }
                byAddr.TryGetValue(sb.HoldingThreadAddress, out ClrThread? holder);
                uint hOs = holder?.OSThreadId ?? 0;
                string objHex = $"0x{sb.Object:x}";
                heldLocks.Add(new JObject
                {
                    ["object"] = objHex,
                    ["holder"] = new JObject { ["osId"] = hOs, ["managedId"] = holder?.ManagedThreadId ?? 0 },
                    ["recursion"] = sb.RecursionCount,
                    ["waiters"] = sb.WaitingThreadCount,
                });
                if (hOs != 0)
                {
                    if (!holdsByOs.TryGetValue(hOs, out List<string>? list)) holdsByOs[hOs] = list = new List<string>();
                    list.Add(objHex);
                }
            }

            // Threads: osId, managedId, top frame, blocked category, and the locks each holds.
            var threadArr = new JArray();
            var suspects = new JArray();
            int tCount = 0;
            foreach (ClrThread t in threads)
            {
                if (tCount++ >= MaxItems) { threadArr.Add(Trunc($"capped at {MaxItems} threads")); break; }
                ClrStackFrame? top = t.EnumerateStackTrace().FirstOrDefault();
                string topStr = top?.Method?.ToString() ?? top?.ToString() ?? "(no managed frame)";
                string? blocked = BlockedCategory(topStr);
                holdsByOs.TryGetValue(t.OSThreadId, out List<string>? holds);
                var holdsArr = holds != null ? new JArray(holds.ToArray()) : new JArray();
                threadArr.Add(new JObject
                {
                    ["osId"] = t.OSThreadId,
                    ["managedId"] = t.ManagedThreadId,
                    ["topFrame"] = topStr,
                    ["blockedOn"] = blocked,   // null = running
                    ["holds"] = holdsArr,
                });
                // A thread that HOLDS a lock AND is blocked entering a monitor is a deadlock-cycle member.
                if (blocked == "monitor" && holdsArr.Count > 0)
                    suspects.Add(new JObject { ["osId"] = t.OSThreadId, ["managedId"] = t.ManagedThreadId, ["holds"] = holdsArr.DeepClone() });
            }

            result["heldLocks"] = heldLocks;
            result["threads"] = threadArr;
            result["deadlockSuspects"] = suspects;
            result["note"] =
                "Held-monitor ownership is exact (holder + waiter count). ClrMD does not decode WHICH object a "
                + "blocked thread is trying to enter, so the explicit wait-for edge isn't here; a thread in "
                + "deadlockSuspects both holds a lock AND is blocked in Monitor.Enter (i.e. a cycle member). For "
                + "the exact 'waiting on lock owned by thread X' edge, cross-reference vs_threads.";
            return result;
        }
        catch (Exception e)
        {
            return new JObject { ["error"] = $"{e.GetType().Name}: {e.Message}" };
        }
    }

    /// <summary>Classify a blocked thread by its top frame, or null if it isn't parked on a sync primitive.</summary>
    private static string? BlockedCategory(string topFrame)
    {
        if (topFrame.IndexOf("Monitor.ReliableEnter", StringComparison.Ordinal) >= 0
            || topFrame.IndexOf("Monitor.Enter", StringComparison.Ordinal) >= 0
            || topFrame.IndexOf("Monitor.TryEnter", StringComparison.Ordinal) >= 0)
            return "monitor";
        if (topFrame.IndexOf("Monitor.Wait", StringComparison.Ordinal) >= 0) return "monitor-wait";
        if (topFrame.IndexOf("WaitHandle.Wait", StringComparison.Ordinal) >= 0
            || topFrame.IndexOf("WaitOneCore", StringComparison.Ordinal) >= 0)
            return "waithandle";
        if (topFrame.IndexOf("Thread.Join", StringComparison.Ordinal) >= 0
            || topFrame.IndexOf("JoinInternal", StringComparison.Ordinal) >= 0)
            return "join";
        return null;
    }

    private static JObject Trunc(string why) => new JObject { ["truncated"] = true, ["note"] = why };
}
