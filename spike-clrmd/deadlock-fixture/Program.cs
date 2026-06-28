using System;
using System.Threading;

// Minimal, deterministic AB-BA deadlock for the ClrMD probe.
// T1 holds L1 wants L2; T2 holds L2 wants L1. The Barrier guarantees both
// first-locks are held before either reaches for the second => 2 held monitors,
// each with exactly one waiter. Main parks forever to keep the process alive.
internal static class Program
{
    private static readonly object L1 = new object();
    private static readonly object L2 = new object();
    private static readonly Barrier Lined = new Barrier(2);

    private static void Main()
    {
        new Thread(() => { lock (L1) { Lined.SignalAndWait(); lock (L2) { } } }) { Name = "T1", IsBackground = true }.Start();
        new Thread(() => { lock (L2) { Lined.SignalAndWait(); lock (L1) { } } }) { Name = "T2", IsBackground = true }.Start();
        Console.WriteLine("deadlocking...");
        Thread.Sleep(Timeout.Infinite);
    }
}
