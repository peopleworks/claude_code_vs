using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

namespace ClrMdProbe
{
    // ClrMD Step-0 spike.
    //
    // Goal: prove we can read STRUCTURED monitor/lock ownership out of a live .NET
    // process via a process SNAPSHOT, hosted in a net48 + x64 process. This is the
    // "Gap A" read for deadlock triage and the make-or-break test for whether ClrMD
    // composes with a Visual Studio debug session attached to the SAME process.
    //
    // The snapshot path (CreateSnapshotAndAttach -> PssCaptureSnapshot) forks a clone
    // of the target's address space; it is NOT a debug-attach, so it should not contend
    // for the single-debugger-per-process lock that VS holds. That's the thing to verify
    // by running this probe while VS is attached + at a break on the same pid.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1 || !int.TryParse(args[0], out int pid))
            {
                Console.Error.WriteLine("usage: ClrMdProbe <pid>");
                Console.Error.WriteLine("  Snapshots the target and prints CLR threads + sync-block (monitor) ownership.");
                return 2;
            }

            Console.WriteLine($"[probe] host={(Environment.Is64BitProcess ? "x64" : "x86")} clr={Environment.Version} target-pid={pid}");

            try
            {
                // Fork a snapshot of the running process and attach ClrMD to the CLONE.
                using (DataTarget dataTarget = DataTarget.CreateSnapshotAndAttach(pid))
                {
                    ClrInfo[] clrs = dataTarget.ClrVersions.ToArray();
                    if (clrs.Length == 0)
                    {
                        Console.Error.WriteLine("[probe] no CLR found (not a managed process, or host/target bitness mismatch?)");
                        return 1;
                    }
                    foreach (ClrInfo clr in clrs)
                        Console.WriteLine($"[probe] clr-found flavor={clr.Flavor} version={clr.Version}");

                    using (ClrRuntime runtime = clrs[0].CreateRuntime())
                    {
                        ClrThread[] threads = runtime.Threads.ToArray();

                        // Map CLR thread object-address -> thread, to resolve a lock holder back to an OS thread id.
                        var byAddress = new Dictionary<ulong, ClrThread>();
                        foreach (ClrThread t in threads)
                            byAddress[t.Address] = t;

                        Console.WriteLine();
                        Console.WriteLine($"=== Threads ({threads.Length}) ===");
                        foreach (ClrThread t in threads)
                        {
                            ClrStackFrame top = t.EnumerateStackTrace().FirstOrDefault();
                            string topStr = top?.Method?.ToString() ?? top?.ToString() ?? "(no managed frame)";
                            Console.WriteLine($"  os={t.OSThreadId,-6} managed={t.ManagedThreadId,-3} addr=0x{t.Address:x12} alive={t.IsAlive}");
                            Console.WriteLine($"      top: {topStr}");
                        }

                        Console.WriteLine();
                        Console.WriteLine("=== Sync blocks (monitor ownership) ===");
                        int held = 0;
                        foreach (SyncBlock sb in runtime.Heap.EnumerateSyncBlocks())
                        {
                            if (!sb.IsMonitorHeld) continue;
                            held++;
                            string holder = byAddress.TryGetValue(sb.HoldingThreadAddress, out ClrThread ht)
                                ? $"os={ht.OSThreadId} managed={ht.ManagedThreadId}"
                                : $"addr=0x{sb.HoldingThreadAddress:x} (unresolved)";
                            Console.WriteLine($"  obj=0x{sb.Object:x12} heldBy[{holder}] recursion={sb.RecursionCount} waiters={sb.WaitingThreadCount}");
                        }
                        if (held == 0)
                            Console.WriteLine("  (no held monitors found)");

                        Console.WriteLine();
                        Console.WriteLine($"[probe] OK - snapshot read succeeded; {held} held monitor(s).");
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[probe] FAILED: {ex.GetType().Name}: {ex.Message}");
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }
    }
}
