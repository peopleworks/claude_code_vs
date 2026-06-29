// Concord component that arms managed data breakpoints from code and streams every change to files
// the extension tails. Productized result of spike-concord/ (proven 0.1-0.12). MULTI-WATCH via
// per-ADDRESS engine bindings with fan-out (the engine binds one data breakpoint per address, so two
// watches on the same value share ONE binding and the hit is fanned out to all of them).
//
// File-IPC contract under %TEMP%\claude-codevs-databp\ (extension <-> component, no DkmCustomMessage):
//   requests\<id>.txt  (extension WRITES): line1 = expression (owner.field)
//   removes\<id>       (extension WRITES): empty marker -> disarm watch <id>
//   status\<id>.txt    (component WRITES): "armed" | "error: <msg>" | "removed"
//   events.jsonl       (component APPENDS): one JSON line per (watcher, change) {requestId, expression, change}
//
// One Binding per expression: its own DkmPendingDataBreakpoint + fresh SourceId, plus a list of
// watcher requestIds. OnDataBreakpointHit routes by bp.SourceId -> Binding, then appends one event per
// watcher (fan-out). A second watch on the same expression reuses the binding (no second Create, which
// the engine would shadow). Per-watch condition/stopOnChange live in the extension, so fan-out keeps
// them independent.
//
// Proven constraints: arm on the REQUEST thread (FilterNextFrame); OUR OWN SourceId (never the
// engine's); Enable async (BeginExecution); drill owner->field child for GetDataBreakpointInfo. In-
// engine halt is impossible from the hit notification, so STOP is the extension's job (EnvDTE).

using System;
using System.Collections.Generic;
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
        internal static readonly string IpcDir = Path.Combine(Path.GetTempPath(), "claude-codevs-databp");
        private static string RequestsDir => Path.Combine(IpcDir, "requests");
        private static string RemovesDir => Path.Combine(IpcDir, "removes");
        private static string StatusDir => Path.Combine(IpcDir, "status");
        private static string EventsFile => Path.Combine(IpcDir, "events.jsonl");

        // One engine binding per expression; many watcher requestIds can share it (fan-out).
        private sealed class Binding
        {
            public string Expression;
            public Guid SourceId;
            public DkmPendingDataBreakpoint Pending;
            public readonly List<string> Watchers = new List<string>();   // requestIds sharing this binding
        }

        private static readonly Dictionary<string, Binding> _byExpr = new Dictionary<string, Binding>();   // expression -> binding
        private static readonly Dictionary<Guid, Binding> _bySource = new Dictionary<Guid, Binding>();      // sourceId  -> binding
        private static readonly Dictionary<string, string> _watchExpr = new Dictionary<string, string>();   // requestId -> expression
        private static readonly HashSet<string> _processed = new HashSet<string>();   // armed-or-failed ids (don't retry)
        private static readonly List<object> _enableKeepAlive = new List<object>();   // GC roots for async Enable (session-lifetime)
        private static bool _busy;   // re-entrancy guard

        // ---------- request-thread loop: process removes + new requests on each stack walk ----------
        DkmStackWalkFrame[] IDkmCallStackFilter.FilterNextFrame(DkmStackContext stackContext, DkmStackWalkFrame input)
        {
            if (input == null) return null;

            if (!_busy)
            {
                _busy = true;
                try { ProcessRemoves(); ProcessRequests(input); }
                catch { }
                finally { _busy = false; }
            }

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

        private static void ProcessRequests(DkmStackWalkFrame frame)
        {
            string[] files;
            try { if (!Directory.Exists(RequestsDir)) return; files = Directory.GetFiles(RequestsDir, "*.txt"); }
            catch { return; }

            foreach (string f in files)
            {
                string id = Path.GetFileNameWithoutExtension(f);
                if (_watchExpr.ContainsKey(id) || _processed.Contains(id)) continue;

                string expression;
                try { string[] lines = File.ReadAllLines(f); expression = lines.Length > 0 ? lines[0].Trim() : null; }
                catch { continue; }
                if (string.IsNullOrEmpty(expression)) continue;

                int dot = expression.LastIndexOf('.');
                if (dot <= 0 || dot >= expression.Length - 1)
                {
                    _processed.Add(id);
                    WriteStatus(id, "error: expression must be owner.field (data breakpoints need an instance field)");
                    continue;
                }

                // Fan-out: if a binding already watches this expression, just subscribe this requestId.
                if (_byExpr.TryGetValue(expression, out Binding existing))
                {
                    existing.Watchers.Add(id);
                    _watchExpr[id] = expression;
                    WriteStatus(id, "armed");
                    continue;
                }

                string err = TryArmNew(frame, id, expression, expression.Substring(0, dot), expression.Substring(dot + 1));
                if (err == null) WriteStatus(id, "armed");
                else if (err != "retry") { _processed.Add(id); WriteStatus(id, "error: " + err); }
                // "retry": owner not in scope yet - leave for a later stack walk.
            }
        }

        private static void ProcessRemoves()
        {
            string[] markers;
            try { if (!Directory.Exists(RemovesDir)) return; markers = Directory.GetFiles(RemovesDir); }
            catch { return; }

            foreach (string m in markers)
            {
                string id = Path.GetFileName(m);
                if (_watchExpr.TryGetValue(id, out string expr) && _byExpr.TryGetValue(expr, out Binding b))
                {
                    b.Watchers.Remove(id);
                    if (b.Watchers.Count == 0)   // last watcher gone - Close the engine binding
                    {
                        try { b.Pending?.Close(); } catch { }
                        _byExpr.Remove(expr);
                        _bySource.Remove(b.SourceId);
                    }
                    WriteStatus(id, "removed");
                }
                _watchExpr.Remove(id);
                _processed.Remove(id);
                try { File.Delete(m); } catch { }
                try { File.Delete(Path.Combine(RequestsDir, id + ".txt")); } catch { }   // so it can't re-arm
            }
        }

        // null = armed (binding created); "retry" = owner not in scope yet; else an error message.
        private static string TryArmNew(DkmStackWalkFrame frame, string id, string expression, string owner, string field)
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

            Guid sourceId = Guid.NewGuid();   // OUR OWN per-binding SourceId
            DkmPendingDataBreakpoint pending = DkmPendingDataBreakpoint.Create(
                process, sourceId, compilerId, thread, false, info.Identifier, (int)info.Size, null);

            DkmWorkList ewl = DkmWorkList.Create(null);
            DkmCompletionRoutine<DkmEnablePendingBreakpointAsyncResult> cb = ar => { };
            _enableKeepAlive.Add(ewl); _enableKeepAlive.Add(cb);   // root across the async Enable
            pending.Enable(ewl, cb);
            ewl.BeginExecution();   // async; Execute() would self-deadlock

            var b = new Binding { Expression = expression, SourceId = sourceId, Pending = pending };
            b.Watchers.Add(id);
            _byExpr[expression] = b;
            _bySource[sourceId] = b;
            _watchExpr[id] = expression;
            return null;
        }

        // ---------- a change: route by SourceId to the binding, FAN OUT to every subscribed watcher ----------
        void IDkmDataBreakpointHitNotification.OnDataBreakpointHit(
            DkmBoundBreakpoint bp, DkmThread thread, bool hasException, string message, DkmEventDescriptorS eventDescriptor)
        {
            try
            {
                if (bp == null || !_bySource.TryGetValue(bp.SourceId, out Binding b)) return;   // not ours
                foreach (string requestId in b.Watchers.ToArray())
                    AppendEvent(requestId, b.Expression, message);
            }
            catch { }
            // No in-engine halt here (event-thread restriction) - the extension calls EnvDTE Break.
        }

        private static void WriteStatus(string id, string status)
        {
            try { Directory.CreateDirectory(StatusDir); File.WriteAllText(Path.Combine(StatusDir, id + ".txt"), status, Encoding.UTF8); }
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
