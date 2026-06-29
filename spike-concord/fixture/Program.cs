using System;
using System.Diagnostics;
using System.Threading;

namespace DataBpTarget
{
    // A long-lived heap object whose INSTANCE field we watch. An instance field of a tracked heap
    // object is the documented-supported managed data-breakpoint target; statics, stack locals,
    // and struct fields are explicitly NOT supported.
    internal sealed class Watcher
    {
        public long Value;   // <-- the watched instance field
    }

    internal static class Program
    {
        private static void Main()
        {
            // 'target' is a local (shows in Locals; keeps the object rooted). At the breakpoint
            // below, the spike component evaluates "target.Value" and arms a data breakpoint on it.
            var target = new Watcher();

            Console.WriteLine($"DataBpTarget pid={Process.GetCurrentProcess().Id}");
            Console.WriteLine("Set a NORMAL breakpoint (F9) on the line marked >>> BREAKPOINT <<< below,");
            Console.WriteLine("then continue (F5). The spike arms a data breakpoint on target.Value at that");
            Console.WriteLine("stop - so the FIRST loop write should break with no data BP set by you.");

            Thread.Sleep(50);   // >>> BREAKPOINT <<<  (set a normal F9 breakpoint on THIS line)

            for (int i = 1; i <= 10; i++)
            {
                target.Value = i * 100L;                     // <-- a write here should trip the armed data BP
                Console.WriteLine($"target.Value = {target.Value}");
                Thread.Sleep(1000);
            }

            Console.WriteLine("done (press Enter to exit)");
            Console.ReadLine();
            GC.KeepAlive(target);   // keep the local provably alive to the end
        }
    }
}
