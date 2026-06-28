// Concord spike component. Two jobs now:
//   1. Rung 0 canary - IDkmCallStackFilter injects "[ClaudeCodeVS Concord Spike]" so we still
//      have proof the component is loaded.
//   2. Rung 1a OBSERVATION - implement the bound- and data-breakpoint HIT notifications and log
//      everything we can read off the breakpoint object to %TEMP%\ConcordSpike-observe.log.
//
// The point of (2): the managed data-breakpoint API (DkmRuntimeClrDataBreakpoint /
// DkmPendingDataBreakpoint) has NO field/address parameter and NO public sample. So instead of
// guessing how a field gets bound, we let the user set a data breakpoint via the WORKING UI, catch
// the hit, and read the engine's own DkmPendingDataBreakpoint.DataElementLocation + .Size + the
// concrete breakpoint types. That observation tells us exactly what to construct in Rung 1b.
//
// All signatures verified against Microsoft.VisualStudio.Debugger.Engine 17.14.1051801.

using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.Breakpoints;
using Microsoft.VisualStudio.Debugger.CallStack;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;

namespace ConcordSpike
{
    public class SpikeService :
        IDkmCallStackFilter,
        IDkmBoundBreakpointHitNotification,
        IDkmDataBreakpointHitNotification
    {
        // ---------- Rung 0 canary: prove the component still loads ----------
        DkmStackWalkFrame[] IDkmCallStackFilter.FilterNextFrame(DkmStackContext stackContext, DkmStackWalkFrame input)
        {
            if (input == null)
                return null;

            SpikeDataItem dataItem = SpikeDataItem.GetInstance(stackContext);
            if (dataItem.State == State.Initial)
            {
                var frames = new DkmStackWalkFrame[2];
                frames[0] = DkmStackWalkFrame.Create(
                    stackContext.Thread, null, input.FrameBase, 0,
                    DkmStackWalkFrameFlags.None, "[ClaudeCodeVS Concord Spike]", null, null);
                frames[1] = input;
                dataItem.State = State.FrameAdded;
                return frames;
            }
            return new DkmStackWalkFrame[1] { input };
        }

        // ---------- Rung 1a: a normal (line/Debugger.Break) breakpoint was hit ----------
        // Confirms our notification hooks fire at break, and gives us the runtime/thread to work
        // from later. We do NOT Suppress - VS breaks normally.
        void IDkmBoundBreakpointHitNotification.OnBoundBreakpointHit(
            DkmBoundBreakpoint bp, DkmThread thread, bool hasException, DkmEventDescriptorS eventDescriptor)
        {
            try
            {
                Log.Line("---- BOUND BP HIT (normal breakpoint) ----");
                DescribeBound(bp);
            }
            catch (Exception ex) { Log.Line("  !! OnBoundBreakpointHit threw: " + ex); }
        }

        // ---------- Rung 1a: a DATA breakpoint was hit (the observation we care about) ----------
        // If this fires for a UI-set "Break When Value Changes", we capture the engine's own field
        // binding. If it NEVER fires even though VS visibly breaks, that itself tells us data-BP
        // hits aren't delivered to an IDE-level component (=> Rung 1b needs a monitor component).
        void IDkmDataBreakpointHitNotification.OnDataBreakpointHit(
            DkmBoundBreakpoint bp, DkmThread thread, bool hasException, string message, DkmEventDescriptorS eventDescriptor)
        {
            try
            {
                Log.Line("==================== DATA BREAKPOINT HIT ====================");
                Log.Line("  message: " + (message ?? "<null>"));
                DescribeBound(bp);
                Log.Line("============================================================");
            }
            catch (Exception ex) { Log.Line("  !! OnDataBreakpointHit threw: " + ex); }
        }

        // Dump everything readable off a bound breakpoint - this IS the observation payload.
        private static void DescribeBound(DkmBoundBreakpoint bp)
        {
            if (bp == null) { Log.Line("  bound: <null>"); return; }

            Field("bound.Type", () => bp.GetType().FullName);
            Field("bound.SourceId", () => bp.SourceId.ToString());
            Field("bound.UniqueId", () => bp.UniqueId.ToString());

            DkmRuntimeBreakpoint target = null;
            Field("bound.Target.Type", () => (target = bp.Target)?.GetType().FullName);
            if (target != null)
            {
                Field("  Target.SourceId", () => target.SourceId.ToString());
                Field("  Target.RuntimeInstance.Type", () => target.RuntimeInstance?.GetType().FullName);
                if (target is DkmRuntimeClrDataBreakpoint clr)
                    Field("  Target.Access [DkmRuntimeClrDataBreakpoint]", () => clr.Access.ToString());
            }

            DkmPendingBreakpoint pending = null;
            Field("bound.Pending.Type", () => (pending = bp.PendingBreakpoint)?.GetType().FullName);
            if (pending is DkmPendingDataBreakpoint pdb)
            {
                // *** the two values Rung 1b needs to reproduce the breakpoint programmatically ***
                Field("  *** Pending.DataElementLocation ***", () => pdb.DataElementLocation);
                Field("  *** Pending.Size ***", () => pdb.Size.ToString());
                Field("  Pending.SourceId", () => pdb.SourceId.ToString());
                Field("  Pending.IsBarrier", () => pdb.IsBarrier.ToString());
            }
        }

        private static void Field(string label, Func<string> get)
        {
            string v;
            try { v = get() ?? "<null>"; }
            catch (Exception ex) { v = "<threw " + ex.GetType().Name + ": " + ex.Message + ">"; }
            Log.Line("  " + label + " = " + v);
        }
    }

    // Minimal append-only file logger. Logging must never perturb the debug session, so every
    // failure is swallowed.
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static readonly string FilePath =
            Path.Combine(Path.GetTempPath(), "ConcordSpike-observe.log");

        public static void Line(string s)
        {
            try
            {
                lock (Gate)
                    File.AppendAllText(FilePath,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine,
                        Encoding.UTF8);
            }
            catch { /* never throw from logging */ }
        }
    }
}
