using System;
using System.Collections.Generic;
using System.IO;
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
                    case "heapstats": result = ReadHeapStats(pid); break;
                    case "threadpool": result = ReadThreadPool(pid); break;
                    case "roots": result = ReadRoots(pid, args.Length > 2 ? args[2] : ""); break;
                    case "heapdiff": result = ReadHeapDiff(pid, args.Length > 2 ? args[2] : ""); break;
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

        // ===== Memory / GC diagnostics =====

        private sealed class TypeStat { public ClrType Type; public long Count; public ulong Bytes; }

        private static string GenName(Generation g)
        {
            switch (g)
            {
                case Generation.Generation0: return "gen0";
                case Generation.Generation1: return "gen1";
                case Generation.Generation2: return "gen2";
                case Generation.Large: return "loh";
                case Generation.Pinned: return "poh";
                case Generation.Frozen: return "frozen";
                default: return "unknown";
            }
        }

        // heap_stats: top types by bytes + per-generation sizes + GC mode + finalizer queue + handle kinds.
        private static JObject ReadHeapStats(int pid)
        {
            using (DataTarget dt = DataTarget.CreateSnapshotAndAttach(pid))
            {
                ClrInfo clrInfo = dt.ClrVersions.FirstOrDefault();
                if (clrInfo == null)
                    return new JObject { ["error"] = "no CLR found in target (managed process? host/target bitness must match)" };

                using (ClrRuntime runtime = clrInfo.CreateRuntime())
                {
                    ClrHeap heap = runtime.Heap;

                    // single heap walk: type stats + generation bytes
                    var counts = new Dictionary<ulong, TypeStat>();
                    var genBytes = new Dictionary<string, ulong>();
                    long totalObjs = 0; ulong totalBytes = 0; long scanned = 0; bool truncated = false;
                    foreach (ClrSegment seg in heap.Segments)
                    {
                        foreach (ClrObject obj in seg.EnumerateObjects())
                        {
                            if (!obj.IsValid || obj.Type == null) continue;
                            if (scanned++ > 5_000_000) { truncated = true; break; }
                            ulong mt = obj.Type.MethodTable;
                            if (!counts.TryGetValue(mt, out TypeStat ts)) counts[mt] = ts = new TypeStat { Type = obj.Type };
                            ts.Count++; ts.Bytes += obj.Size;
                            totalObjs++; totalBytes += obj.Size;
                            string g = GenName(seg.GetGeneration(obj.Address));
                            genBytes[g] = genBytes.TryGetValue(g, out ulong gb) ? gb + obj.Size : obj.Size;
                        }
                        if (truncated) break;
                    }

                    var topTypes = new JArray();
                    foreach (TypeStat ts in counts.Values.OrderByDescending(t => t.Bytes).Take(40))
                        topTypes.Add(new JObject { ["type"] = ts.Type.Name ?? "<unknown>", ["count"] = ts.Count, ["bytes"] = ts.Bytes });

                    var generations = new JObject();
                    foreach (var kv in genBytes) generations[kv.Key] = kv.Value;

                    var gc = new JObject { ["server"] = heap.IsServer, ["heapCount"] = heap.SubHeaps.Length };
                    ClrSubHeap sh = heap.SubHeaps.FirstOrDefault();
                    if (sh != null) { gc["backgroundGC"] = sh.HasBackgroundGC; gc["regions"] = sh.HasRegions; gc["pinnedObjectHeap"] = sh.HasPinnedObjectHeap; }

                    var handleCounts = new Dictionary<string, long>();
                    long handleScan = 0;
                    foreach (ClrHandle h in runtime.EnumerateHandles())
                    {
                        if (handleScan++ > 2_000_000) break;
                        string k = h.HandleKind.ToString();
                        handleCounts[k] = handleCounts.TryGetValue(k, out long hc) ? hc + 1 : 1;
                    }
                    var handles = new JObject();
                    foreach (var kv in handleCounts.OrderByDescending(k => k.Value)) handles[kv.Key] = kv.Value;

                    long finalizable = 0; var finBy = new Dictionary<ulong, TypeStat>(); long finScan = 0;
                    foreach (ClrObject o in heap.EnumerateFinalizableObjects())
                    {
                        if (finScan++ > 2_000_000) break;
                        finalizable++;
                        if (o.Type == null) continue;
                        ulong mt = o.Type.MethodTable;
                        if (!finBy.TryGetValue(mt, out TypeStat ts)) finBy[mt] = ts = new TypeStat { Type = o.Type };
                        ts.Count++;
                    }
                    var finTop = new JArray();
                    foreach (TypeStat ts in finBy.Values.OrderByDescending(t => t.Count).Take(10))
                        finTop.Add(new JObject { ["type"] = ts.Type.Name ?? "<unknown>", ["count"] = ts.Count });

                    var result = new JObject
                    {
                        ["clr"] = $"{clrInfo.Flavor} {clrInfo.Version}",
                        ["gc"] = gc,
                        ["totalObjects"] = totalObjs,
                        ["totalBytes"] = totalBytes,
                        ["generations"] = generations,
                        ["topTypes"] = topTypes,
                        ["handles"] = handles,
                        ["finalizable"] = new JObject { ["count"] = finalizable, ["topTypes"] = finTop },
                        ["note"] = "Top types by total bytes (live heap walk). generations = bytes per GC generation (loh/poh = large/pinned object heaps). finalizable = objects still registered for finalization (a large/growing single type points at a stuck finalizer or undisposed resources). handles grouped by kind (a Pinned/AsyncPinned explosion = fragmentation; Strong/Dependent growth = a managed leak). Use vs_gc_roots on a suspect type to see what's keeping it alive, vs_heap_diff to see what's growing.",
                    };
                    if (truncated) result["truncated"] = true;
                    return result;
                }
            }
        }

        // threadpool: Portable ThreadPool worker counts + queue backlog + starvation signal (.NET 6+ targets).
        private static JObject ReadThreadPool(int pid)
        {
            using (DataTarget dt = DataTarget.CreateSnapshotAndAttach(pid))
            {
                ClrInfo clrInfo = dt.ClrVersions.FirstOrDefault();
                if (clrInfo == null)
                    return new JObject { ["error"] = "no CLR found in target (managed process? host/target bitness must match)" };

                using (ClrRuntime runtime = clrInfo.CreateRuntime())
                {
                    var result = new JObject { ["clr"] = $"{clrInfo.Flavor} {clrInfo.Version}" };
                    ClrType ptp = runtime.BaseClassLibrary?.GetTypeByName("System.Threading.PortableThreadPool");
                    if (ptp == null) { result["note"] = "target has no PortableThreadPool (not .NET 6+ Core) — threadpool stats unavailable."; return result; }

                    ClrAppDomain dom = runtime.AppDomains.FirstOrDefault();

                    // Locate the PortableThreadPool singleton: static field, else heap scan.
                    ClrObject pool = default;
                    ClrStaticField instField = ptp.GetStaticFieldByName("ThreadPoolInstance");
                    if (instField != null && dom != null) { try { pool = instField.ReadObject(dom); } catch { } }
                    if (!pool.IsValid)
                        foreach (ClrObject o in runtime.Heap.EnumerateObjects()) { if (o.Type == ptp) { pool = o; break; } }
                    if (!pool.IsValid) { result["error"] = "could not locate PortableThreadPool instance"; return result; }

                    short min = ReadShort(pool, "_minThreads"), max = ReadShort(pool, "_maxThreads");
                    short processing = 0, existing = 0, goal = 0; int requested = 0;
                    if (pool.Type?.GetFieldByName("_separated") != null)
                    {
                        ClrValueType sep = pool.ReadValueTypeField("_separated");
                        if (sep.Type?.GetFieldByName("numRequestedWorkers") != null) requested = sep.ReadField<int>("numRequestedWorkers");
                        if (sep.Type?.GetFieldByName("counts") != null)
                        {
                            ClrValueType cv = sep.ReadValueTypeField("counts");
                            if (cv.Type?.GetFieldByName("_data") != null)
                            {
                                ulong data = cv.ReadField<ulong>("_data");
                                processing = (short)(data & 0xFFFF);
                                existing = (short)((data >> 16) & 0xFFFF);
                                goal = (short)((data >> 32) & 0xFFFF);
                            }
                        }
                    }

                    // Work-queue backlog: ThreadPool.s_workQueue (static), else heap scan.
                    ClrObject wq = default;
                    ClrType tpType = runtime.BaseClassLibrary?.GetTypeByName("System.Threading.ThreadPool");
                    ClrStaticField wqField = tpType?.GetStaticFieldByName("s_workQueue");
                    if (wqField != null && dom != null) { try { wq = wqField.ReadObject(dom); } catch { } }
                    if (!wq.IsValid)
                        foreach (ClrObject o in runtime.Heap.EnumerateObjects()) { if (o.Type?.Name == "System.Threading.ThreadPoolWorkQueue") { wq = o; break; } }
                    long backlog = wq.IsValid ? CountWorkQueue(wq) : 0;

                    bool starved = backlog > existing && processing >= goal && goal < max;

                    result["minThreads"] = min;
                    result["maxThreads"] = max;
                    result["existingThreads"] = existing;
                    result["busyThreads"] = processing;
                    result["threadsGoal"] = goal;
                    result["requestedWorkers"] = requested;
                    result["queuedWorkItems"] = backlog;
                    result["starved"] = starved;
                    result["note"] = "starved = queued work exceeds existing worker threads while all goal-threads are busy and the goal hasn't grown to max. The pool injects ~1 thread/500ms, so a sustained backlog with a pinned thread count is starvation — classically sync-over-async blocking pool threads. Pair with vs_async_stacks to see what the pool threads are stuck on. Confirm borderline cases with a second sample.";
                    return result;
                }
            }
        }

        private static short ReadShort(ClrObject o, string field)
        {
            try { return o.Type?.GetFieldByName(field) != null ? o.ReadField<short>(field) : (short)0; } catch { return 0; }
        }

        private static long CountWorkQueue(ClrObject wq)
        {
            long n = 0;
            if (TryObjField(wq, "highPriorityWorkItems", out ClrObject hp)) n += CountConcurrentQueue(hp);
            if (TryObjField(wq, "workItems", out ClrObject wi)) n += CountConcurrentQueue(wi);
            if (TryObjField(wq, "_assignableWorkItemQueues", out ClrObject aq) && aq.IsArray)
            {
                ClrArray arr = aq.AsArray();
                for (int i = 0; i < arr.Length; i++) { ClrObject q = arr.GetObjectValue(i); if (q.IsValid && !q.IsNull) n += CountConcurrentQueue(q); }
            }
            return n;
        }

        // ConcurrentQueue<object> length: walk _head -> _slots[].Item -> _nextSegment (slightly over-counts; fine).
        private static long CountConcurrentQueue(ClrObject q)
        {
            long n = 0;
            if (!TryObjField(q, "_head", out ClrObject seg)) return 0;
            int guard = 0;
            while (seg.IsValid && !seg.IsNull && guard++ < 100000)
            {
                if (TryObjField(seg, "_slots", out ClrObject slots) && slots.IsArray)
                {
                    ClrArray sa = slots.AsArray();
                    for (int i = 0; i < sa.Length; i++)
                    {
                        ClrValueType slot = sa.GetStructValue(i);
                        ClrObject item = slot.ReadObjectField("Item");
                        if (item.IsValid && !item.IsNull) n++;
                    }
                }
                if (!TryObjField(seg, "_nextSegment", out ClrObject next)) break;
                seg = next;
            }
            return n;
        }

        // roots: "why is this alive?" — resolve a type name or 0x-address to a target object, then BFS from
        // every GC root (heap roots AND per-thread stack roots) to it, returning the retention path.
        private static JObject ReadRoots(int pid, string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return new JObject { ["error"] = "usage: roots <pid> <typeName|0xaddress>" };

            using (DataTarget dt = DataTarget.CreateSnapshotAndAttach(pid))
            {
                ClrInfo clrInfo = dt.ClrVersions.FirstOrDefault();
                if (clrInfo == null)
                    return new JObject { ["error"] = "no CLR found in target (managed process? host/target bitness must match)" };

                using (ClrRuntime runtime = clrInfo.CreateRuntime())
                {
                    ClrHeap heap = runtime.Heap;
                    ulong targetAddr = 0; long instanceCount = 0; string targetType = null;

                    if (target.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        try { targetAddr = Convert.ToUInt64(target.Substring(2), 16); } catch { }
                        if (targetAddr == 0) return new JObject { ["error"] = $"bad address '{target}'" };
                        targetType = heap.GetObject(targetAddr).Type?.Name;
                    }
                    else
                    {
                        long scan = 0;
                        foreach (ClrObject o in heap.EnumerateObjects())
                        {
                            if (scan++ > 5_000_000) break;
                            if (o.Type == null) continue;
                            string name = o.Type.Name;
                            if (name == null) continue;
                            if (name == target || name.IndexOf(target, StringComparison.Ordinal) >= 0)
                            {
                                if (targetAddr == 0) { targetAddr = o.Address; targetType = name; }
                                if (++instanceCount >= 200000) break;
                            }
                        }
                        if (targetAddr == 0) return new JObject { ["clr"] = $"{clrInfo.Flavor} {clrInfo.Version}", ["note"] = $"no live objects of type '{target}' found." };
                    }

                    var (path, rootKind) = FindRetentionPath(runtime, targetAddr);

                    var result = new JObject
                    {
                        ["clr"] = $"{clrInfo.Flavor} {clrInfo.Version}",
                        ["target"] = $"0x{targetAddr:x}",
                        ["targetType"] = targetType ?? "<unknown>",
                    };
                    if (instanceCount > 0) result["instanceCount"] = instanceCount;

                    if (path == null)
                    {
                        result["rooted"] = false;
                        result["note"] = "No retention path from a GC root — the object is unrooted (eligible for collection / already dead), or the search hit its ceiling.";
                    }
                    else
                    {
                        result["rooted"] = true;
                        result["rootKind"] = rootKind;
                        var arr = new JArray();
                        foreach (var (a, ty) in path) arr.Add(new JObject { ["address"] = $"0x{a:x}", ["type"] = ty });
                        result["retentionPath"] = arr;
                        result["note"] = "Retention path: GC root (first frame) → … → target (last frame); each frame holds a reference to the next, so the chain is *why* the target can't be collected. rootKind names the anchor (StaticVar = a static field, Stack = a thread local, StrongHandle/Pinned = a GC handle, FinalizerQueue = pending finalization).";
                    }
                    return result;
                }
            }
        }

        // BFS from all roots (heap + stacks) to targetAddr; returns the path (root→target) + the root's kind.
        private static (List<(ulong addr, string type)> path, string rootKind) FindRetentionPath(ClrRuntime runtime, ulong targetAddr)
        {
            ClrHeap heap = runtime.Heap;
            var visited = new HashSet<ulong>();
            var parent = new Dictionary<ulong, ulong>();
            var rootKindOf = new Dictionary<ulong, string>();
            var queue = new Queue<ulong>();

            void Seed(ClrObject o, string kind)
            {
                if (o.IsNull || o.Address == 0 || !visited.Add(o.Address)) return;
                parent[o.Address] = 0; rootKindOf[o.Address] = kind; queue.Enqueue(o.Address);
            }

            foreach (ClrRoot r in heap.EnumerateRoots()) Seed(r.Object, r.RootKind.ToString());
            foreach (ClrThread t in runtime.Threads)
                foreach (ClrStackRoot sr in t.EnumerateStackRoots()) Seed(sr.Object, "Stack");

            bool found = visited.Contains(targetAddr);
            long steps = 0;
            while (!found && queue.Count > 0)
            {
                ulong cur = queue.Dequeue();
                if (steps++ > 20_000_000) break;
                ClrObject obj = heap.GetObject(cur);
                if (!obj.IsValid || obj.Type == null || !obj.Type.ContainsPointers) continue;
                foreach (ulong child in obj.EnumerateReferenceAddresses())
                {
                    if (child == 0 || !visited.Add(child)) continue;
                    parent[child] = cur;
                    queue.Enqueue(child);
                    if (child == targetAddr) { found = true; break; }
                }
            }

            if (!parent.ContainsKey(targetAddr)) return (null, null);

            var path = new List<(ulong, string)>();
            var guard = new HashSet<ulong>();
            ulong a = targetAddr;
            while (guard.Add(a))
            {
                path.Add((a, heap.GetObject(a).Type?.Name ?? "<unknown>"));
                if (!parent.TryGetValue(a, out ulong p) || p == 0) break;
                a = p;
            }
            path.Reverse();
            string rootKind = rootKindOf.TryGetValue(path[0].Item1, out string rk) ? rk : "?";
            return (path, rootKind);
        }

        // heapdiff: first call (no baseline file) captures per-type {count,bytes} to baselinePath; later
        // calls diff the current heap against it and report what GREW (the leak finder). Baseline persists.
        private static JObject ReadHeapDiff(int pid, string baselinePath)
        {
            if (string.IsNullOrWhiteSpace(baselinePath))
                return new JObject { ["error"] = "usage: heapdiff <pid> <baselinePath>" };

            using (DataTarget dt = DataTarget.CreateSnapshotAndAttach(pid))
            {
                ClrInfo clrInfo = dt.ClrVersions.FirstOrDefault();
                if (clrInfo == null)
                    return new JObject { ["error"] = "no CLR found in target (managed process? host/target bitness must match)" };

                using (ClrRuntime runtime = clrInfo.CreateRuntime())
                {
                    ClrHeap heap = runtime.Heap;
                    var cur = new Dictionary<string, long[]>(); // type -> [count, bytes]
                    long scanned = 0; bool truncated = false;
                    foreach (ClrObject obj in heap.EnumerateObjects())
                    {
                        if (!obj.IsValid || obj.Type == null) continue;
                        if (scanned++ > 5_000_000) { truncated = true; break; }
                        string name = obj.Type.Name ?? "<unknown>";
                        if (!cur.TryGetValue(name, out long[] e)) cur[name] = e = new long[2];
                        e[0] += 1; e[1] += (long)obj.Size;
                    }

                    if (!File.Exists(baselinePath))
                    {
                        var bo = new JObject();
                        foreach (var kv in cur) bo[kv.Key] = new JArray(kv.Value[0], kv.Value[1]);
                        try { File.WriteAllText(baselinePath, bo.ToString(Formatting.None)); }
                        catch (Exception e) { return new JObject { ["error"] = $"could not write baseline: {e.Message}" }; }
                        return new JObject
                        {
                            ["clr"] = $"{clrInfo.Flavor} {clrInfo.Version}",
                            ["mode"] = "baseline",
                            ["types"] = cur.Count,
                            ["totalObjects"] = cur.Values.Sum(v => v[0]),
                            ["note"] = "Baseline captured. Let the process run, then call vs_heap_diff again to see what grew.",
                        };
                    }

                    JObject baseline;
                    try { baseline = JObject.Parse(File.ReadAllText(baselinePath)); }
                    catch (Exception e) { return new JObject { ["error"] = $"could not read baseline: {e.Message}" }; }

                    var grew = new List<(string type, long dCount, long dBytes)>();
                    foreach (var kv in cur)
                    {
                        long bCount = 0, bBytes = 0;
                        if (baseline[kv.Key] is JArray ba && ba.Count >= 2) { bCount = (long)ba[0]; bBytes = (long)ba[1]; }
                        long dCount = kv.Value[0] - bCount;
                        long dBytes = kv.Value[1] - bBytes;
                        if (dCount != 0 || dBytes != 0) grew.Add((kv.Key, dCount, dBytes));
                    }

                    var arr = new JArray();
                    foreach (var g in grew.OrderByDescending(x => x.dBytes).Take(40))
                        arr.Add(new JObject { ["type"] = g.type, ["countDelta"] = g.dCount, ["bytesDelta"] = g.dBytes });

                    var result = new JObject
                    {
                        ["clr"] = $"{clrInfo.Flavor} {clrInfo.Version}",
                        ["mode"] = "diff",
                        ["grew"] = arr,
                        ["note"] = "Per-type growth since the baseline (sorted by bytes gained; negatives shrank). A type that keeps climbing across repeated diffs is the leak. The baseline is preserved — call again to keep watching (pass reset to re-baseline). Then vs_gc_roots the growing type to see what's holding it.",
                    };
                    if (truncated) result["truncated"] = true;
                    return result;
                }
            }
        }
    }
}
