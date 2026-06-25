using System;
using System.Diagnostics;
using System.Threading;

namespace LockJam;

// A tiny concurrent "ledger". FIVE background workers run against three accounts. Everything compiles and
// starts, then the process hangs and never prints "settled". Only SOME of the workers are responsible: a
// subset is locked in a cycle, one is merely idle (waiting, but not on a contended lock), and one is
// genuinely busy. The job: attach the debugger, look at ALL the threads, and pick out the ones that form
// the deadlock cycle - distinguishing them from the thread that's just parked and the thread that's fine.
//
// There are no bug-revealing names in the WORK itself (every cycle worker runs the same Transfer method);
// who-holds-what is only visible from runtime state - the thread names encode the route, and each stuck
// worker's locals show the account it holds vs. the one it's blocked acquiring.
//
// Run under the debugger (F5). Once it has wedged it self-breaks via Debugger.Break(), so the thread
// states are frozen for vs_threads to read - no manual "Break All" needed.
internal sealed class Account
{
    public Account(string id) => Id = id;
    public string Id { get; }
    public readonly object Gate = new object();
    public decimal Balance = 1000m;
}

internal static class Program
{
    // Three accounts; three workers each lock two of them in a rotated order -> a 3-node cycle (A->B->C->A).
    private static readonly Account A = new Account("A");
    private static readonly Account B = new Account("B");
    private static readonly Account C = new Account("C");

    // Makes every transfer worker grab its FIRST gate before any reaches for its SECOND, so the cycle
    // forms on every run instead of only on an unlucky interleaving.
    private static readonly Barrier Lined = new Barrier(3);

    // A work queue the dispatcher blocks on. No job is ever posted, so it parks here forever - it IS
    // waiting, but on an empty queue, not a contended lock. The classic false suspect.
    private static readonly SemaphoreSlim Jobs = new SemaphoreSlim(0);

    private static volatile bool _auditing = true;

    private static void Main()
    {
        var threads = new[]
        {
            Worker("xfer A->B", () => Transfer(A, B, 10m)),   // cycle: holds A, will block on B
            Worker("xfer B->C", () => Transfer(B, C, 20m)),   // cycle: holds B, will block on C
            Worker("xfer C->A", () => Transfer(C, A, 30m)),   // cycle: holds C, will block on A
            Worker("audit",     Audit),                       // healthy: spins, never blocks
            Worker("dispatch",  Dispatch),                    // idle: parked on an empty queue, not the cycle
        };

        // Give the workers time to wedge, then break so every thread is frozen for inspection. (With no
        // debugger attached this just hangs - which is the point; attach and you land right here.)
        Thread.Sleep(2000);
        Debugger.Break();                 // <-- when this hits, ask Claude which threads are deadlocked

        _auditing = false;
        foreach (var t in threads) t.Join();   // never returns: the cycle workers are stuck
        Console.WriteLine("settled");
    }

    // Lock one account, wait for the others to do the same, then lock the second. The second acquire is
    // where the cycle closes: each worker holds exactly what the next one is blocked waiting for.
    private static void Transfer(Account from, Account to, decimal amount)
    {
        lock (from.Gate)
        {
            Lined.SignalAndWait();        // everyone now holds their first gate...
            lock (to.Gate)                // ...and now blocks here, forever
            {
                from.Balance -= amount;
                to.Balance += amount;
            }
        }
    }

    // Negative control #1: a thread that is actually running (pure spin), not waiting on anything. Should
    // NOT be flagged as waiting - when frozen it's caught inside Audit, not on a lock/wait primitive.
    private static void Audit()
    {
        long ticks = 0;
        while (_auditing)
            ticks = unchecked(ticks * 1664525 + 1013904223);
        GC.KeepAlive(ticks);
    }

    // Negative control #2: a thread that IS waiting - but on an empty work queue (SemaphoreSlim), not a
    // contended lock. Will be flagged waiting, but it is not part of the deadlock cycle.
    private static void Dispatch() => Jobs.Wait();

    private static Thread Worker(string name, Action body)
    {
        var t = new Thread(() => body()) { Name = name, IsBackground = true };
        t.Start();
        return t;
    }
}
