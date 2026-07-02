using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.CodeModel;
using ClaudeCodeVs.Debugging;
using ClaudeCodeVs.Protocol;
using ClaudeCodeVs.Testing;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// SPIKE (spike_test_runner) - the fix-verify loop tools, backed by <see cref="TestRunner"/> on VS's own
/// Test Explorer engine. vs_list_tests (discover) / vs_run_test (run + coverage/profile) / vs_debug_test
/// (launch one under the debugger). Read-only discovery; run/debug drive the engine. All self-load Test
/// Explorer, so no manual step. Delete/rename into a real vs-test server once proven.
/// </summary>
internal sealed class VsListTestsTool : IIdeTool
{
    private readonly TestRunner _runner;
    public VsListTestsTool(TestRunner runner) => _runner = runner;

    public string Name => "vs_list_tests";
    public string Description =>
        "Discover the unit tests in the solution open in Visual Studio, via Roslyn's semantic model (finds "
        + "methods marked [Fact]/[Theory]/[Test]/[TestMethod]/[TestCase] - ground truth, not grep). Returns "
        + "[{fullyQualifiedName, displayName, className, project, source}] - feed a fullyQualifiedName to "
        + "vs_run_test / vs_debug_test. Optional filter = an FQN substring. Managed (C#/VB) test projects.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["filter"] = new JObject { ["type"] = "string", ["description"] = "Optional fully-qualified-name substring to narrow discovery (e.g. a class or method name)." },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string? filter = (string?)args["filter"];
        var result = await RoslynReader.FindTestMethodsAsync(filter, ct); // discovery via the semantic model, not the engine callback
        Log.Info($"vs_list_tests(filter={filter ?? "*"}) -> {(int?)result["count"] ?? 0} test(s){((bool?)result["available"] == false ? " (no C#/VB solution)" : "")}");
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}

/// <summary>vs_run_test - run a single test (or all) and return outcomes + failure detail, with optional coverage/profiling.</summary>
internal sealed class VsRunTestTool : IIdeTool
{
    private readonly TestRunner _runner;
    public VsRunTestTool(TestRunner runner) => _runner = runner;

    public string Name => "vs_run_test";
    public string Description =>
        "Run tests through Visual Studio's Test Explorer engine and get structured results: per test "
        + "{outcome, errorMessage, errorStackTrace, durationMs}, plus overall success/status. Pass test = a "
        + "fully-qualified name to run just that one (great for a test you just added/changed); omit to run "
        + "all. collectCoverage=true attaches a code-coverage file; profile=true runs under the profiler. "
        + "Loads Test Explorer itself. Managed (.NET) test projects.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["test"] = new JObject { ["type"] = "string", ["description"] = "Fully-qualified test name to run (e.g. 'TestLab.ScoreTests.Add_TwoPositives_Sums'). Omit to run all tests." },
            ["collectCoverage"] = new JObject { ["type"] = "boolean", ["description"] = "Collect code coverage; the .coverage file path is returned under response.attachments (default false)." },
            ["profile"] = new JObject { ["type"] = "boolean", ["description"] = "Run under the profiler (TestHostMode.Profile) instead of a plain run (default false)." },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string? test = (string?)args["test"];
        bool coverage = (bool?)args["collectCoverage"] ?? false;
        bool profile = (bool?)args["profile"] ?? false;
        var result = await _runner.RunAsync(test, coverage, profile, ct);
        string status = (string?)result["response"]?["Status"] ?? ((bool?)result["ok"] == true ? "?" : "error");
        Log.Info($"vs_run_test(test={test ?? "*"}, cov={coverage}, prof={profile}) -> {status}, {(int?)result["testCount"] ?? 0} result(s){((bool?)result["ok"] == false ? $", error={(string?)result["error"]}" : "")}");
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}

/// <summary>vs_debug_test - launch a single test under the VS debugger (pair with vs_break_on_thrown to stop at the fault).</summary>
internal sealed class VsDebugTestTool : IIdeTool
{
    private readonly TestRunner _runner;
    public VsDebugTestTool(TestRunner runner) => _runner = runner;

    public string Name => "vs_debug_test";
    public string Description =>
        "Launch ONE test under the Visual Studio debugger (by fully-qualified name), so you can catch a "
        + "failing test red-handed. Pair it with vs_break_on_thrown FIRST to stop at the throw site, then "
        + "vs_debug_state / vs_get_frame_locals to read the live frame. Loads Test Explorer itself. Managed "
        + "(.NET) test projects.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["test"] = new JObject { ["type"] = "string", ["description"] = "Fully-qualified name of the test to debug (e.g. 'TestLab.ScoreTests.ScoreRounds_FlatBonusPerRound')." },
        },
        ["required"] = new JArray("test"),
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string test = (string?)args["test"] ?? "";
        var result = await _runner.DebugAsync(test, ct);
        Log.Info($"vs_debug_test(test={test}) -> ok={(bool?)result["ok"]}{((bool?)result["ok"] == false ? $", error={(string?)result["error"]}" : "")}");
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}

/// <summary>vs_hunt_flaky - force-reproduce an intermittent/transient failure by hammering a test.</summary>
internal sealed class VsHuntFlakyTool : IIdeTool
{
    private readonly TestRunner _runner;
    public VsHuntFlakyTool(TestRunner runner) => _runner = runner;

    public string Name => "vs_hunt_flaky";
    public string Description =>
        "Hunt an intermittent/flaky/transient test failure: run a test repeatedly until it fails (or maxRuns), "
        + "capturing the REAL outcome + errorMessage + stack of each failing run - the force-reproduce lever for "
        + "heisenbugs. Runs in the BACKGROUND: returns the final verdict if it finishes within ~40s, else returns "
        + "{huntId, status:'running'} to poll with vs_hunt_result (or stop with vs_hunt_cancel). measureRate=true "
        + "runs all maxRuns to estimate the failure rate. When it reproduces, arm vs_break_on_thrown then "
        + "vs_debug_test to catch it red-handed. Managed (.NET) test projects.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["test"] = new JObject { ["type"] = "string", ["description"] = "Fully-qualified test name to hammer (from vs_list_tests)." },
            ["maxRuns"] = new JObject { ["type"] = "integer", ["description"] = "Maximum repetitions (default 25)." },
            ["measureRate"] = new JObject { ["type"] = "boolean", ["description"] = "Run all maxRuns to measure the failure RATE (default false = stop at the first failure)." },
        },
        ["required"] = new JArray("test"),
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string test = (string?)args["test"] ?? "";
        int maxRuns = (int?)args["maxRuns"] ?? 25;
        bool measureRate = (bool?)args["measureRate"] ?? false;
        var st = _runner.StartHunt(test, maxRuns, measureRate);
        // Hybrid: wait up to 40s inline (fast hunts return the full verdict in one call, under the ~60s shim
        // timeout); a longer hunt keeps running in the background and hands back a huntId to poll.
        await Task.WhenAny(st.Runner, Task.Delay(40000, ct));
        var snap = st.Snapshot();
        if ((bool?)snap["done"] != true)
            snap["note"] = $"Still running in the background - poll vs_hunt_result with huntId '{st.Id}' for the verdict (or vs_hunt_cancel to stop). Returned early so the call can't time out.";
        Log.Info($"vs_hunt_flaky(test={test}, maxRuns={maxRuns}) -> {((bool?)snap["done"] == true ? (string?)snap["verdict"] : $"running ({st.Id})")}");
        Ui.BridgeStatus.RecordDebugInspect();
        return snap;
    }
}

/// <summary>vs_hunt_result - poll a background flaky-hunt started by vs_hunt_flaky.</summary>
internal sealed class VsHuntResultTool : IIdeTool
{
    private readonly TestRunner _runner;
    public VsHuntResultTool(TestRunner runner) => _runner = runner;

    public string Name => "vs_hunt_result";
    public string Description =>
        "Poll a background flaky-hunt started by vs_hunt_flaky. Pass its huntId; returns the current progress "
        + "{status, executed, reproduced, firstFailedRun, failures, verdict}. status 'running' = keep polling; "
        + "'done'/'error'/'canceled' = terminal (verdict is set when done).";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject { ["huntId"] = new JObject { ["type"] = "string", ["description"] = "The huntId returned by vs_hunt_flaky." } },
        ["required"] = new JArray("huntId"),
    };

    public Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        string id = (string?)args["huntId"] ?? "";
        var st = _runner.GetHunt(id);
        Ui.BridgeStatus.RecordDebugInspect();
        return Task.FromResult<object>(st?.Snapshot() ?? new JObject { ["error"] = "unknown or expired huntId: " + id });
    }
}

/// <summary>vs_hunt_cancel - stop a background flaky-hunt.</summary>
internal sealed class VsHuntCancelTool : IIdeTool
{
    private readonly TestRunner _runner;
    public VsHuntCancelTool(TestRunner runner) => _runner = runner;

    public string Name => "vs_hunt_cancel";
    public string Description => "Cancel a background flaky-hunt started by vs_hunt_flaky (pass its huntId).";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject { ["huntId"] = new JObject { ["type"] = "string", ["description"] = "The huntId to cancel." } },
        ["required"] = new JArray("huntId"),
    };

    public Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        Ui.BridgeStatus.RecordDebugInspect();
        return Task.FromResult<object>(_runner.CancelHunt((string?)args["huntId"] ?? ""));
    }
}

/// <summary>
/// vs_catch_flaky - the "catch red-handed" half: loop a test UNDER the debugger with break-on-thrown armed
/// until the failing iteration halts at the throw site with the live frame + $exception. Gated (it drives).
/// </summary>
internal sealed class VsCatchFlakyTool : IIdeTool
{
    private readonly TestRunner _runner;
    private readonly DebuggerDriver _driver;
    public VsCatchFlakyTool(TestRunner runner, DebuggerDriver driver) { _runner = runner; _driver = driver; }

    public string Name => "vs_catch_flaky";
    public string Description =>
        "Catch a flaky/transient failure RED-HANDED: repeatedly run a test UNDER the debugger with "
        + "break-on-thrown armed, so the failing iteration halts at the throw site with the live frame + "
        + "$exception readable. If 'exception' isn't given, a quick pre-hunt reproduces the failure and learns "
        + "the exception type (works when it throws a named exception; for a bare assertion pass exception, "
        + "e.g. Xunit.Sdk.XunitException). On success it leaves the debugger PAUSED at the fault - inspect with "
        + "vs_debug_state / vs_get_frame_locals / vs_exception. Requires the 'Allow Claude to drive debugger' "
        + "toggle. Managed (.NET) test projects.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["test"] = new JObject { ["type"] = "string", ["description"] = "Fully-qualified test name to catch (from vs_list_tests)." },
            ["maxRuns"] = new JObject { ["type"] = "integer", ["description"] = "Max debug runs to attempt (default 15). Raise for rarer flakes (mind the ~60s call budget)." },
            ["exception"] = new JObject { ["type"] = "string", ["description"] = "Exception type to break on (e.g. 'System.InvalidOperationException'). Omit to auto-learn it from a quick pre-hunt." },
        },
        ["required"] = new JArray("test"),
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        if (!Ui.BridgeStatus.AllowDebuggerDrive)
            return new JObject { ["error"] = "debugger driving is disabled", ["hint"] = "Enable 'Allow Claude to drive debugger' in the Claude Code panel, then retry (this launches + breaks under the debugger)." };
        Ui.BridgeStatus.RecordDebugDrive();

        string test = (string?)args["test"] ?? "";
        int maxRuns = (int?)args["maxRuns"] ?? 15;
        string? given = (string?)args["exception"];

        // 1) Determine the exception type(s) to break on. A quick pre-hunt learns a THROWN type; for a bare
        //    assertion (no type in the message) we arm the framework assertion base types so it's still one call.
        JObject? preHunt = null;
        List<string> exceptions;
        if (!string.IsNullOrEmpty(given))
        {
            exceptions = new List<string> { given! };
            await _runner.EnsureBuiltAsync(ct);
        }
        else
        {
            preHunt = await _runner.QuickReproAsync(test, 8, ct);
            if ((bool?)preHunt["reproduced"] != true)
                return Ret(test, new JObject { ["caught"] = false, ["preHunt"] = preHunt, ["note"] = "Couldn't reproduce in a quick pre-hunt to learn the exception type. Confirm flakiness with vs_hunt_flaky, or pass `exception` explicitly." });
            var learned = (string?)preHunt["exceptionType"];
            exceptions = !string.IsNullOrEmpty(learned) ? new List<string> { learned! } : new List<string>(AssertBaseTypes);
        }

        // 2) Arm break-on-thrown for each candidate (a name not present in the debuggee just never fires) + acquire.
        foreach (var ex in exceptions) await _driver.SetBreakOnThrownAsync(ex, true, ct);
        if (!await _runner.EnsureAcquiredAsync(ct))
        {
            foreach (var ex in exceptions) await _driver.SetBreakOnThrownAsync(ex, false, ct);
            return Ret(test, new JObject { ["caught"] = false, ["error"] = "couldn't acquire the test engine" });
        }

        // 3) Loop debug runs until one breaks at the fault (bounded by maxRuns + a ~40s budget).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int run = 0;
        while (run < maxRuns && sw.ElapsedMilliseconds < 40000)
        {
            ct.ThrowIfCancellationRequested();
            run++;
            var snap = await _driver.LaunchAndAwaitBreakAsync(() => _runner.StartDebugRun(test, ct), 60000, ct);
            if ((string?)snap["mode"] == "break")
            {
                string? caught = (string?)(snap["locals"] as JArray)?.FirstOrDefault(l => (string?)l["name"] == "$exception")?["type"];
                return Ret(test, new JObject
                {
                    ["caught"] = true,
                    ["onRun"] = run,
                    ["exception"] = caught,
                    ["armedFor"] = JArray.FromObject(exceptions),
                    ["break"] = snap,
                    ["preHunt"] = preHunt,
                    ["note"] = "PAUSED at the fault under the debugger. Inspect: vs_debug_state / vs_get_frame_locals / vs_exception. Then vs_continue or vs_stop_debugging. (break-on-thrown is still armed - clear it with vs_break_on_thrown enabled:false.)",
                });
            }
            await Task.Delay(300, ct).ConfigureAwait(true);
        }

        // 4) Not caught: disarm each + stop any session so nothing is left half-armed.
        foreach (var ex in exceptions) await _driver.SetBreakOnThrownAsync(ex, false, ct);
        await _driver.StopDebuggingAsync(ct);
        return Ret(test, new JObject
        {
            ["caught"] = false,
            ["debugRuns"] = run,
            ["armedFor"] = JArray.FromObject(exceptions),
            ["preHunt"] = preHunt,
            ["note"] = "Not caught within the run/time budget. The flake may be rarer than the runs allowed, or the exception type doesn't match. Check the rate with vs_hunt_flaky, then raise maxRuns.",
        });
    }

    // Assertion base types per framework - armed when a flaky ASSERT gives no exception type to learn. Break-on-
    // thrown matches subclasses (e.g. Xunit.Sdk.TrueException derives from XunitException), so the base catches all.
    private static readonly string[] AssertBaseTypes =
    {
        "Xunit.Sdk.XunitException",
        "NUnit.Framework.AssertionException",
        "Microsoft.VisualStudio.TestTools.UnitTesting.AssertFailedException",
    };

    private object Ret(string test, JObject o)
    {
        Log.Info($"vs_catch_flaky(test={test}) -> caught={(bool?)o["caught"]}{((bool?)o["caught"] == true ? $" on run {o["onRun"]}" : "")}");
        return o;
    }
}

/// <summary>vs_rerun_failed - re-run only the tests that failed in the last run (Scope.ForState(Failed)).</summary>
internal sealed class VsRerunFailedTool : IIdeTool
{
    private readonly TestRunner _runner;
    public VsRerunFailedTool(TestRunner runner) => _runner = runner;

    public string Name => "vs_rerun_failed";
    public string Description =>
        "Re-run ONLY the tests that failed in the last run - the classic fix-verify move (fix, then re-run just "
        + "the failures instead of the whole suite). Returns the same per-test {outcome, errorMessage, stack} as "
        + "vs_run_test. If the last run had no remembered failures it runs nothing. Optional collectCoverage. "
        + "Managed (.NET) test projects.";

    public JToken Schema => new JObject
    {
        ["type"] = "object",
        ["properties"] = new JObject
        {
            ["collectCoverage"] = new JObject { ["type"] = "boolean", ["description"] = "Collect code coverage for the re-run (default false)." },
        },
    };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        bool coverage = (bool?)args["collectCoverage"] ?? false;
        var result = await _runner.RunAsync(null, coverage, false, ct, build: true, failedOnly: true);
        Log.Info($"vs_rerun_failed -> {(int?)result["testCount"] ?? 0} test(s), passed={(bool?)result["passed"]}");
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}
