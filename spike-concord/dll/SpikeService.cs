// Concord data-breakpoint component — file-IPC productization (Chunk 1).
//
// PROVEN so far (spike 0.1-0.9): we can arm a managed data breakpoint from code on the REQUEST
// thread (FilterNextFrame), it fires crash-free on every change with old->new values, and the only
// gotcha is using our OWN SourceId (never the engine's). In-engine halt is impossible from the hit
// notification (event thread) - the EXTENSION halts via EnvDTE instead.
//
// This version makes the component file-driven so the real extension can drive it without a
// DkmCustomMessage bridge:
//
//   request.txt   (extension WRITES, component READS):  line1 = requestId, line2 = expression
//   status.txt    (component WRITES):                   "<requestId> armed" | "<requestId> error: ..."
//   events.jsonl  (component APPENDS, extension TAILS): one JSON line per change
//
// The component checks request.txt on each stack walk (FilterNextFrame, request-side) and arms the
// requested expression; on each hit it appends the change to events.jsonl. The extension tails
// events.jsonl, returns changes to the model, and calls EnvDTE Break on-demand for "stop on change".
//
// All Dkm signatures verified vs Microsoft.VisualStudio.Debugger.Engine 17.14.1051801.

using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.Breakpoints;
using Microsoft.VisualStudio.Debugger.CallStack;
using Microsoft.VisualStudio.Debugger.Clr;
using Microsoft.VisualStudio.Debugger.ComponentInterfaces;
using Microsoft.VisualStudio.Debugger.Evaluation;
using Microsoft.VisualStudio.Debugger.Symbols;

namespace ConcordSpike
{
    public class SpikeService :
        IDkmCallStackFilter,
        IDkmDataBreakpointHitNotification
    {
        // OUR OWN SourceId (never the engine's f7a1b1d1 - that crashes). Identifies breakpoints we own.
        private static readonly Guid OurDataBpSourceId = new Guid("047dd4c9-7540-43bf-be25-e824b1316f44");

        private static readonly string IpcDir = Path.Combine(Path.GetTempPath(), "claude-codevs-databp");
        private static string RequestFile => Path.Combine(IpcDir, "request.txt");
        private static string StatusFile => Path.Combine(IpcDir, "status.txt");
        private static string EventsFile => Path.Combine(IpcDir, "events.jsonl");

        // The requestId we've already armed (so we arm a given request exactly once). null = none yet.
        private static string _armedRequestId;
        private static string _armedExpression;
        private static bool _captureStack;   // model-driven: capture method+file:line per change
        private static bool _arming;   // re-entrancy guard while an arm attempt runs

        // ---------- request-thread trigger: arm on a stack walk when a new request is pending ----------
        DkmStackWalkFrame[] IDkmCallStackFilter.FilterNextFrame(DkmStackContext stackContext, DkmStackWalkFrame input)
        {
            if (input == null)
                return null;

            if (!_arming)
            {
                _arming = true;
                try { TryProcessRequest(input); }
                catch (Exception ex) { Log.Line("arm threw: " + ex.Message); }
                finally { _arming = false; }
            }

            // Canary: keep proving the component is loaded.
            SpikeDataItem dataItem = SpikeDataItem.GetInstance(stackContext);
            if (dataItem.State == State.Initial)
            {
                var frames = new DkmStackWalkFrame[2];
                frames[0] = DkmStackWalkFrame.Create(stackContext.Thread, null, input.FrameBase, 0,
                    DkmStackWalkFrameFlags.None, "[ClaudeCodeVS Concord Spike]", null, null);
                frames[1] = input;
                dataItem.State = State.FrameAdded;
                return frames;
            }
            return new DkmStackWalkFrame[1] { input };
        }

        private static void TryProcessRequest(DkmStackWalkFrame frame)
        {
            // request.txt: line1 = requestId, line2 = expression (e.g. "target.Value"),
            // optional line3 = "true" to capture method+file:line per change.
            string id, expression; bool captureStack;
            try
            {
                if (!File.Exists(RequestFile)) return;
                string[] lines = File.ReadAllLines(RequestFile);
                if (lines.Length < 2) return;
                id = lines[0].Trim();
                expression = lines[1].Trim();
                captureStack = lines.Length >= 3 && lines[2].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            catch { return; }

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(expression)) return;
            if (id == _armedRequestId) return;   // already armed this request

            // Split "owner.field" - we evaluate the OWNER then drill to the field child (the child
            // result is anchored to the heap object; a bare expression isn't trackable).
            int dot = expression.LastIndexOf('.');
            if (dot <= 0 || dot >= expression.Length - 1)
            {
                WriteStatus(id, "error: expression must be owner.field (data BPs need an instance field)");
                _armedRequestId = id;   // don't retry a malformed request
                return;
            }
            string owner = expression.Substring(0, dot);
            string field = expression.Substring(dot + 1);

            string err = TryArm(frame, owner, field);
            if (err == null)
            {
                _armedRequestId = id;
                _armedExpression = expression;
                _captureStack = captureStack;
                WriteStatus(id, "armed");
                Log.Line("ARMED request " + id + " on " + expression + (captureStack ? " (captureStack)" : ""));
            }
            else if (err == "retry")
            {
                // owner not in scope on this frame/walk - leave un-armed so a later walk retries.
            }
            else
            {
                _armedRequestId = id;   // hard failure - don't spin
                WriteStatus(id, "error: " + err);
                Log.Line("ARM FAILED request " + id + ": " + err);
            }
        }

        // Returns null on success, "retry" if the owner isn't in scope yet, or an error string.
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

            // 1. evaluate the owning object
            DkmLanguageExpression expr = DkmLanguageExpression.Create(language, DkmEvaluationFlags.NoSideEffects, owner, null);
            DkmEvaluationResult ownerResult = null;
            DkmWorkList wl = DkmWorkList.Create(null);
            ctx.EvaluateExpression(wl, expr, frame, ar => { ownerResult = ar.ResultObject; });
            wl.Execute();
            if (!(ownerResult is DkmSuccessEvaluationResult)) return "retry";   // not in scope yet

            // 2. drill to the field child (anchors to the heap object)
            DkmEvaluationResult[] children = null;
            DkmWorkList wlc = DkmWorkList.Create(null);
            ownerResult.GetChildren(wlc, 100, ctx, ar => { children = ar.InitialChildren; });
            wlc.Execute();
            DkmSuccessEvaluationResult fieldResult = null;
            if (children != null)
                foreach (DkmEvaluationResult ch in children)
                    if (ch is DkmSuccessEvaluationResult s && s.Name == field) { fieldResult = s; break; }
            if (fieldResult == null) return "field '" + field + "' not found on " + owner;

            // 3. mint the data-breakpoint binding from the child result
            string infoErr;
            DkmDataBreakpointInfo info = fieldResult.GetDataBreakpointInfo(out infoErr);
            if (!string.IsNullOrEmpty(infoErr) || string.IsNullOrEmpty(info.Identifier))
                return string.IsNullOrEmpty(infoErr) ? "no data-breakpoint info" : infoErr.Replace("\r", " ").Replace("\n", " ");

            // 4. create (OUR SourceId) + enable async (BeginExecution, never Execute)
            DkmPendingDataBreakpoint pending = DkmPendingDataBreakpoint.Create(
                process, OurDataBpSourceId, compilerId, thread, false, info.Identifier, (int)info.Size, null);
            _enablePending = pending;
            _enableWl = DkmWorkList.Create(null);
            _enableCb = ar => { _enableWl = null; _enablePending = null; _enableCb = null; };
            pending.Enable(_enableWl, _enableCb);
            _enableWl.BeginExecution();
            return null;
        }

        // GC roots for the async Enable (engine doesn't root our managed callback across BeginExecution).
        private static DkmWorkList _enableWl;
        private static DkmPendingDataBreakpoint _enablePending;
        private static DkmCompletionRoutine<DkmEnablePendingBreakpointAsyncResult> _enableCb;

        // ---------- change events: append to events.jsonl (extension tails this) ----------
        void IDkmDataBreakpointHitNotification.OnDataBreakpointHit(
            DkmBoundBreakpoint bp, DkmThread thread, bool hasException, string message, DkmEventDescriptorS eventDescriptor)
        {
            try
            {
                // Only report breakpoints WE armed (match our SourceId).
                if (bp == null || bp.SourceId != OurDataBpSourceId) return;

                // Model-driven: capture WHERE the write happened (top frame method + file:line) only
                // when the request asked for it. The process is stopped here, so the thread's top
                // frame IS the writing location.
                string location = null;
                if (_captureStack && thread != null)
                {
                    try
                    {
                        DkmBasicInstructionSymbolInfo sym = thread.GetTopStackFrame()?.BasicSymbolInfo;
                        if (sym != null)
                        {
                            location = sym.MethodName ?? "?";
                            DkmSourcePosition pos = sym.SourcePosition;
                            if (pos != null)
                                location += " @ " + (pos.DocumentName ?? "?") + ":" + pos.TextSpan.StartLine;
                        }
                    }
                    catch (Exception lex) { location = "<location error: " + lex.Message + ">"; }
                }

                AppendEvent(_armedRequestId, _armedExpression, message, location);
            }
            catch (Exception ex) { Log.Line("OnDataBreakpointHit threw: " + ex.Message); }
            // Note: we do NOT halt here - in-engine halt from this notification doesn't work.
            // The extension calls EnvDTE Break when it reads a change and stop-on-change is set.
        }

        private static void WriteStatus(string id, string status)
        {
            try { Directory.CreateDirectory(IpcDir); File.WriteAllText(StatusFile, id + " " + status, Encoding.UTF8); }
            catch { }
        }

        private static void AppendEvent(string id, string expression, string message, string location)
        {
            try
            {
                Directory.CreateDirectory(IpcDir);
                string line = "{\"requestId\":" + Json(id) + ",\"expression\":" + Json(expression) +
                              ",\"change\":" + Json(message) +
                              (location != null ? ",\"location\":" + Json(location) : "") + "}";
                File.AppendAllText(EventsFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        // Minimal JSON string encoder (no Newtonsoft in the engine-loaded component).
        private static string Json(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
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
            sb.Append('"');
            return sb.ToString();
        }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();
        private static readonly string FilePath =
            Path.Combine(Path.GetTempPath(), "claude-codevs-databp", "component.log");

        public static void Line(string s)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                lock (Gate)
                    File.AppendAllText(FilePath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}
