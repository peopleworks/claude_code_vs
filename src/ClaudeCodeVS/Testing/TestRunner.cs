using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Testing;

/// <summary>
/// SPIKE (branch spike_test_runner) - drives VS's Test Explorer engine for run / debug, on LAYER 1 (the
/// path vs_test_probe proved + recommended): the in-proc MEF-acquired <c>OperationBroker</c>, cast to
/// <c>IRequestFactory</c>. (An earlier cut went through the BROKERED ITestWindowService and failed - its
/// StreamJsonRpc/MessagePack WIRE contract had drifted from the interface metadata: RunTestsAsync missing,
/// GetTestsAsync wanted a FilteredTestsRequest DTO. Lesson in vs-test-api-map.)
///
/// Run/debug need NO result callback: <c>ExecuteTestsByFilterAsync</c> / <c>DebugTestsByFilterAsync</c>
/// take a <c>List&lt;SearchQuery&gt;</c> and return a <c>Task&lt;bool&gt;</c> that completes when the run
/// finishes - so we just await it. Coverage rides on <c>RunTestsAsync(..., null callback)</c> reading the
/// return's Attachments. What still needs the INTERNAL <c>ITestWindowDataCallback</c> (deferred to a
/// Reflection.Emit + [IgnoresAccessChecksTo] follow-up): per-test failure MESSAGES and discovery LISTING.
///
/// All Test Explorer types are internal, so loaded + invoked via reflection (only IMPLEMENTING an internal
/// interface is blocked; instantiating internal classes + calling internal methods is fine). The VS
/// plumbing (IComponentModel) comes from the SDK.
/// </summary>
internal sealed class TestRunner
{
    private const string AsmController = "Microsoft.VisualStudio.TestWindow.dll";
    private const string AsmInternal = "Microsoft.VisualStudio.TestWindow.Internal.dll";
    private const string AsmInterfaces = "Microsoft.VisualStudio.TestWindow.Interfaces.dll";

    private object? _broker;      // OperationBroker (implements IRequestFactory + IOperationState)
    private Type? _reqFactory;    // IRequestFactory type

    private readonly Dictionary<string, HuntState> _hunts = new(); // background flaky-hunts, by id
    private readonly object _huntsLock = new();

    /// <summary>Discovery is callback-gated on Layer 1 - report that honestly rather than fake it.</summary>
    public Task<JObject> ListAsync(string? fqnFilter, CancellationToken ct) => Task.FromResult(new JObject
    {
        ["available"] = false,
        ["note"] = "Listing needs Test Explorer's internal result callback (ITestWindowDataCallback), which "
                 + "requires a Reflection.Emit shim - deferred. Meanwhile: run/debug by fully-qualified name, "
                 + "or find test methods via vs_search_symbols / grep for [Fact]/[Theory]/[Test]/[TestMethod].",
    });

    /// <summary>Run tests by fully-qualified name (or all). Awaits the engine's own completion task.</summary>
    public async Task<JObject> RunAsync(string? fqn, bool collectCoverage, bool profile, CancellationToken ct, bool build = true, bool failedOnly = false)
    {
        var (broker, type, err) = await AcquireAsync(ct);
        if (broker == null) return new JObject { ["ok"] = false, ["error"] = err };
        await EnsureLoadedAsync(ct);
        var report = new JObject { ["target"] = failedOnly ? "(failed)" : fqn ?? "(all)" };
        if (build) report["build"] = await BuildSolutionAsync(ct); // self-sufficient: no manual Ctrl+Shift+B needed

        // Wire Test Explorer's INTERNAL result callback (emitted, since it's an internal interface) so we
        // get REAL per-test outcomes - Success/Status on the response do NOT distinguish pass from fail.
        var sink = new TestResultSink();
        object? callbackOptions = null;
        try
        {
            var cbIface = FindType("Microsoft.VisualStudio.TestWindow.Extensibility.ITestWindowDataCallback", AsmInternal);
            if (cbIface != null) callbackOptions = BuildCallbackOptions(TestCallbackFactory.Create(cbIface, sink));
        }
        catch (Exception e) { report["callbackSetupError"] = Flat(e); }

        try
        {
            object? resp = await RunTestsAsyncReflect(broker, type!, fqn, collectCoverage, profile, callbackOptions, ct, failedOnly);
            report["mode"] = profile ? "Profile" : "Run";
            report["coverageRequested"] = collectCoverage;
            report["response"] = DescribeResponse(resp);

            var tests = sink.Snapshot();
            report["testCount"] = tests.Count;
            report["tests"] = tests;
            report["ok"] = true;
            if (tests.Count > 0)
            {
                report["passed"] = tests.All(t => IsPassed((JObject)t));
                report["failedCount"] = tests.Count(t => !IsPassed((JObject)t));
            }
            else
            {
                report["passed"] = null;
                report["note"] = profile
                    ? "Profile mode returned no per-test results (usually Status=Cancelled): TestHostMode.Profile needs a ProfilerToolId (a Diagnostics Hub tool GUID) that isn't wired yet. Use a plain run or collectCoverage=true. Tracked as a follow-up."
                    : "No per-test results streamed"
                        + (report["callbackSetupError"] != null ? " (callback emit failed - see callbackSetupError)" : "")
                        + " - response.Success only means the run completed, not that tests passed. Use vs_debug_test to see a failure.";
            }
            return report;
        }
        catch (Exception e) { report["ok"] = false; report["error"] = Flat(e); return report; }
    }

    /// <summary>
    /// Start a BACKGROUND flaky-hunt and return its <see cref="HuntState"/> immediately (async start+poll -
    /// a long hunt can't block past the MCP shim timeout). The caller may wait briefly on <c>state.Runner</c>
    /// for the fast case, then poll <see cref="GetHunt"/>.
    /// </summary>
    public HuntState StartHunt(string test, int maxRuns, bool measureRate)
    {
        if (maxRuns < 1) maxRuns = 1;
        string id = "hunt_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var st = new HuntState(id, test, maxRuns, measureRate);
        lock (_huntsLock)
        {
            _hunts[id] = st;
            if (_hunts.Count > 24) // bounded registry: drop the oldest completed hunts
                foreach (var k in _hunts.Where(kv => kv.Value.IsDone).Select(kv => kv.Key).Take(_hunts.Count - 24).ToList())
                    _hunts.Remove(k);
        }
        st.Runner = Task.Run(() => RunHuntLoopAsync(st));
        return st;
    }

    public HuntState? GetHunt(string id) { lock (_huntsLock) return _hunts.TryGetValue(id, out var s) ? s : null; }

    public JObject CancelHunt(string id)
    {
        var st = GetHunt(id);
        if (st == null) return new JObject { ["error"] = "unknown or expired huntId: " + id };
        st.Cts.Cancel();
        return new JObject { ["huntId"] = id, ["canceled"] = true, ["note"] = "Cancellation requested; poll vs_hunt_result for the final state." };
    }

    /// <summary>The hunt loop: run the test repeatedly, updating the shared HuntState. Empty/cancelled runs are
    /// retried (not counted); stops at the first failure unless measureRate. No timeout here - the tool decides
    /// how long to wait inline; the hunt lives past the request.</summary>
    private async Task RunHuntLoopAsync(HuntState st)
    {
        var ct = st.Cts.Token;
        int maxAttempts = st.MaxRuns * 4 + 10; // cap so engine cancellations can't loop forever
        bool builtOnce = false;
        try
        {
            while (st.Executed < st.MaxRuns)
            {
                ct.ThrowIfCancellationRequested();
                if (st.Attempts >= maxAttempts) { st.Finish(capHit: true); return; }
                st.RecordAttempt();
                var r = await RunAsync(st.Test, false, false, ct, build: !builtOnce);
                builtOnce = true;
                if ((bool?)r["ok"] != true) { st.Fail("run failed: " + (string?)r["error"]); return; }

                var tests = (r["tests"] as JArray) ?? new JArray();
                if (tests.Count == 0) { st.RecordInconclusive(); await Task.Delay(700, ct); continue; }

                var fails = tests.Where(t => !IsPassed((JObject)t))
                    .Select(t => ((JToken?)t["outcome"], (JToken?)t["errorMessage"], (JToken?)t["errorStackTrace"]))
                    .ToList();
                st.RecordRun(fails);
                if (fails.Count > 0 && !st.MeasureRate) { st.Finish(capHit: false); return; }
                await Task.Delay(500, ct); // settle so the engine actually runs the next one
            }
            st.Finish(capHit: false);
        }
        catch (OperationCanceledException) { st.MarkCanceled(); }
        catch (Exception e) { st.Fail(Flat(e)); }
    }

    // ---------------- catch-red-handed support (used by vs_catch_flaky) ----------------

    /// <summary>Ensure the in-proc engine is acquired (call before <see cref="StartDebugRun"/>).</summary>
    public async Task<bool> EnsureAcquiredAsync(CancellationToken ct) { var (b, _, _) = await AcquireAsync(ct); return b != null; }

    /// <summary>Open Test Explorer + build the solution (for the specified-exception path that skips the pre-hunt).</summary>
    public async Task EnsureBuiltAsync(CancellationToken ct) { await EnsureLoadedAsync(ct); await BuildSolutionAsync(ct); }

    /// <summary>Start a DEBUG run of one test and return the engine Task WITHOUT awaiting (it completes on run
    /// end, or stays pending if the debugger breaks). Must be on the UI thread + acquired. For the catch loop.</summary>
    public Task StartDebugRun(string fqn, CancellationToken ct)
    {
        var filter = BuildSearchQueryList(fqn);
        var m = _reqFactory!.GetMethod("DebugTestsByFilterAsync");
        return (Task)m!.Invoke(_broker!, new object?[] { filter, ct })!;
    }

    /// <summary>Fast (no-debugger) pre-hunt: run until the test fails, and parse the exception TYPE from the
    /// failure so the catcher knows what to break-on-thrown. Returns {reproduced, exceptionType?, ...}.</summary>
    public async Task<JObject> QuickReproAsync(string fqn, int maxRuns, CancellationToken ct)
    {
        int executed = 0, attempts = 0, maxAttempts = maxRuns * 4 + 10;
        bool builtOnce = false;
        while (executed < maxRuns && attempts < maxAttempts)
        {
            ct.ThrowIfCancellationRequested();
            attempts++;
            var r = await RunAsync(fqn, false, false, ct, build: !builtOnce);
            builtOnce = true;
            if ((bool?)r["ok"] != true) return new JObject { ["reproduced"] = false, ["error"] = "run failed: " + (string?)r["error"] };
            var tests = (r["tests"] as JArray) ?? new JArray();
            if (tests.Count == 0) { await Task.Delay(700, ct).ConfigureAwait(true); continue; }
            executed++;
            var fail = tests.FirstOrDefault(t => !IsPassed((JObject)t)) as JObject;
            if (fail != null)
            {
                return new JObject
                {
                    ["reproduced"] = true,
                    ["onRun"] = executed,
                    ["exceptionType"] = ParseExceptionType((string?)fail["errorMessage"]),
                    ["outcome"] = fail["outcome"],
                    ["errorMessage"] = fail["errorMessage"],
                    ["errorStackTrace"] = fail["errorStackTrace"],
                };
            }
            await Task.Delay(400, ct).ConfigureAwait(true);
        }
        return new JObject { ["reproduced"] = false, ["executed"] = executed, ["note"] = $"no failure in {executed} run(s)" };
    }

    /// <summary>Pull a fully-qualified exception type name out of a failure message ("System.FooException : ...").
    /// Returns null for assertion failures (message has no type), which is why the caller can accept an explicit one.</summary>
    private static string? ParseExceptionType(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(message!, @"^\s*([A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)*(?:Exception|Error))\b");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Launch ONE test under the VS debugger by FQN. Fire (don't fully await - it blocks at a break).</summary>
    public async Task<JObject> DebugAsync(string fqn, CancellationToken ct)
    {
        var (broker, type, err) = await AcquireAsync(ct);
        if (broker == null) return new JObject { ["ok"] = false, ["error"] = err };
        await EnsureLoadedAsync(ct);
        var buildInfo = await BuildSolutionAsync(ct);
        try
        {
            var filter = BuildSearchQueryList(fqn);
            var m = type!.GetMethod("DebugTestsByFilterAsync");
            var task = (Task)m!.Invoke(broker, new object?[] { filter, ct })!;
            // Give the debug launch a moment to spin up, but do NOT await completion: if it breaks at the
            // fault the task won't complete until execution continues.
            await Task.WhenAny(task, Task.Delay(2000, ct)).ConfigureAwait(true);
            return new JObject
            {
                ["ok"] = true,
                ["fqn"] = fqn,
                ["build"] = buildInfo,
                ["launched"] = task.IsFaulted ? "faulted" : "started",
                ["error"] = task.IsFaulted ? Flat(task.Exception!) : null,
                ["note"] = "Under the VS debugger. Arm vs_break_on_thrown FIRST (or before calling this) to stop at the throw, then vs_debug_state / vs_get_frame_locals to read the live frame.",
            };
        }
        catch (Exception e) { return new JObject { ["ok"] = false, ["error"] = Flat(e) }; }
    }

    // ---------------- Layer 1 acquisition (MEF) ----------------

    private async Task<(object? broker, Type? type, string? err)> AcquireAsync(CancellationToken ct)
    {
        if (_broker != null) return (_broker, _reqFactory, null);
        var cm = (IComponentModel?)await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SComponentModel));
        var t = FindType("Microsoft.VisualStudio.TestWindow.Controller.IRequestFactory", AsmController);
        if (cm == null || t == null) return (null, null, $"prereq missing (componentModel={cm != null}, IRequestFactory={t != null})");
        try
        {
            var get = typeof(IComponentModel).GetMethod("GetService")!.MakeGenericMethod(t);
            object? svc = get.Invoke(cm, null);
            if (svc == null) return (null, null, "GetService<IRequestFactory> returned null");
            _broker = svc; _reqFactory = t;
            return (svc, t, null);
        }
        catch (Exception e) { return (null, null, Flat(e)); }
    }

    // ---------------- run variants ----------------

    private async Task<object?> RunTestsAsyncReflect(object broker, Type type, string? fqn, bool coverage, bool profile, object? callbackOptions, CancellationToken ct, bool failedOnly = false)
    {
        // RunTestsAsync(TestHostMode mode, TestFilterOptions filter, TestRunOptions runOptions, TestCallbackOptions callbackOptions, ct)
        object? filter = failedOnly ? BuildStateFilter("Failed") : BuildScopeFilter(fqn);
        object runOpts = BuildRunOptions(coverage);
        var modeType = FindType("Microsoft.VisualStudio.TestWindow.Messages.TestHostMode", AsmInternal)!;
        object mode = Enum.ToObject(modeType, profile ? 2 : 0);
        var m = type.GetMethod("RunTestsAsync");
        return await InvokeAsync(m!, broker, new object?[] { mode, filter, runOpts, callbackOptions, ct });
    }

    /// <summary>TestCallbackOptions(ITestWindowDataCallback) + DataSelector = include per-test results.</summary>
    private object? BuildCallbackOptions(object callback)
    {
        var t = FindType("Microsoft.VisualStudio.TestWindow.Extensibility.TestCallbackOptions", AsmInternal);
        if (t == null) return null;
        object o = t.GetConstructors().First().Invoke(new[] { callback });
        var selType = FindType("Microsoft.VisualStudio.TestWindow.Extensibility.TestSelectorOptions", AsmInternal);
        if (selType != null)
        {
            object? sel = selType.GetProperty("New", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            sel = selType.GetMethod("WithTestResults")?.Invoke(sel, null) ?? sel;
            t.GetProperty("DataSelector")?.SetValue(o, sel);
        }
        return o;
    }

    private static bool IsPassed(JObject t)
    {
        string? outcome = (string?)t["outcome"] ?? (string?)t["state"];
        return string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// List&lt;SearchQuery&gt; matching one FQN (ExactMatch) or all (Contains ""). SearchQuery's accessible
    /// ctor is 5-arg — (string property, string value, ICollection&lt;string&gt; values,
    /// ICollection&lt;SearchQuery&gt; subQueries, FilterMatchKind matchKind) — so we pick ANY ctor that
    /// starts (string, string) and ends with the enum, filling the middle (collection) params with null.
    /// The engine rejects an EMPTY list, so this must always produce one query.
    /// </summary>
    private object BuildSearchQueryList(string? fqn)
    {
        var sqType = FindType("Microsoft.VisualStudio.TestWindow.Messages.SearchQuery", AsmInternal)!;
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sqType))!;

        var ctor = sqType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(c =>
            {
                var ps = c.GetParameters();
                return ps.Length >= 3
                    && ps[0].ParameterType == typeof(string)
                    && ps[1].ParameterType == typeof(string)
                    && ps[ps.Length - 1].ParameterType.IsEnum;
            })
            .OrderBy(c => c.GetParameters().Length) // prefer the simplest matching ctor
            .FirstOrDefault();
        if (ctor == null) return list; // (shouldn't happen) leave empty -> caller surfaces the engine error

        var pars = ctor.GetParameters();
        var fmk = pars[pars.Length - 1].ParameterType;
        bool all = string.IsNullOrEmpty(fqn);
        object match = all ? EnumVal(fmk, new[] { "Contains" }, 0) : EnumVal(fmk, new[] { "ExactMatch", "Exact" }, 2);

        var argv = new object?[pars.Length];       // middle (collection) params stay null
        argv[0] = "FullyQualifiedName";
        argv[1] = all ? "" : fqn;
        argv[pars.Length - 1] = match;
        list.Add(ctor.Invoke(argv));
        return list;
    }

    /// <summary>TestFilterOptions(ICollection&lt;Scope&gt;, int?) with one Scope.ForSymbol(fqn), or null = all.</summary>
    private object? BuildScopeFilter(string? fqn)
    {
        var scopeType = FindType("Microsoft.VisualStudio.TestWindow.Extensibility.Scope", AsmInternal);
        var tfoType = FindType("Microsoft.VisualStudio.TestWindow.Extensibility.TestFilterOptions", AsmInternal);
        if (scopeType == null || tfoType == null) return null;
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(scopeType))!;
        if (!string.IsNullOrEmpty(fqn))
        {
            // Scope.ForSymbol(fqn, projectName, type, method) — fqn-only; others null.
            object? scope = scopeType.GetMethod("ForSymbol", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object?[] { fqn, null, null, null });
            if (scope != null) list.Add(scope);
        }
        // RunTestsAsync rejects a null filter; a NON-null TestFilterOptions with an EMPTY scope list = run all.
        var ctor = tfoType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        return ctor.Invoke(new object?[] { list, null });
    }

    /// <summary>TestFilterOptions([Scope.ForState(TestState.Failed)]) - re-run only the tests in a given
    /// last-run state (Failed). TestState lives in the .Interfaces assembly.</summary>
    private object? BuildStateFilter(string stateName)
    {
        var scopeType = FindType("Microsoft.VisualStudio.TestWindow.Extensibility.Scope", AsmInternal);
        var tfoType = FindType("Microsoft.VisualStudio.TestWindow.Extensibility.TestFilterOptions", AsmInternal);
        var stateType = FindType("Microsoft.VisualStudio.TestWindow.Extensibility.TestState", AsmInterfaces);
        if (scopeType == null || tfoType == null || stateType == null) return null;
        object stateVal = EnumVal(stateType, new[] { stateName }, 1); // TestState.Failed = 1
        object? scope = scopeType.GetMethod("ForState", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new[] { stateVal });
        if (scope == null) return null;
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(scopeType))!;
        list.Add(scope);
        var ctor = tfoType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        return ctor.Invoke(new object?[] { list, null });
    }

    private object BuildRunOptions(bool coverage)
    {
        var t = FindType("Microsoft.VisualStudio.TestWindow.Extensibility.TestRunOptions", AsmInternal)!;
        object o = Activator.CreateInstance(t)!;
        if (coverage) t.GetProperty("CollectCoverage")?.SetValue(o, (bool?)true);
        return o;
    }

    /// <summary>Dump EVERY public property of the response (TestWindowRunResponse) so we read the engine's
    /// ground truth - Success/Status/ElapsedTime/Attachments - instead of guessing which field means what.</summary>
    private static JObject DescribeResponse(object? resp)
    {
        var o = new JObject();
        if (resp == null) { o["(null)"] = true; return o; }
        foreach (var p in resp.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            try { o[p.Name] = Describe(p.GetValue(resp)); } catch { }
        }
        return o;
    }

    /// <summary>Value → JToken: strings/primitives as text; collections as arrays whose complex elements are
    /// dumped one level deep — so a coverage TestRunAttachment surfaces its real Uri/path, not its type name.</summary>
    private static JToken? Describe(object? v)
    {
        if (v == null) return null;
        if (v is string s) return s;
        var t = v.GetType();
        if (t.IsPrimitive || v is decimal || v is Guid || v is Uri || v is DateTime || v is TimeSpan) return v.ToString();
        if (v is IEnumerable en)
        {
            var arr = new JArray();
            foreach (var x in en) arr.Add(DescribeShallow(x));
            return arr;
        }
        return DescribeShallow(v);
    }

    private static JToken DescribeShallow(object? v)
    {
        if (v == null) return JValue.CreateNull();
        if (v is string s) return s;
        var t = v.GetType();
        if (t.IsPrimitive || v is decimal || v is Guid || v is Uri || v is DateTime || v is TimeSpan) return v.ToString();
        var o = new JObject();
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            try { o[p.Name] = p.GetValue(v)?.ToString(); } catch { }
        }
        return o.HasValues ? o : (JToken)(v.ToString() ?? "");
    }

    // ---------------- reflection plumbing + package load ----------------

    private static async Task<object?> InvokeAsync(MethodInfo m, object target, object?[] args)
    {
        object? ret = m.Invoke(target, args);
        if (ret == null) return null;
        var rt = ret.GetType();
        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ret = rt.GetMethod("AsTask")!.Invoke(ret, null);
        if (ret is Task task)
        {
            await task.ConfigureAwait(true);
            var rp = task.GetType().GetProperty("Result");
            return rp != null && rp.PropertyType != typeof(void) ? rp.GetValue(task) : null;
        }
        return ret;
    }

    private static object EnumVal(Type enumType, string[] names, int fallback)
    {
        try
        {
            foreach (var want in names)
                foreach (var n in Enum.GetNames(enumType))
                    if (n.Equals(want, StringComparison.OrdinalIgnoreCase)) return Enum.Parse(enumType, n);
        }
        catch { }
        try { return Enum.ToObject(enumType, fallback); } catch { return fallback; }
    }

    private static string Flat(Exception e) { var x = e.InnerException ?? e; return x.GetType().Name + ": " + x.Message; }

    /// <summary>
    /// Build the solution programmatically so the tools never depend on a manual Ctrl+Shift+B - we drive VS
    /// for everything else, so we drive the build too. Synchronous EnvDTE build (it pumps internally);
    /// LastBuildInfo = number of projects that failed (0 = success). Best-effort.
    /// </summary>
    private static async Task<JObject> BuildSolutionAsync(CancellationToken ct)
    {
        var info = new JObject();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        try
        {
            var sb = (ServiceProvider.GlobalProvider.GetService(typeof(Microsoft.VisualStudio.Shell.Interop.SDTE)) as EnvDTE.DTE)?.Solution?.SolutionBuild;
            if (sb == null) { info["built"] = false; info["note"] = "no solution loaded"; return info; }
            sb.Build(true); // build + wait
            info["built"] = true;
            info["projectsFailed"] = sb.LastBuildInfo; // 0 = success
        }
        catch (Exception e) { info["built"] = false; info["error"] = Flat(e); }
        return info;
    }

    private bool _ensuredLoaded;
    public async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_ensuredLoaded) return; // idempotent - the package stays loaded; don't re-open + re-delay per run
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var dte = ServiceProvider.GlobalProvider.GetService(typeof(Microsoft.VisualStudio.Shell.Interop.SDTE)) as EnvDTE.DTE;
        foreach (var c in new[] { "TestExplorer.ShowTestExplorer", "Test.ShowTestExplorer", "View.TestExplorer" })
        {
            try { dte?.ExecuteCommand(c); break; } catch { }
        }
        await Task.Delay(800, ct).ConfigureAwait(true);
        _ensuredLoaded = true;
    }

    private static readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);

    private static Type? FindType(string fullName, string dll)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { var t = a.GetType(fullName, false); if (t != null) return t; } catch { }
        }
        try
        {
            string? ide = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName);
            if (ide == null) return null;
            string path = Path.Combine(ide, "CommonExtensions", "Microsoft", "TestWindow", dll);
            if (!File.Exists(path)) return null;
            if (!_loaded.TryGetValue(path, out var asm)) { asm = Assembly.LoadFrom(path); _loaded[path] = asm; }
            return asm.GetType(fullName, false);
        }
        catch { return null; }
    }
}
