// Concord spike component. Three jobs:
//   Rung 0  - IDkmCallStackFilter injects "[ClaudeCodeVS Concord Spike]" (proves it loads).
//   Rung 1a - IDkmDataBreakpointHitNotification logs the engine's own data-BP anatomy.
//   Rung 1b - at the first NORMAL breakpoint hit, ARM a managed data breakpoint on "target.Value"
//             entirely from this IDE-level component, via the public-API chain we reverse-engineered:
//
//   GetTopStackFrame -> DkmLanguageExpression.Create("target.Value")
//     -> DkmInspectionContext.EvaluateExpression(...)            (async, work list)
//     -> (DkmSuccessEvaluationResult).GetDataBreakpointInfo()    -> { Identifier, Size }
//     -> DkmPendingDataBreakpoint.Create(..., Identifier, Size)
//     -> .Enable(...)                                            (Microsoft's runtime monitor arms it)
//
// If, after arming, a write to target.Value stops the debuggee with NO user data breakpoint set,
// Rung 1b PASSES and managed "break when value changes" is reachable programmatically.
//
// Every signature verified by reflection against Microsoft.VisualStudio.Debugger.Engine 17.14.1051801.

using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.Breakpoints;
using Microsoft.VisualStudio.Debugger.CallStack;
using Microsoft.VisualStudio.Debugger.Clr;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Microsoft.VisualStudio.Debugger.Evaluation;

namespace ConcordSpike
{
    public class SpikeService :
        IDkmCallStackFilter,
        IDkmBoundBreakpointHitNotification,
        IDkmDataBreakpointHitNotification
    {
        // The expression we arm a data breakpoint on. Hard-coded to the spike fixture's field.
        private const string WatchExpression = "target.Value";

        // SourceId observed from a UI-set "Break When Value Changes" - reuse it so VS treats our
        // programmatic breakpoint identically to a user one.
        private static readonly Guid UserDataBpSourceId = new Guid("f7a1b1d1-d4ee-4e0e-9bac-bdaa38c83fe3");

        // One-shot: arm on the first normal breakpoint only.
        private static bool _armed;

        // ---------- Rung 0 canary ----------
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

        // ---------- Rung 1b: arm on the first normal breakpoint ----------
        void IDkmBoundBreakpointHitNotification.OnBoundBreakpointHit(
            DkmBoundBreakpoint bp, DkmThread thread, bool hasException, DkmEventDescriptorS eventDescriptor)
        {
            try
            {
                Log.Line("---- BOUND BP HIT (normal breakpoint) ----");
                if (!_armed)
                    TryArmDataBreakpoint(thread);
            }
            catch (Exception ex) { Log.Line("  !! OnBoundBreakpointHit threw: " + ex); }
            // never Suppress - let VS break normally
        }

        private static void TryArmDataBreakpoint(DkmThread thread)
        {
            Log.Line(">>> Rung 1b: arming data breakpoint on '" + WatchExpression + "'");
            DkmProcess process = thread.Process;

            // 1. find the CLR runtime instance
            DkmClrRuntimeInstance clr = null;
            foreach (DkmRuntimeInstance ri in process.GetRuntimeInstances())
                if (ri is DkmClrRuntimeInstance c) { clr = c; break; }
            if (clr == null) { Log.Line("  abort: no DkmClrRuntimeInstance"); return; }

            // 2. the frame to evaluate in (a normal BP in Main => top frame is Main, target in scope)
            DkmStackWalkFrame frame = thread.GetTopStackFrame();
            if (frame == null) { Log.Line("  abort: no top stack frame"); return; }
            Log.Line("  top frame: " + (frame.Description ?? "<null>"));

            // 3. build the C# expression + inspection context
            var compilerId = new DkmCompilerId(DkmVendorId.Microsoft, DkmLanguageId.CSharp);
            DkmLanguage language = DkmLanguage.Create("C#", compilerId);
            DkmLanguageExpression expr = DkmLanguageExpression.Create(
                language, DkmEvaluationFlags.NoSideEffects, WatchExpression, null);
            DkmInspectionSession session = DkmInspectionSession.Create(process, null);
            DkmInspectionContext ctx = DkmInspectionContext.Create(
                session, clr, thread, 5000,
                DkmEvaluationFlags.NoSideEffects, DkmFuncEvalFlags.None, 10, language, null);

            // 4. evaluate the expression (async; Execute() drains the work list synchronously here)
            DkmEvaluationResult evalResult = null;
            int evalError = 0;
            DkmWorkList wl = DkmWorkList.Create(null);
            ctx.EvaluateExpression(wl, expr, frame, ar => { evalError = ar.ErrorCode; evalResult = ar.ResultObject; });
            wl.Execute();
            Log.Line("  evaluate: errorCode=" + evalError + " resultType=" + (evalResult?.GetType().Name ?? "<null>"));

            var success = evalResult as DkmSuccessEvaluationResult;
            if (success == null)
            {
                if (evalResult is DkmFailedEvaluationResult fail)
                    Log.Line("  abort: evaluation failed: " + (fail.ErrorMessage ?? "<null>"));
                else
                    Log.Line("  abort: evaluation did not produce a success result");
                return;
            }
            Log.Line("  evaluated: Value=" + (success.Value ?? "<null>") + " Type=" + (success.Type ?? "<null>"));

            // 5. mint the data-breakpoint binding from the evaluation result
            string infoErr;
            DkmDataBreakpointInfo info = success.GetDataBreakpointInfo(out infoErr);
            Log.Line("  GetDataBreakpointInfo: error=" + (infoErr ?? "<null>") +
                     " Identifier=" + (info.Identifier ?? "<null>") + " Size=" + info.Size);
            if (!string.IsNullOrEmpty(infoErr) || string.IsNullOrEmpty(info.Identifier))
            {
                Log.Line("  abort: no data-breakpoint info for this expression");
                return;
            }

            // 6. create + enable the pending data breakpoint
            DkmPendingDataBreakpoint pending = DkmPendingDataBreakpoint.Create(
                process, UserDataBpSourceId, compilerId, thread, false, info.Identifier, (int)info.Size, null);
            Log.Line("  created DkmPendingDataBreakpoint; enabling (THE load-bearing IDE-level Enable)...");

            int enableError = 0;
            DkmWorkList wl2 = DkmWorkList.Create(null);
            pending.Enable(wl2, ar => { enableError = ar.ErrorCode; });
            wl2.Execute();
            Log.Line("  Enable: errorCode=" + enableError + (enableError == 0 ? "" : " (0x" + enableError.ToString("X8") + ")"));

            _armed = true;
            Log.Line(enableError == 0
                ? ">>> ARMED OK. Continue - a write to " + WatchExpression + " should now break with NO user data BP."
                : ">>> Enable returned an error - see code above; arming likely failed.");
        }

        // ---------- Rung 1a/1b observation: a DATA breakpoint hit (UI-set OR our armed one) ----------
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

        // Dump everything readable off a bound breakpoint - the observation payload.
        private static void DescribeBound(DkmBoundBreakpoint bp)
        {
            if (bp == null) { Log.Line("  bound: <null>"); return; }
            Field("bound.Type", () => bp.GetType().FullName);
            Field("bound.SourceId", () => bp.SourceId.ToString());
            DkmRuntimeBreakpoint target = null;
            Field("bound.Target.Type", () => (target = bp.Target)?.GetType().FullName);
            DkmPendingBreakpoint pending = null;
            Field("bound.Pending.Type", () => (pending = bp.PendingBreakpoint)?.GetType().FullName);
            if (pending is DkmPendingDataBreakpoint pdb)
            {
                Field("  Pending.DataElementLocation", () => pdb.DataElementLocation);
                Field("  Pending.Size", () => pdb.Size.ToString());
                Field("  Pending.SourceId", () => pdb.SourceId.ToString());
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

    // Append-only file logger; logging must never perturb the debug session.
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
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* never throw from logging */ }
        }
    }
}
