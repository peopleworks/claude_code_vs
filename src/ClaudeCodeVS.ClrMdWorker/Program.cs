using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClrMdWorker
{
    // Out-of-process ClrMD worker. Runs in its own process (own assembly binding) so devenv's
    // System.Collections.Immutable / binding policy can't break ClrMD. Writes one JSON object to stdout.
    //   Usage: ClrMdWorker.exe waitchains <pid>
    internal static class Program
    {
        private const int MaxItems = 200;

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length < 2) { Emit(new JObject { ["error"] = "usage: ClrMdWorker <command> <pid>" }); return 2; }
                if (!int.TryParse(args[1], out int pid)) { Emit(new JObject { ["error"] = $"invalid pid '{args[1]}'" }); return 2; }
                JObject result;
                switch (args[0])
                {
                    case "waitchains": result = ReadWaitChains(pid); break;
                    case "asyncstacks": result = ReadAsyncStacks(pid); break;
                    default: result = new JObject { ["error"] = $"unknown command '{args[0]}'" }; break;
                }
                Emit(result);
                return result["error"] != null ? 1 : 0;
            }
            catch (Exception e)
            {
                Emit(new JObject { ["error"] = $"{e.GetType().Name}: {e.Message}" });
                return 1;
            }
        }

        private static void Emit(JObject o) => Console.Out.Write(o.ToString(Formatting.None));

        /// <summary>Gap A - structured monitor/lock ownership + deadlock suspects from a process snapshot.</summary>
        private static JObject ReadWaitChains(int pid)
        {
            using (DataTarget dt = DataTarget.CreateSnapshotAndAttach(pid))
            {
                ClrInfo clrInfo = dt.ClrVersions.FirstOrDefault();
                if (clrInfo == null)
                    return new JObject { ["error"] = "no CLR found in target (managed process? host/target bitness must match)" };

                using (ClrRuntime runtime = clrInfo.CreateRuntime())
                {
                    var result = new JObject { ["clr"] = $"{clrInfo.Flavor} {clrInfo.Version}" };

                    ClrThread[] threads = runtime.Threads.Where(t => t.IsAlive).ToArray();
                    var byAddr = new Dictionary<ulong, ClrThread>();
                    foreach (ClrThread t in threads) byAddr[t.Address] = t;

                    // Held monitors: object -> holder + waiter count; index holder-osId -> objects it holds.
                    var heldLocks = new JArray();
                    var holdsByOs = new Dictionary<uint, List<string>>();
                    int lockCount = 0;
                    foreach (SyncBlock sb in runtime.Heap.EnumerateSyncBlocks())
                    {
                        if (!sb.IsMonitorHeld) continue;
                        if (lockCount++ >= MaxItems) { heldLocks.Add(Trunc($"capped at {MaxItems} locks")); break; }
                        byAddr.TryGetValue(sb.HoldingThreadAddress, out ClrThread holder);
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
                            if (!holdsByOs.TryGetValue(hOs, out List<string> list)) holdsByOs[hOs] = list = new List<string>();
                            list.Add(objHex);
                        }
                    }

                    // Threads: osId, managedId, top frame, blocked category, the locks each holds.
                    var threadArr = new JArray();
                    var suspects = new JArray();
                    int tCount = 0;
                    foreach (ClrThread t in threads)
                    {
                        if (tCount++ >= MaxItems) { threadArr.Add(Trunc($"capped at {MaxItems} threads")); break; }
                        ClrStackFrame top = t.EnumerateStackTrace().FirstOrDefault();
                        string topStr = top?.Method?.ToString() ?? top?.ToString() ?? "(no managed frame)";
                        string blocked = BlockedCategory(topStr);
                        holdsByOs.TryGetValue(t.OSThreadId, out List<string> holds);
                        var holdsArr = holds != null ? new JArray(holds.ToArray()) : new JArray();
                        threadArr.Add(new JObject
                        {
                            ["osId"] = t.OSThreadId,
                            ["managedId"] = t.ManagedThreadId,
                            ["topFrame"] = topStr,
                            ["blockedOn"] = blocked,   // null = running
                            ["holds"] = holdsArr,
                        });
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
            }
        }

        private static string BlockedCategory(string topFrame)
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

        // ===== Gap B: logical async-stack reconstruction (DumpAsync-style) =====
        // Each async state-machine box IS-A Task<T> on modern .NET. We walk the boxes on the heap and
        // link each to its continuation (the async method that resumes when it completes) to rebuild the
        // logical chain the physical MoveNext/ThreadPool stack hides. Field names verified for .NET 8.

        private sealed class AsyncBox
        {
            public ulong Address;
            public string Method = "<unknown>";
            public int State;            // -1 running, >=0 = await-point index (-2 completed is filtered out)
            public ulong ContinuationAddr;
            public bool TopLevel = true; // nothing continues to me => I'm the innermost frame of a chain
        }

        private const string BoxPrefix = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder<";

        private static JObject ReadAsyncStacks(int pid)
        {
            using (DataTarget dt = DataTarget.CreateSnapshotAndAttach(pid))
            {
                ClrInfo clrInfo = dt.ClrVersions.FirstOrDefault();
                if (clrInfo == null)
                    return new JObject { ["error"] = "no CLR found in target (managed process? host/target bitness must match)" };

                using (ClrRuntime runtime = clrInfo.CreateRuntime())
                {
                    ClrType taskType = runtime.BaseClassLibrary?.GetTypeByName("System.Threading.Tasks.Task");
                    if (taskType == null) return new JObject { ["error"] = "could not resolve System.Threading.Tasks.Task" };

                    var boxes = new Dictionary<ulong, AsyncBox>();
                    int scanned = 0;
                    foreach (ClrObject obj in runtime.Heap.EnumerateObjects())
                    {
                        if (!obj.IsValid || obj.Type == null || obj.Size <= 24) continue;
                        if (!IsTask(obj.Type, taskType) || !IsBox(obj.Type)) continue;
                        if (boxes.ContainsKey(obj.Address)) continue;
                        if (scanned++ > 200000) break; // hard scan ceiling on giant heaps

                        string method = "<unknown>"; int state = -1;
                        if (obj.Type.GetFieldByName("StateMachine") is ClrInstanceField smF)
                        {
                            // .NET 8: StateMachine is a value-type field (the struct lives inline in the box).
                            if (smF.IsValueType)
                            {
                                ClrValueType sm = obj.ReadValueTypeField("StateMachine");
                                if (sm.IsValid && sm.Type != null)
                                {
                                    method = PrettyMethod(sm.Type.Name);
                                    if (sm.Type.GetFieldByName("<>1__state") != null) state = sm.ReadField<int>("<>1__state");
                                }
                            }
                            else // legacy ref-type state machine (older Framework)
                            {
                                ClrObject sm = smF.ReadObject(obj.Address, false);
                                if (sm.IsValid && sm.Type != null)
                                {
                                    method = PrettyMethod(sm.Type.Name);
                                    if (sm.Type.GetFieldByName("<>1__state") != null) state = sm.ReadField<int>("<>1__state");
                                }
                            }
                        }
                        if (state == -2) continue; // completed

                        ulong contAddr = 0;
                        if (obj.Type.GetFieldByName("m_continuationObject") is ClrInstanceField cf)
                        {
                            ClrObject raw = cf.ReadObject(obj.Address, false);
                            if (raw.IsValid) { ClrObject r = Resolve(raw, taskType); if (!r.IsNull) contAddr = r.Address; }
                        }
                        boxes[obj.Address] = new AsyncBox { Address = obj.Address, Method = method, State = state, ContinuationAddr = contAddr };
                    }

                    // Something continues to it => it's not the innermost frame of its chain.
                    foreach (AsyncBox b in boxes.Values)
                        if (b.ContinuationAddr != 0 && boxes.TryGetValue(b.ContinuationAddr, out AsyncBox parent))
                            parent.TopLevel = false;

                    // Each innermost root: walk continuations inner->outer (already stack order; no reverse).
                    var stacks = new JArray();
                    int emitted = 0;
                    foreach (AsyncBox root in boxes.Values.Where(b => b.TopLevel))
                    {
                        var frames = new List<AsyncBox>();
                        var seen = new HashSet<ulong>();
                        AsyncBox cur = root;
                        while (cur != null && seen.Add(cur.Address))
                        {
                            frames.Add(cur);
                            cur = (cur.ContinuationAddr != 0 && boxes.TryGetValue(cur.ContinuationAddr, out AsyncBox nxt)) ? nxt : null;
                        }
                        // Filter framework noise: keep only chains that touch user code.
                        if (!frames.Any(f => !f.Method.StartsWith("System.", StringComparison.Ordinal) && !f.Method.StartsWith("Microsoft.", StringComparison.Ordinal)))
                            continue;
                        var arr = new JArray();
                        foreach (AsyncBox f in frames)
                            arr.Add(new JObject { ["method"] = f.Method, ["state"] = f.State, ["address"] = $"0x{f.Address:x}" });
                        stacks.Add(new JObject { ["frames"] = arr });
                        if (++emitted >= 200) { stacks.Add(Trunc("capped at 200 async stacks")); break; }
                    }

                    return new JObject
                    {
                        ["clr"] = $"{clrInfo.Flavor} {clrInfo.Version}",
                        ["asyncStacks"] = stacks,
                        ["count"] = emitted,
                        ["note"] = "Logical async call stacks rebuilt from heap state-machine boxes, innermost frame first. "
                            + "state: -1 = running, >=0 = await-point index. Only in-flight async methods that touch user code "
                            + "are shown (pure System/Microsoft framework chains are filtered). What each frame awaits isn't "
                            + "decoded; WhenAll fan-out is linearized to the first continuation.",
                    };
                }
            }
        }

        private static bool IsTask(ClrType t, ClrType taskType)
        {
            for (; t != null; t = t.BaseType)
                if (t.MetadataToken == taskType.MetadataToken && t.Module == taskType.Module) return true;
            return false;
        }

        private static bool IsBox(ClrType t)
            => t?.Name is string n && n.StartsWith(BoxPrefix, StringComparison.Ordinal)
               && n.IndexOf("AsyncStateMachineBox", BoxPrefix.Length, StringComparison.Ordinal) >= 0;

        private static bool TryObjField(ClrObject o, string name, out ClrObject result)
        {
            result = default;
            if (o.Type?.GetFieldByName(name) is ClrInstanceField f)
            {
                ClrObject v = f.ReadObject(o.Address, false);
                if (v.IsValid) { result = v; return true; }
            }
            return false;
        }

        // Resolve a raw m_continuationObject down to the parent box/Task it represents (all cases).
        private static ClrObject Resolve(ClrObject c, ClrType taskType)
        {
            if (c.IsNull || c.Type == null) return c;
            if (IsTask(c.Type, taskType) && IsBox(c.Type)) return c;          // (a) another box directly
            if (TryObjField(c, "m_task", out ClrObject t)) return t;          // (d) standard Task continuation
            if (TryObjField(c, "m_action", out ClrObject a)) c = a;          // (b) action-wrapping node
            if (TryObjField(c, "_target", out ClrObject tgt))                 //     -> delegate target = parent box
            {
                c = tgt;
                if (c.Type?.Name == "System.Runtime.CompilerServices.AsyncMethodBuilderCore+ContinuationWrapper"
                    && TryObjField(c, "_continuation", out ClrObject inner))   // (c) ContinuationWrapper unwrap
                {
                    c = inner;
                    if (TryObjField(c, "_target", out ClrObject t2)) c = t2;
                }
            }
            return c;
        }

        // "Ns.Class+<InnerAsync>d__7" -> "Ns.Class.InnerAsync"
        private static string PrettyMethod(string smTypeName)
        {
            if (string.IsNullOrEmpty(smTypeName)) return "<unknown>";
            int lt = smTypeName.IndexOf('<');
            int gt = smTypeName.IndexOf(">d__", StringComparison.Ordinal);
            if (lt >= 0 && gt > lt)
            {
                string method = smTypeName.Substring(lt + 1, gt - lt - 1);
                string owner = smTypeName.Substring(0, lt).TrimEnd('+', '.');
                return owner.Length > 0 ? owner.Replace('+', '.') + "." + method : method;
            }
            return smTypeName;
        }
    }
}
