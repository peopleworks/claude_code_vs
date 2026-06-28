using System;
using System.Collections.Generic;
using System.Threading;

namespace MemLoad
{
    // A workload fixture for the ClrMD memory/threadpool tools. Run it (F5 or attach), then ask Claude:
    //   vs_heap_stats              -> System.Byte[] dominates the heap; gen sizes, handles, finalizer queue
    //   vs_threadpool              -> starved: 64 blocked work items pin every pool thread, the rest queue
    //   vs_heap_diff               -> baseline, wait, diff -> System.Byte[] keeps growing (the leak)
    //   vs_gc_roots System.Byte[]  -> the retention path (static Retained list) that keeps it alive
    internal static class Program
    {
        // A static collection that keeps growing => a textbook managed "leak", rooted by a static field.
        private static readonly List<byte[]> Retained = new List<byte[]>();

        private static void Main()
        {
            for (int i = 0; i < 2000; i++) Retained.Add(new byte[1024]);            // ~2 MB rooted up front

            for (int i = 0; i < 64; i++)                                            // flood + block the pool
                ThreadPool.QueueUserWorkItem(_ => Thread.Sleep(Timeout.Infinite));  //  -> backlog + starvation

            new Thread(() =>                                                        // keep leaking -> heap_diff growth
            {
                while (true) { lock (Retained) Retained.Add(new byte[4096]); Thread.Sleep(20); }
            }) { IsBackground = true }.Start();

            Console.WriteLine("MemLoad running — leaking byte[] and starving the threadpool. Attach/break and probe.");
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
