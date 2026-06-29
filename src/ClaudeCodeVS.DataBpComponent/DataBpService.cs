// Concord component that arms a managed data breakpoint from code and streams every change to a
// file the extension tails. This is the productized result of spike-concord/ (proven 0.1-0.12).
//
// File-IPC contract under %TEMP%\claude-codevs-databp\  (extension <-> component, no DkmCustomMessage):
//   request.txt   (extension WRITES): line1 = requestId, line2 = expression (owner.field)
//   status.txt    (component WRITES): "<requestId> armed" | "<requestId> error: <msg>"
//   events.jsonl  (component APPENDS): one JSON line per change {requestId, expression, change}
//
// Proven constraints baked in: arm on the REQUEST thread (FilterNextFrame); OUR OWN SourceId (never
// the engine's); Enable async (BeginExecution); drill owner->field child for GetDataBreakpointInfo.
// In-engine halt is impossible from the hit notification, so STOP is the extension's job (EnvDTE).
//
// Single active watch (a new requestId replaces it). All Dkm signatures verified vs engine 17.14.

using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.Breakpoints;
using Microsoft.VisualStudio.Debugger.CallStack;
using Microsoft.VisualStudio.Debugger.Clr;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Microsoft.VisualStudio.Debugger.Evaluation;

namespace ClaudeCodeVs.DataBp
{
    public sealed class DataBpService :
        IDkmCallStackFilter,
        IDkmDataBreakpointHitNotification
    {
        // Our own breakpoint SourceId. NEVER reuse the engine's (f7a1b1d1...) - it crashes the
        // breakpoint manager (the spike's hardest-won lesson).
        private static readonly Guid OurSourceId = new Guid("047dd4c9-7540-43bf-be25-e824b1316f44");

        internal static readonly string IpcDir = Path.Combine(Path.GetTempPath(), "claude-codevs-databp");
        private static string RequestFile => Path.Combine(IpcDir, "request.txt");
        private static string StatusFile => Path.Combine(IpcDir, "status.txt");
        private static string EventsFile => Path.Combine(IpcDir, "events.jsonl");

        private static string _armedRequestId;   // the request we've armed (arm each request once)
        private static string _armedExpression;
        private static bool _arming;              // re-entrancy guard

        // ---------- arm on a request-thread stack walk when a new request is pending ----------
        DkmStackWalkFrame[] IDkmCallStackFilter.FilterNextFrame(DkmStackContext stackContext, DkmStackWalkFrame input)
        {
            if (input == null) return null;

            if (!_arming)
            {
                _arming = true;
                try { TryProcessRequest(input); } catch { } finally { _arming = false; }
            }

            // Load canary (also confirms the component is live in the debug session).
            DataBpDataItem item = DataBpDataItem.GetInstance(stackContext);
            if (item.State == State.Initial)
            {
                var frames = new DkmStackWalkFrame[2];
                frames[0] = DkmStackWalkFrame.Create(stackContext.Thread, null, input.FrameBase, 0,
                    DkmStackWalkFrameFlags.None, "[ClaudeCodeVS data watch]", null, null);
                frames[1] = input;
                item.State = State.FrameAdded;
                return frames;
            }
            return new DkmStackWalkFrame[1] { input };
        }

        private static void TryProcessRequest(DkmStackWalkFrame frame)
        {
            string id, expression;
            try
            {
                if (!File.Exists(RequestFile)) return;
                string[] lines = File.ReadAllLines(RequestFile);
                if (lines.Length < 2) return;
                id = lines[0].Trim();
                expression = lines[1].Trim();
            }
            catch { return; }

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(expression) || id == _armedRequestId) return;

            int dot = expression.LastIndexOf('.');
            if (dot <= 0 || dot >= expression.Length - 1)
            {
                _armedRequestId = id;
                WriteStatus(id, "error: expression must be owner.field (data breakpoints need an instance field)");
                return;
            }

            string err = TryArm(frame, expression.Substring(0, dot), expression.Substring(dot + 1));
            if (err == null)
            {
                _armedRequestId = id;
                _armedExpression = expression;
                WriteStatus(id, "armed");
            }
            else if (err != "retry")
            {
                _armedRequestId = id;   // hard failure - don't spin
                WriteStatus(id, "error: " + err);
            }
            // "retry": leave un-armed so a later stack walk (when the owner is in scope) tries again.
        }

        // null = armed; "retry" = owner not in scope yet; else an error message.
        private static string TryArm(DkmStackWalkFrame frame, string owner, string field)
        {
            DkmThread thread = frame.Thread;
            DkmProcess process = frame.Process;
            if (thread == null || process == null) return "retry";

            DkmClrRuntimeInstance clr = null;
            foreach (DkmRuntimeInstance ri in process.GetRuntimeInstances())
                if (ri is DkmClrRuntimeInstance c) { clr = c; break; }
            if (clr == null) return "no CLR runtime";

            var compilerId = new DkmCompilerId(DkmVendorId.Microsoft, DkmLanguageId.CSharp);
            DkmLanguage language = DkmLanguage.Create("C#", compilerId);
            DkmInspectionSession session = DkmInspectionSession.Create(process, null);
            DkmInspectionContext ctx = DkmInspectionContext.Create(session, clr, thread, 5000,
                DkmEvaluationFlags.NoSideEffects, DkmFuncEvalFlags.None, 10, language, null);

            // evaluate the owning object, then drill to the field child (anchors to the heap object)
            DkmLanguageExpression expr = DkmLanguageExpression.Create(language, DkmEvaluationFlags.NoSideEffects, owner, null);
            DkmEvaluationResult ownerResult = null;
            DkmWorkList wl = DkmWorkList.Create(null);
            ctx.EvaluateExpression(wl, expr, frame, ar => { ownerResult = ar.ResultObject; });
            wl.Execute();
            if (!(ownerResult is DkmSuccessEvaluationResult)) return "retry";

            DkmEvaluationResult[] children = null;
            DkmWorkList wlc = DkmWorkList.Create(null);
            ownerResult.GetChildren(wlc, 100, ctx, ar => { children = ar.InitialChildren; });
            wlc.Execute();
            DkmSuccessEvaluationResult fieldResult = null;
            if (children != null)
                foreach (DkmEvaluationResult ch in children)
                    if (ch is DkmSuccessEvaluationResult s && s.Name == field) { fieldResult = s; break; }
            if (fieldResult == null) return "field '" + field + "' not found on " + owner;

            string infoErr;
            DkmDataBreakpointInfo info = fieldResult.GetDataBreakpointInfo(out infoErr);
            if (!string.IsNullOrEmpty(infoErr) || string.IsNullOrEmpty(info.Identifier))
                return string.IsNullOrEmpty(infoErr) ? "no data-breakpoint info" : infoErr.Replace("\r", " ").Replace("\n", " ");

            DkmPendingDataBreakpoint pending = DkmPendingDataBreakpoint.Create(
                process, OurSourceId, compilerId, thread, false, info.Identifier, (int)info.Size, null);
            _enablePending = pending;
            _enableWl = DkmWorkList.Create(null);
            _enableCb = ar => { _enableWl = null; _enablePending = null; _enableCb = null; };
            pending.Enable(_enableWl, _enableCb);
            _enableWl.BeginExecution();   // async; Execute() would self-deadlock
            return null;
        }

        // GC roots for the async Enable (engine doesn't root our managed callback across BeginExecution).
        private static DkmWorkList _enableWl;
        private static DkmPendingDataBreakpoint _enablePending;
        private static DkmCompletionRoutine<DkmEnablePendingBreakpointAsyncResult> _enableCb;

        // ---------- a change: append to events.jsonl (the extension tails this) ----------
        void IDkmDataBreakpointHitNotification.OnDataBreakpointHit(
            DkmBoundBreakpoint bp, DkmThread thread, bool hasException, string message, DkmEventDescriptorS eventDescriptor)
        {
            try
            {
                if (bp == null || bp.SourceId != OurSourceId) return;   // only our watch
                AppendEvent(_armedRequestId, _armedExpression, message);
            }
            catch { }
            // No in-engine halt here (proven impossible on the event thread) - the extension calls
            // EnvDTE Break when it reads a change and stop-on-change is set.
        }

        private static void WriteStatus(string id, string status)
        {
            try { Directory.CreateDirectory(IpcDir); File.WriteAllText(StatusFile, id + " " + status, Encoding.UTF8); }
            catch { }
        }

        private static void AppendEvent(string id, string expression, string message)
        {
            try
            {
                Directory.CreateDirectory(IpcDir);
                string line = "{\"requestId\":" + Json(id) + ",\"expression\":" + Json(expression) +
                              ",\"change\":" + Json(message) + "}";
                File.AppendAllText(EventsFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private static string Json(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
