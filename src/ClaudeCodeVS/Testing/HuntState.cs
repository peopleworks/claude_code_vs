using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Testing;

/// <summary>
/// Thread-safe state for a BACKGROUND flaky-hunt. vs_hunt_flaky runs the hunt on a background task (the async
/// start+poll pattern) so a long hunt can never block past the MCP shim's ~60s HTTP timeout - deferring the
/// reply wouldn't help (unlike openDiff, the vs-debug tools aren't on the persistent WebSocket). This holds
/// the live progress; vs_hunt_result reads <see cref="Snapshot"/>. The loop lives in TestRunner.RunHuntLoopAsync.
/// </summary>
internal sealed class HuntState
{
    public string Id { get; }
    public string Test { get; }
    public int MaxRuns { get; }
    public bool MeasureRate { get; }
    public CancellationTokenSource Cts { get; } = new();
    public Task Runner { get; set; } = Task.CompletedTask;

    private readonly object _lock = new();
    private int _executed, _passes, _inconclusive, _attempts;
    private int? _firstFail;
    private readonly JArray _failures = new();
    private bool _done, _canceled, _capHit;
    private string? _verdict, _error;

    public HuntState(string id, string test, int maxRuns, bool measureRate)
    { Id = id; Test = test; MaxRuns = maxRuns; MeasureRate = measureRate; }

    public int Executed { get { lock (_lock) return _executed; } }
    public int Attempts { get { lock (_lock) return _attempts; } }
    public bool IsDone { get { lock (_lock) return _done; } }

    public void RecordAttempt() { lock (_lock) _attempts++; }
    public void RecordInconclusive() { lock (_lock) _inconclusive++; }

    /// <summary>Record one REAL execution (empty/cancelled runs don't call this). fails = the failing tests this run.</summary>
    public void RecordRun(IReadOnlyList<(JToken? outcome, JToken? msg, JToken? stack)> fails)
    {
        lock (_lock)
        {
            _executed++;
            if (fails.Count > 0)
            {
                _firstFail ??= _executed;
                foreach (var (o, m, s) in fails)
                    _failures.Add(new JObject { ["run"] = _executed, ["outcome"] = o, ["errorMessage"] = m, ["errorStackTrace"] = s });
            }
            else _passes++;
        }
    }

    public void Finish(bool capHit)
    {
        lock (_lock)
        {
            if (_done) return;
            _capHit = capHit;
            bool underSampled = MeasureRate && _executed < MaxRuns && capHit;
            _verdict =
                _executed == 0 ? $"INCONCLUSIVE: no run actually executed ({_inconclusive} cancelled/empty) - the engine kept cancelling."
                : _firstFail == null ? $"stable: passed {_executed}/{_executed} executed run(s)" + (underSampled ? $" - UNDER-SAMPLED (wanted {MaxRuns})" : "")
                : _firstFail == 1 && !MeasureRate ? "consistently failing on the first run - not flaky, just broken"
                : $"FLAKY: first failure on run {_firstFail} of {_executed} executed" + (MeasureRate ? $"; failed {_failures.Count}/{_executed}" : "");
            _done = true;
        }
    }

    public void Fail(string error) { lock (_lock) { if (_done) return; _error = error; _done = true; } }
    public void MarkCanceled() { lock (_lock) { if (_done) return; _canceled = true; _done = true; } }

    public JObject Snapshot()
    {
        lock (_lock)
        {
            var o = new JObject
            {
                ["huntId"] = Id,
                ["test"] = Test,
                ["status"] = _done ? (_error != null ? "error" : _canceled ? "canceled" : "done") : "running",
                ["done"] = _done,
                ["executed"] = _executed,
                ["passes"] = _passes,
                ["inconclusiveRuns"] = _inconclusive,
                ["attempts"] = _attempts,
                ["reproduced"] = _firstFail != null,
                ["firstFailedRun"] = _firstFail,
                ["failures"] = (JArray)_failures.DeepClone(),
            };
            if (_capHit) o["attemptCapHit"] = true;
            if (_canceled) o["canceled"] = true;
            if (_error != null) o["error"] = _error;
            if (_verdict != null) o["verdict"] = _verdict;
            if (_done && _firstFail != null)
                o["note"] = "Reproduced. Catch it red-handed: arm vs_break_on_thrown, then vs_debug_test on this test.";
            return o;
        }
    }
}
