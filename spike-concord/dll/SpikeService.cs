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

        // DIAGNOSTIC (0.8.0): the UI's data-BP SourceId is f7a1b1d1-... Reusing the ENGINE's OWN
        // SourceId for a breakpoint WE create may confuse the engine's breakpoint manager (it owns
        // that id) - a candidate cause of the crash-on-continue AND the never-delivered completion
        // (the engine could route our Enable callback to ITS handler). Test with OUR OWN unique id.
        private static readonly Guid OurDataBpSourceId = new Guid("047dd4c9-7540-43bf-be25-e824b1316f44");

        // One-shot: _armed flips true only on a SUCCESSFUL arm (so we retry on later stack walks if
        // the field isn't in scope yet). _arming guards against re-entrancy while an attempt runs.
        private static bool _armed;
        private static bool _arming;
        private static int _postArmBeats;   // diagnostic: count stack walks AFTER arming (proves the arm itself didn't crash)
        private static bool _stopRequested;  // one-shot: force a real halt on the first data-BP hit

        // GC roots for the async Enable. The native engine does NOT root our managed work list or
        // completion delegate across BeginExecution, so if a GC runs before Enable completes, the
        // native completion callback lands on freed memory and crashes the debugger. Keep them
        // alive until the completion routine fires, then release.
        private static DkmWorkList _enableWl;
        private static DkmPendingDataBreakpoint _enablePending;
        private static DkmCompletionRoutine<DkmEnablePendingBreakpointAsyncResult> _enableCb;

        // ---------- Rung 0 canary + Rung 1b (v5) REQUEST-THREAD arming trigger ----------
        // FilterNextFrame runs on the REQUEST thread (issue #61: the same arm code that hangs from a
        // breakpoint-hit notification works here). The arm pipeline (GetDataBreakpointInfo / Enable)
        // is documented IDE-component/request-side only, so this is the correct context to arm from.
        DkmStackWalkFrame[] IDkmCallStackFilter.FilterNextFrame(DkmStackContext stackContext, DkmStackWalkFrame input)
        {
            if (input == null)
                return null;

            // Diagnostic: if we get stack walks AFTER arming, the arm itself didn't crash (the crash
            // is later, on continue). Logs the first few only.
            if (_armed && _postArmBeats < 3) { _postArmBeats++; Log.Line("  [alive] post-arm stack walk #" + _postArmBeats); }

            // Arm on the first stack-walk frame where the expression resolves (request-side context).
            if (!_armed && !_arming)
            {
                _arming = true;
                try { if (TryArmDataBreakpoint(input)) _armed = true; }
                catch (Exception ex) { Log.Line("  !! arm threw: " + ex); }
                finally { _arming = false; }
            }

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

        // Kept only for observation; arming moved OFF this event-thread notification (it's the wrong
        // context - see v5 above).
        void IDkmBoundBreakpointHitNotification.OnBoundBreakpointHit(
            DkmBoundBreakpoint bp, DkmThread thread, bool hasException, DkmEventDescriptorS eventDescriptor)
        {
        }

        // Returns true only when the arm pipeline reached the Enable issue (success).
        private static bool TryArmDataBreakpoint(DkmStackWalkFrame frame)
        {
            DkmThread thread = frame.Thread;
            DkmProcess process = frame.Process;
            if (thread == null || process == null) return false;

            DkmClrRuntimeInstance clr = null;
            foreach (DkmRuntimeInstance ri in process.GetRuntimeInstances())
                if (ri is DkmClrRuntimeInstance c) { clr = c; break; }
            if (clr == null) return false;

            Log.Line(">>> Rung 1b v5 (request thread): arming data breakpoint on " + OwnerExpression + "." + FieldName);
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
                // Expected on stack walks before 'target' is in scope - stay un-armed and retry later.
                Log.Line("  (owner '" + OwnerExpression + "' not in scope yet; will retry)");
                return false;
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
            if (fieldResult == null) { Log.Line("  abort: field '" + FieldName + "' not found among children"); return false; }
            Log.Line("  field child: Name=" + fieldResult.Name + " Value=" + (fieldResult.Value ?? "<null>") + " Type=" + (fieldResult.Type ?? "<null>"));

            // 3. mint the data-breakpoint binding from the CHILD result
            string infoErr;
            DkmDataBreakpointInfo info = fieldResult.GetDataBreakpointInfo(out infoErr);
            Log.Line("  GetDataBreakpointInfo: error=" + (infoErr ?? "<null>") +
                     " Identifier=" + (info.Identifier ?? "<null>") + " Size=" + info.Size);
            if (!string.IsNullOrEmpty(infoErr) || string.IsNullOrEmpty(info.Identifier))
            {
                Log.Line("  abort: no data-breakpoint info");
                return false;
            }

            // 4. create + enable the pending data breakpoint - with OUR OWN SourceId (diagnostic)
            DkmPendingDataBreakpoint pending = DkmPendingDataBreakpoint.Create(
                process, OurDataBpSourceId, compilerId, thread, false, info.Identifier, (int)info.Size, null);
            Log.Line("  created DkmPendingDataBreakpoint (SourceId=OURS " + OurDataBpSourceId + "); enabling...");

            // Enable is async (BeginExecution, never the blocking Execute() - that self-deadlocks ->
            // E_XAPI_COMPLETION_ROUTINE_RELEASED). On the REQUEST thread (here) the completion is
            // delivered normally; on the event thread it never was (the v3/v4 crash).
            _enablePending = pending;
            // DIAGNOSTIC: a whole-list completion routine (fires when the work list itself finishes).
            // If THIS logs but the per-item routine doesn't - or neither does - we learn exactly where
            // the async Enable stalls.
            _enableWl = DkmWorkList.Create(wl3 =>
            {
                Log.Line("  [whole-list] Enable work list COMPLETED. IsCanceled=" + wl3.IsCanceled);
            });
            _enableCb = ar =>
            {
                int e = ar.ErrorCode;
                Log.Line(e == 0
                    ? ">>> ARMED OK (per-item completion). write to " + OwnerExpression + "." + FieldName + " should break."
                    : ">>> Enable failed (per-item): errorCode=" + e + " (0x" + e.ToString("X8") + ")");
                _enableWl = null; _enablePending = null; _enableCb = null;   // release roots
            };
            _enablePending.Enable(_enableWl, _enableCb);
            _enableWl.BeginExecution();
            Log.Line("  Enable issued from request thread; wl.IsCanceled=" + _enableWl?.IsCanceled);
            return true;
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

                // The hit notifies us but doesn't halt (our SourceId isn't a registered stopping
                // source, and DkmEventDescriptorS has no "stop" - only Suppress). To turn the
                // tracepoint into a real breakpoint, force a break on the first hit via AsyncBreak.
                if (!_stopRequested)
                {
                    _stopRequested = true;
                    DkmProcess proc = thread?.Process ?? bp?.Process;
                    if (proc != null)
                    {
                        proc.AsyncBreak(true);
                        Log.Line("  -> AsyncBreak(StopImmediately:true) requested (force a real halt)");
                    }
                    else Log.Line("  -> no DkmProcess to AsyncBreak");
                }
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
