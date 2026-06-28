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
            // 'target' is a local so it shows up in the Locals window; expand it and set the data
            // breakpoint on its Value field. The local keeps the object rooted for the whole loop.
            var target = new Watcher();

            Console.WriteLine($"DataBpTarget pid={Process.GetCurrentProcess().Id}");
            Console.WriteLine("At the Debugger.Break() below: in Locals, expand 'target', right-click");
            Console.WriteLine("'Value' -> Break When Value Changes. Then continue - each write should trip it.");

            // STOP POINT: set the data breakpoint on target.Value here, then continue.
            Debugger.Break();

            for (int i = 1; i <= 10; i++)
            {
                target.Value = i * 100L;                     // <-- writes to the watched instance field
                Console.WriteLine($"target.Value = {target.Value}");
                Thread.Sleep(1000);
            }

            Console.WriteLine("done");
            Console.WriteLine("(press Enter to exit)");
            Console.ReadLine();

            // keep the local provably alive to the end so the JIT can't drop it early
            GC.KeepAlive(target);
        }
    }
}
