using System;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Debugging;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// Phase 2 debug-awareness tools - the interactive PULL channel. These are exposed on the bridge's
/// secondary MCP surface (POST /mcp, reached by the stdio shim that the CLI launches as an MCP server),
/// NOT on the IDE-protocol WebSocket whose tools the CLI keeps dormant. They let the model fetch live
/// runtime state mid-turn (on demand) rather than only at prompt-submit time (the UserPromptSubmit hook).
///
/// All are read-only and marshal to the UI thread (EnvDTE is main-thread bound). Each returns a JSON
/// snapshot; when not stopped at a breakpoint the snapshot is just {"mode":"run|design|unknown"} so the
/// model can tell "nothing paused" from real data. See DebuggerReader for the EnvDTE access.
/// </summary>
internal sealed class VsDebugStateTool : IIdeTool
{
    public string Name => "vs_debug_state";
    public string Description =>
        "Get Visual Studio's live debugger state: whether it's paused, and if so the stop location, "
        + "call stack (function names, innermost first), and the current frame's arguments and locals "
        + "with their runtime values. Returns {\"mode\":\"break|run|design|unknown\", ...}. Use this to "
        + "inspect actual runtime values while debugging, not just source.";

    public JToken Schema => new JObject { ["type"] = "object", ["properties"] = new JObject() };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var snap = DebuggerReader.ReadSnapshot();
        var fn = (string?)snap["stoppedAt"]?["function"];
        var vals = DebuggerReader.SummarizeValues(snap);
        Log.Info($"vs_debug_state -> mode={(string?)snap["mode"]}{(fn != null ? $" @ {fn}" : "")}{(vals.Length > 0 ? $" · {vals}" : "")}");
        Ui.BridgeStatus.RecordDebugInspect();
        return snap;
    }
}

/// <summary>vs_list_breakpoints - all breakpoints (works in any mode, even before a run starts).</summary>
internal sealed class VsListBreakpointsTool : IIdeTool
{
    public string Name => "vs_list_breakpoints";
    public string Description =>
        "List all breakpoints set in Visual Studio (file, line, function, enabled, current hit count, and "
        + "condition if any). Works even when not running, so you can see where execution will stop. "
        + "Returns {\"mode\":..., \"breakpoints\":[...]}.";

    public JToken Schema => new JObject { ["type"] = "object", ["properties"] = new JObject() };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var result = DebuggerReader.ReadBreakpoints();
        int count = (result["breakpoints"] as JArray)?.Count ?? 0;
        Log.Info($"vs_list_breakpoints -> {count} breakpoint(s)");
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}

/// <summary>vs_get_frame_locals - args + locals for a specific call-stack frame (walk up to callers).</summary>
internal sealed class VsGetFrameLocalsTool : IIdeTool
{
    public string Name => "vs_get_frame_locals";
    public string Description =>
        "Get the arguments and local variables (with runtime values) for a specific call-stack frame "
        + "while paused. frameIndex 0 is the innermost/current frame; higher indices walk up toward "
        + "callers. By default reads the current/stopped thread; pass threadId (from vs_threads) to read "
        + "ANOTHER thread's frame - e.g. to inspect each thread parked in a deadlock. Use vs_debug_state or "
        + "vs_threads first to see the stacks. Break-mode only.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["frameIndex"] = new JObject
            {
                ["type"] = "integer",
                ["description"] = "Call-stack frame index, 0 = innermost/current (default 0).",
            },
            ["threadId"] = new JObject
            {
                ["type"] = "integer",
                ["description"] = "Thread id (from vs_threads) to read; omit or 0 = the current/stopped thread. Use to read another thread's locals (e.g. each thread in a deadlock cycle).",
            },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        int frameIndex = (int?)args["frameIndex"] ?? 0;
        int threadId = (int?)args["threadId"] ?? 0;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var result = DebuggerReader.ReadFrameLocals(frameIndex, threadId);
        var fn = (string?)result["function"];
        var vals = DebuggerReader.SummarizeValues(result);
        string tgt = threadId > 0 ? $", thread={threadId}" : "";
        Log.Info($"vs_get_frame_locals(frame={frameIndex}{tgt}) -> mode={(string?)result["mode"]}{(fn != null ? $" @ {fn}" : "")}{(vals.Length > 0 ? $" · {vals}" : "")}");
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}

/// <summary>vs_evaluate - evaluate an expression in a chosen frame's context while paused.</summary>
internal sealed class VsEvaluateTool : IIdeTool
{
    public string Name => "vs_evaluate";
    public string Description =>
        "Evaluate an expression in the Visual Studio debugger while paused, in the context of a chosen "
        + "call-stack frame (frameIndex 0 = current). Returns {value, type, isValid}. Use it to probe "
        + "values the locals don't show (e.g. 'order.Items.Count', 'a / b', 'object.ReferenceEquals(x, y)'). "
        + "Break-mode only. IMPORTANT: the VS evaluator does NOT support LINQ or lambdas (e.g. "
        + "'list.Select(x => x.Foo)' returns isValid=false) - prefer simple expressions: indexing, "
        + "field/property access, .Count, .Sum(), arithmetic, ReferenceEquals. Note: expressions with "
        + "side effects (method calls) DO execute - there is no read-only eval. Pass threadId (from "
        + "vs_threads) to evaluate on ANOTHER thread - e.g. read 'from.Id' on each thread in a deadlock.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["expression"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "The expression to evaluate, in the debugged language's syntax.",
            },
            ["frameIndex"] = new JObject
            {
                ["type"] = "integer",
                ["description"] = "Frame to evaluate in, 0 = current (default 0).",
            },
            ["threadId"] = new JObject
            {
                ["type"] = "integer",
                ["description"] = "Thread id (from vs_threads) to evaluate on; omit or 0 = current. Use to read another thread's state, e.g. 'from.Id' on each thread in a deadlock.",
            },
        },
        ["required"] = new JArray("expression"),
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string expression = (string?)args["expression"] ?? "";
        int frameIndex = (int?)args["frameIndex"] ?? 0;
        int threadId = (int?)args["threadId"] ?? 0;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var result = DebuggerReader.Evaluate(expression, frameIndex, threadId);

        // Log the actual result so the Output pane reads like a debug session (not just "valid=True").
        string mode = (string?)result["mode"] ?? "?";
        string tgt = threadId > 0 ? $", thread={threadId}" : "";
        if (result["error"] != null)
            Log.Info($"vs_evaluate('{expression}', frame={frameIndex}{tgt}) -> mode={mode}, error={(string?)result["error"]}");
        else
        {
            string val = (string?)result["value"] ?? "";
            if (val.Length > 160) val = val.Substring(0, 160) + "…";
            Log.Info($"vs_evaluate('{expression}', frame={frameIndex}{tgt}) -> valid={(bool?)result["isValid"]}, value={val}");
        }
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}

/// <summary>vs_expand - drill into an object's child members (Expression.DataMembers) to a depth.</summary>
internal sealed class VsExpandTool : IIdeTool
{
    public string Name => "vs_expand";
    public string Description =>
        "Expand an object's structure while paused: evaluate an expression and recurse into its child "
        + "members (fields/properties/elements) to a depth, returning a tree of {name,type,value,children}. "
        + "Use this to inspect a complex object without guessing every member path (e.g. expand 'order' "
        + "instead of evaluating 'order.Customer.Address.City' blind). Break-mode only. Pass threadId (from "
        + "vs_threads) to expand on ANOTHER thread - e.g. drill 'from' on each thread in a deadlock.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["expression"] = new JObject { ["type"] = "string", ["description"] = "Expression to expand (e.g. 'order', 'this', 'items[0]')." },
            ["depth"] = new JObject { ["type"] = "integer", ["description"] = "Levels of children to expand, 1-3 (default 2)." },
            ["frameIndex"] = new JObject { ["type"] = "integer", ["description"] = "Frame to evaluate in, 0 = current (default 0)." },
            ["threadId"] = new JObject { ["type"] = "integer", ["description"] = "Thread id (from vs_threads) to expand on; omit or 0 = current. Use to drill into another thread's state." },
        },
        ["required"] = new JArray("expression"),
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string expression = (string?)args["expression"] ?? "";
        int depth = (int?)args["depth"] ?? 2;
        int frameIndex = (int?)args["frameIndex"] ?? 0;
        int threadId = (int?)args["threadId"] ?? 0;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var result = DebuggerReader.Expand(expression, frameIndex, depth, threadId);
        int kids = (result["children"] as JArray)?.Count ?? 0;
        string tgt = threadId > 0 ? $", thread={threadId}" : "";
        Log.Info($"vs_expand('{expression}', depth={depth}{tgt}) -> {(string?)result["mode"]}, {kids} child(ren)");
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}

/// <summary>vs_threads - all threads + their stacks (deadlock/race investigation).</summary>
internal sealed class VsThreadsTool : IIdeTool
{
    public string Name => "vs_threads";
    public string Description =>
        "List ALL threads of the debugged process while paused, each with its call stack (function names), "
        + "suspended state, and location. Use for deadlocks and races. Returns {mode, threads:[...]}. "
        + "(EnvDTE gives per-thread stacks but not lock/wait-chain ownership.) Break-mode only.";

    public JToken Schema => new JObject { ["type"] = "object", ["properties"] = new JObject() };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var result = DebuggerReader.ReadThreads();
        int n = (result["threads"] as JArray)?.Count ?? 0;
        Log.Info($"vs_threads -> mode={(string?)result["mode"]}, {n} thread(s)");
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}

/// <summary>vs_exception - inspect the exception in scope ($exception) at a first-chance break or in a catch.</summary>
internal sealed class VsExceptionTool : IIdeTool
{
    public string Name => "vs_exception";
    public string Description =>
        "Inspect the exception currently in scope while paused ($exception) - at a first-chance break "
        + "(after vs_break_on_thrown) or inside a catch block. Returns its type, message, and an expanded "
        + "tree including InnerException and stack, so you see WHAT was thrown and WHY without knowing the "
        + "$exception pseudo-variable. Break-mode only.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["frameIndex"] = new JObject { ["type"] = "integer", ["description"] = "Frame to evaluate in, 0 = current (default 0)." },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        int frameIndex = (int?)args["frameIndex"] ?? 0;
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var result = DebuggerReader.ReadException(frameIndex);
        Log.Info($"vs_exception -> mode={(string?)result["mode"]}, type={(string?)result["type"] ?? "(none)"}");
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}

/// <summary>vs_list_processes - local processes available to attach to (the door to debugging real apps).</summary>
internal sealed class VsListProcessesTool : IIdeTool
{
    public string Name => "vs_list_processes";
    public string Description =>
        "List running local processes you can attach the debugger to (id + name, flagged if already being "
        + "debugged). Pass a name filter (e.g. 'dotnet', 'w3wp', or the app name) - the machine has hundreds "
        + "of processes. Use this to find a running web app / service / desktop app, then vs_attach to it. "
        + "Works in any mode.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["filter"] = new JObject { ["type"] = "string", ["description"] = "Case-insensitive substring to match in the process name/path (recommended)." },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string? filter = (string?)args["filter"];
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var result = DebuggerReader.ReadProcesses(filter);
        Log.Info($"vs_list_processes(filter={filter ?? "*"}) -> {(int?)result["count"] ?? 0} match(es)");
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}

/// <summary>
/// vs_wait_chains - structured deadlock triage via a ClrMD process snapshot (not EnvDTE/stack-text).
/// Reads a fork of the debuggee, so it coexists with VS owning the debug port. Runs the snapshot off the
/// UI thread (it's slow and not a VS API); only the PID lookup is on the UI thread.
/// </summary>
internal sealed class VsWaitChainsTool : IIdeTool
{
    public string Name => "vs_wait_chains";
    public string Description =>
        "Deadlock triage via a ClrMD process SNAPSHOT (structured, not stack text): lists every HELD monitor "
        + "with its owner thread + waiter count, every thread with the locks it holds and whether it's blocked "
        + "(Monitor.Enter / Wait / Join), and 'deadlockSuspects' = threads that hold a lock AND are blocked "
        + "entering a monitor (the cycle members). Needs an active debug session (a Break All first gives the "
        + "cleanest read); it reads a fork, so it coexists with VS. Managed (.NET) only. For the explicit "
        + "'waiting on lock owned by thread X' edge, also see vs_threads. Optional pid for multi-process "
        + "sessions (default: the first debuggee).";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["pid"] = new JObject { ["type"] = "integer", ["description"] = "Process id to snapshot; omit to use the first debugged process." },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var (mode, pids) = DebuggerReader.DebugTarget();
        if (pids.Count == 0)
        {
            Log.Info($"vs_wait_chains -> mode={mode}, no debuggee");
            Ui.BridgeStatus.RecordDebugInspect();
            return new JObject { ["mode"] = mode, ["note"] = "No debugged process. Start or attach a debug session first (a Break All gives the cleanest snapshot)." };
        }
        int pid = (int?)args["pid"] ?? pids[0];

        // Snapshot + CLR read OFF the UI thread (slow, and not a VS API) so devenv's UI never blocks.
        JObject result = await Task.Run(() => ClrMdReader.ReadWaitChains(pid), ct);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        result["mode"] = mode;
        result["pid"] = pid;
        if (result["error"] != null)
            Log.Info($"vs_wait_chains(pid={pid}) -> error={(string?)result["error"]}");
        else
        {
            int held = (result["heldLocks"] as JArray)?.Count ?? 0;
            int susp = (result["deadlockSuspects"] as JArray)?.Count ?? 0;
            Log.Info($"vs_wait_chains(pid={pid}) -> {held} held lock(s), {susp} suspect(s)");
        }
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}

/// <summary>
/// vs_async_stacks - reconstruct LOGICAL async call stacks via a ClrMD snapshot. A paused async
/// continuation's physical stack is just MoveNext/ThreadPool; this rebuilds the logical chain
/// (RunAsync -> ComputeAsync -> InnerAsync) from the heap's async state-machine boxes. Out-of-process,
/// off the UI thread - same worker as vs_wait_chains.
/// </summary>
internal sealed class VsAsyncStacksTool : IIdeTool
{
    public string Name => "vs_async_stacks";
    public string Description =>
        "Reconstruct LOGICAL async call stacks from a ClrMD snapshot - the chain the physical stack hides. "
        + "When paused on an async continuation the call stack is just MoveNext/ThreadPool frames; this walks "
        + "the heap's async state-machine boxes and returns each in-flight async method chain (innermost first) "
        + "with its await-point state, so you can see who awaits whom across awaits (e.g. a stuck async "
        + "pipeline). Needs an active debug session; reads a fork, so it coexists with VS. Managed (.NET) only. "
        + "Optional pid for multi-process sessions (default: the first debuggee).";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["pid"] = new JObject { ["type"] = "integer", ["description"] = "Process id to snapshot; omit to use the first debugged process." },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var (mode, pids) = DebuggerReader.DebugTarget();
        if (pids.Count == 0)
        {
            Log.Info($"vs_async_stacks -> mode={mode}, no debuggee");
            Ui.BridgeStatus.RecordDebugInspect();
            return new JObject { ["mode"] = mode, ["note"] = "No debugged process. Start or attach a debug session first." };
        }
        int pid = (int?)args["pid"] ?? pids[0];

        JObject result = await Task.Run(() => ClrMdReader.ReadAsyncStacks(pid), ct);

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        result["mode"] = mode;
        result["pid"] = pid;
        if (result["error"] != null)
            Log.Info($"vs_async_stacks(pid={pid}) -> error={(string?)result["error"]}");
        else
            Log.Info($"vs_async_stacks(pid={pid}) -> {(int?)result["count"] ?? 0} async stack(s)");
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}
