// Concord spike component. Three jobs:
//   Rung 0  - IDkmCallStackFilter injects "[ClaudeCodeVS Concord Spike]" (proves it loads).
//   Rung 1a - IDkmDataBreakpointHitNotification logs the engine's own data-BP anatomy.
//   Rung 1b - at the first NORMAL breakpoint hit, ARM a managed data breakpoint on target.Value
//             entirely from this IDE-level component.
//
// IMPORTANT LESSON (Rung 1b v1 failure): GetDataBreakpointInfo on a BARE expression "target.Value"
// fails with "The value cannot be found on the managed heap and cannot be tracked." The data-BP
// tracker anchors to the OWNING heap object, so you must replicate the UI gesture: evaluate the
// owner ("target"), enumerate its CHILDREN, and call GetDataBreakpointInfo on the field child -
// that child result carries the object's heap address + field offset.
//
//   GetTopStackFrame
//     -> evaluate "target"                                   (DkmInspectionContext.EvaluateExpression)
//     -> ownerResult.GetChildren(...)                        -> find child Name=="Value"
//     -> (child DkmSuccessEvaluationResult).GetDataBreakpointInfo(out err)  -> { Identifier, Size }
//     -> DkmPendingDataBreakpoint.Create(..., Identifier, Size) -> .Enable(...)
//
// Every signature verified by reflection vs Microsoft.VisualStudio.Debugger.Engine 17.14.1051801.

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
        // Evaluate the OWNING object, then drill to this field child (anchors to the heap object).
        private const string OwnerExpression = "target";
        private const string FieldName = "Value";

        // SourceId observed from a UI-set "Break When Value Changes" - reuse so VS treats ours
        // identically to a user breakpoint.
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
        }

        private static void TryArmDataBreakpoint(DkmThread thread)
        {
            Log.Line(">>> Rung 1b: arming data breakpoint on " + OwnerExpression + "." + FieldName);
            DkmProcess process = thread.Process;

            DkmClrRuntimeInstance clr = null;
            foreach (DkmRuntimeInstance ri in process.GetRuntimeInstances())
                if (ri is DkmClrRuntimeInstance c) { clr = c; break; }
            if (clr == null) { Log.Line("  abort: no DkmClrRuntimeInstance"); return; }

            DkmStackWalkFrame frame = thread.GetTopStackFrame();
            if (frame == null) { Log.Line("  abort: no top stack frame"); return; }

            var compilerId = new DkmCompilerId(DkmVendorId.Microsoft, DkmLanguageId.CSharp);
            DkmLanguage language = DkmLanguage.Create("C#", compilerId);
            DkmInspectionSession session = DkmInspectionSession.Create(process, null);
            DkmInspectionContext ctx = DkmInspectionContext.Create(
                session, clr, thread, 5000,
                DkmEvaluationFlags.NoSideEffects, DkmFuncEvalFlags.None, 10, language, null);

            // 1. evaluate the OWNING object expression
            DkmLanguageExpression expr = DkmLanguageExpression.Create(
                language, DkmEvaluationFlags.NoSideEffects, OwnerExpression, null);
            DkmEvaluationResult ownerResult = null;
            DkmWorkList wl = DkmWorkList.Create(null);
            ctx.EvaluateExpression(wl, expr, frame, ar => { ownerResult = ar.ResultObject; });
            wl.Execute();

            var ownerSuccess = ownerResult as DkmSuccessEvaluationResult;
            if (ownerSuccess == null)
            {
                Log.Line("  abort: owner '" + OwnerExpression + "' did not evaluate: " +
                         ((ownerResult as DkmFailedEvaluationResult)?.ErrorMessage ?? ownerResult?.GetType().Name ?? "<null>"));
                return;
            }
            Log.Line("  owner evaluated: Type=" + (ownerSuccess.Type ?? "<null>"));

            // 2. enumerate children, find the field (the child result IS anchored to the heap object)
            DkmEvaluationResult[] children = null;
            DkmWorkList wlc = DkmWorkList.Create(null);
            ownerResult.GetChildren(wlc, 100, ctx, ar => { children = ar.InitialChildren; });
            wlc.Execute();
            Log.Line("  children: " + (children == null ? "<null>" : children.Length.ToString()));

            DkmSuccessEvaluationResult fieldResult = null;
            if (children != null)
            {
                foreach (DkmEvaluationResult ch in children)
                {
                    string name = (ch as DkmSuccessEvaluationResult)?.Name ?? (ch as DkmFailedEvaluationResult)?.Name;
                    Log.Line("    child: Name=" + (name ?? "<" + ch?.GetType().Name + ">"));
                    if (ch is DkmSuccessEvaluationResult s && s.Name == FieldName) fieldResult = s;
                }
            }
            if (fieldResult == null) { Log.Line("  abort: field '" + FieldName + "' not found among children"); return; }
            Log.Line("  field child: Name=" + fieldResult.Name + " Value=" + (fieldResult.Value ?? "<null>") + " Type=" + (fieldResult.Type ?? "<null>"));

            // 3. mint the data-breakpoint binding from the CHILD result
            string infoErr;
            DkmDataBreakpointInfo info = fieldResult.GetDataBreakpointInfo(out infoErr);
            Log.Line("  GetDataBreakpointInfo: error=" + (infoErr ?? "<null>") +
                     " Identifier=" + (info.Identifier ?? "<null>") + " Size=" + info.Size);
            if (!string.IsNullOrEmpty(infoErr) || string.IsNullOrEmpty(info.Identifier))
            {
                Log.Line("  abort: no data-breakpoint info");
                return;
            }

            // 4. create + enable the pending data breakpoint
            DkmPendingDataBreakpoint pending = DkmPendingDataBreakpoint.Create(
                process, UserDataBpSourceId, compilerId, thread, false, info.Identifier, (int)info.Size, null);
            Log.Line("  created DkmPendingDataBreakpoint; enabling (the IDE-level Enable)...");

            // Enable is an IDE-level (>100,000) op whose completion must be re-dispatched back onto
            // THIS event thread. Driving it with the synchronous Execute() pump parks the very thread
            // the reply needs -> self-deadlock until a ~67s timeout, surfaced as
            // E_XAPI_COMPLETION_ROUTINE_RELEASED (0x8EDE000C). Use BeginExecution(): it returns
            // immediately, and the completion routine fires asynchronously once we return from the
            // notification and the dispatcher is free. (Same "issue -> await, never block the
            // dispatcher" discipline as DebuggerDriver.)
            _armed = true;   // one-shot: set before issuing so a re-entrant hit can't double-arm
            DkmWorkList wl2 = DkmWorkList.Create(null);
            pending.Enable(wl2, ar =>
            {
                int e = ar.ErrorCode;
                Log.Line(e == 0
                    ? ">>> ARMED OK (async). A write to " + OwnerExpression + "." + FieldName + " should now break with NO user data BP."
                    : ">>> Enable failed async: errorCode=" + e + " (0x" + e.ToString("X8") + ")");
            });
            wl2.BeginExecution();
            Log.Line("  Enable issued (async, non-blocking); returning from notification.");
        }

        // ---------- observation: a DATA breakpoint hit (UI-set OR our armed one) ----------
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

        private static void DescribeBound(DkmBoundBreakpoint bp)
        {
            if (bp == null) { Log.Line("  bound: <null>"); return; }
            Field("bound.SourceId", () => bp.SourceId.ToString());
            DkmRuntimeBreakpoint target = null;
            Field("bound.Target.Type", () => (target = bp.Target)?.GetType().FullName);
            DkmPendingBreakpoint pending = null;
            Field("bound.Pending.Type", () => (pending = bp.PendingBreakpoint)?.GetType().FullName);
            if (pending is DkmPendingDataBreakpoint pdb)
            {
                Field("  Pending.DataElementLocation", () => pdb.DataElementLocation);
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
            catch { }
        }
    }
}
