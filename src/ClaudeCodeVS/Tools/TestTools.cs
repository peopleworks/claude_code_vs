using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.CodeModel;
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
