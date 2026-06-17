using System;
using System.Collections.Generic;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Debugging;

/// <summary>
/// Reads the live Visual Studio debugger state (break location, call stack, locals/arguments) via the
/// EnvDTE automation model and shapes it as JSON for the UserPromptSubmit hook to inject into Claude's
/// context. This is the debug analog of <see cref="ClaudeCodeVs.Editor.ErrorListReader"/>: VS already
/// has all of this; we just expose it so the model can reason about runtime values, not just source.
///
/// EnvDTE is UI-thread bound and throws readily while the debugger transitions, so every access is
/// individually guarded and the whole read runs on the UI thread. When not stopped at a breakpoint we
/// return just {"mode": ...} so the hook injects nothing (no noise on non-debugging turns).
/// </summary>
internal static class DebuggerReader
{
    private const int MaxFrames = 20;    // cap call-stack depth sent to the model
    private const int MaxLocals = 60;    // cap variables per frame
    private const int MaxValueLen = 240; // truncate long value renderings (deep object graphs)

    /// <summary>Read a debug-state snapshot. Must be called on the UI thread.</summary>
    public static JObject ReadSnapshot()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var dte = ServiceProvider.GlobalProvider.GetService(typeof(SDTE)) as DTE;
        if (dte == null) return Mode("unknown");
        var dbg = dte.Debugger;
        if (dbg == null) return Mode("unknown");

        dbgDebugMode mode;
        try { mode = dbg.CurrentMode; }
        catch { return Mode("unknown"); }

        // Only break mode has a meaningful stack/locals to report.
        if (mode != dbgDebugMode.dbgBreakMode)
            return Mode(mode == dbgDebugMode.dbgRunMode ? "run" : "design");

        var snap = Mode("break");

        // Where we're stopped. Per-frame source isn't on EnvDTE.StackFrame; for the *current* stop we
        // use the broken-into active document + caret line (VS positions it on the stop line). Precise
        // per-frame file/line via IDebugStackFrame2 is a fast follow (see ROADMAP/CLAUDE notes).
        try
        {
            var doc = dte.ActiveDocument;
            if (doc != null)
            {
                snap["stoppedAt"] = new JObject
                {
                    ["file"] = doc.FullName,
                    ["line"] = CurrentLine(doc),
                    ["function"] = SafeFunction(dbg.CurrentStackFrame),
                };
            }
        }
        catch (Exception e) { Log(snap, $"stoppedAt unavailable: {e.Message}"); }

        // Call stack (innermost first) - function names only for now.
        try
        {
            var frames = new JArray();
            var thread = dbg.CurrentThread;
            if (thread != null)
            {
                int n = 0;
                foreach (StackFrame f in thread.StackFrames)
                {
                    if (n++ >= MaxFrames) break;
                    frames.Add(new JObject { ["function"] = SafeFunction(f) });
                }
            }
            snap["callStack"] = frames;
        }
        catch (Exception e) { Log(snap, $"callStack unavailable: {e.Message}"); }

        // Arguments + locals for the current frame. EnvDTE's Locals collection ALSO includes the method
        // parameters, so read args first and exclude their names from locals to avoid duplicates.
        try
        {
            var frame = dbg.CurrentStackFrame;
            if (frame != null)
            {
                var args = ReadExpressions(frame.Arguments);
                snap["args"] = args;

                var argNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var a in args)
                    argNames.Add((string?)a["name"] ?? "");

                snap["locals"] = ReadExpressions(frame.Locals, argNames);
            }
        }
        catch (Exception e) { Log(snap, $"locals unavailable: {e.Message}"); }

        return snap;
    }

    private static JArray ReadExpressions(Expressions exprs, HashSet<string>? exclude = null)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // EnvDTE Expression access is main-thread only
        var arr = new JArray();
        if (exprs == null) return arr;

        int n = 0;
        foreach (Expression e in exprs)
        {
            string name = "";
            try { name = e.Name ?? ""; } catch { }
            if (exclude != null && exclude.Contains(name)) continue; // drop dupes (params show up in Locals too)
            if (n++ >= MaxLocals) break;

            string type = "", value = "";
            try { type = e.Type ?? ""; } catch { }
            try { value = e.Value ?? ""; } catch { }
            if (value.Length > MaxValueLen) value = value.Substring(0, MaxValueLen) + "…";

            arr.Add(new JObject { ["name"] = name, ["type"] = type, ["value"] = value });
        }
        return arr;
    }

    private static int CurrentLine(Document doc)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // EnvDTE Document/TextSelection access is main-thread only
        try { return doc.Selection is TextSelection ts ? ts.CurrentLine : 0; }
        catch { return 0; }
    }

    private static string SafeFunction(StackFrame f)
    {
        ThreadHelper.ThrowIfNotOnUIThread(); // EnvDTE StackFrame access is main-thread only
        try { return f?.FunctionName ?? ""; }
        catch { return ""; }
    }

    private static JObject Mode(string m) => new JObject { ["mode"] = m };

    /// <summary>Attach a non-fatal note to the snapshot (a partial read is still useful to the model).</summary>
    private static void Log(JObject snap, string note)
    {
        if (snap["notes"] is not JArray notes)
            snap["notes"] = notes = new JArray();
        notes.Add(note);
    }
}
