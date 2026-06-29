using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Debugging;

/// <summary>
/// Extension half of the managed data-breakpoint feature - drives the Concord component
/// (ClaudeCodeVS.DataBpComponent) over a tiny file-IPC channel under %TEMP%\claude-codevs-databp\.
///
///   request.txt  (we WRITE): line1 = requestId, line2 = expression (owner.field)
///   status.txt   (component): "&lt;requestId&gt; armed" | "&lt;requestId&gt; error: ..."
///   events.jsonl (component appends): one JSON line per change {requestId, expression, change}
///
/// Why file-IPC and not DkmCustomMessage: the component arms on its OWN request thread; we only need
/// to hand it an expression and read back changes - a file channel is enough and keeps the extension
/// out of the Concord graph. We poke a stack walk (via the driver) so the component arms promptly, and
/// for stop-on-change we call EnvDTE Break when a change matches the condition (the component itself
/// can't halt from its hit notification - the event-thread restriction proven in the spike).
/// </summary>
internal sealed class DataBreakpointBridge : IDisposable
{
    private static readonly string IpcDir = Path.Combine(Path.GetTempPath(), "claude-codevs-databp");
    private static string RequestsDir => Path.Combine(IpcDir, "requests");
    private static string RemovesDir => Path.Combine(IpcDir, "removes");
    private static string StatusDir => Path.Combine(IpcDir, "status");
    private static string EventsFile => Path.Combine(IpcDir, "events.jsonl");
    private static string RequestFileFor(string id) => Path.Combine(RequestsDir, id + ".txt");
    private static string StatusFileFor(string id) => Path.Combine(StatusDir, id + ".txt");

    private readonly DebuggerDriver _driver;
    private readonly ConcurrentDictionary<string, Watch> _watches = new();
    private readonly CancellationTokenSource _cts = new();
    private long _eventsPos;
    private int _tailing;

    public DataBreakpointBridge(DebuggerDriver driver) => _driver = driver;

    private sealed class Watch
    {
        public string Expression;
        public string Condition;
        public bool StopOnChange;
        public bool Broke;                         // broke at least once (honest: a real Break was issued)
        public int BreakCount;                     // how many matching changes halted execution
        public readonly List<JObject> Changes = new();   // structured: {previous, current, type}
    }

    /// <summary>Arm a watch on an instance-field expression; returns {requestId, status} or {error}.</summary>
    public async Task<JObject> SetWatchAsync(string expression, string condition, bool stopOnChange, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return Err("expression is required (use owner.field, e.g. order.Total)");
        if (expression.LastIndexOf('.') <= 0)
            return Err("expression must be owner.field - a managed data breakpoint watches an INSTANCE FIELD of a heap object (statics, locals and struct fields are unsupported)");

        EnsureTailing();
        string id = Guid.NewGuid().ToString("N").Substring(0, 8);
        _watches[id] = new Watch { Expression = expression, Condition = condition, StopOnChange = stopOnChange };

        try { Directory.CreateDirectory(RequestsDir); File.WriteAllText(RequestFileFor(id), expression, Encoding.UTF8); }
        catch (Exception e) { _watches.TryRemove(id, out _); return Err("couldn't write the watch request: " + e.Message); }

        // The component arms on a request-thread stack walk - poke one so it picks up this request now.
        if (!await _driver.PokeStackWalkAsync(ct))
        {
            _watches.TryRemove(id, out _);
            try { File.Delete(RequestFileFor(id)); } catch { }
            return Err("not paused at a breakpoint - set a data breakpoint while stopped where the owning object is in scope");
        }

        for (int i = 0; i < 50 && !ct.IsCancellationRequested; i++)
        {
            string st = TryRead(StatusFileFor(id));   // component writes "armed" | "error: ..."
            if (st == "armed")
                return new JObject
                {
                    ["requestId"] = id,
                    ["status"] = "armed",
                    ["expression"] = expression,
                    ["condition"] = condition,
                    ["stopOnChange"] = stopOnChange,
                    ["note"] = "watching. Poll vs_get_data_changes with this requestId for the change timeline"
                               + (stopOnChange ? "; execution breaks on EACH change matching the condition." : ".")
                               + " Disarm with vs_remove_data_breakpoint."
                };
            if (st != null && st.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            {
                _watches.TryRemove(id, out _);
                return Err("arm failed: " + st);
            }
            await Task.Delay(100, ct);
            if (i == 5) await _driver.PokeStackWalkAsync(ct);   // nudge again if the first walk raced the file write
        }
        _watches.TryRemove(id, out _);
        return Err("timed out arming the watch (is the owner object in scope at the current stop?)");
    }

    /// <summary>Disarm a watch - write the remove marker, drop our tracking, and (best-effort) poke a
    /// walk so the component Closes the engine binding now.</summary>
    public async Task<JObject> RemoveWatchAsync(string requestId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId) || !_watches.ContainsKey(requestId))
            return Err("unknown requestId - nothing to remove");

        try { Directory.CreateDirectory(RemovesDir); File.WriteAllText(Path.Combine(RemovesDir, requestId), "", Encoding.UTF8); }
        catch (Exception e) { return Err("couldn't write the remove request: " + e.Message); }

        _watches.TryRemove(requestId, out _);          // stop routing changes / breaking immediately
        bool poked = await _driver.PokeStackWalkAsync(ct);
        return new JObject
        {
            ["requestId"] = requestId,
            ["status"] = "removed",
            ["note"] = poked
                ? "disarmed; engine binding closed."
                : "stopped tracking; the engine binding closes at the next stop (or when the debuggee exits)."
        };
    }

    /// <summary>Return the accumulated change timeline for a watch.</summary>
    public JObject GetChanges(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || !_watches.TryGetValue(requestId, out var w))
            return Err("unknown requestId - arm one with vs_set_data_breakpoint first");
        lock (w.Changes)
            return new JObject
            {
                ["requestId"] = requestId,
                ["expression"] = w.Expression,
                ["count"] = w.Changes.Count,
                ["stopOnChange"] = w.StopOnChange,
                ["broke"] = w.Broke,           // honest: a real Break was issued on a matching change
                ["breakCount"] = w.BreakCount,
                ["changes"] = new JArray(w.Changes)   // structured {previous,current,type}, oldest first
            };
    }

    private void EnsureTailing()
    {
        if (Interlocked.CompareExchange(ref _tailing, 1, 0) != 0) return;
        _ = Task.Run(() => TailLoopAsync(_cts.Token));
    }

    // Tail events.jsonl incrementally; route each change to its watch and break on a matching change.
    private async Task TailLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(EventsFile))
                {
                    using var fs = new FileStream(EventsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length < _eventsPos) _eventsPos = 0;   // file was reset
                    fs.Seek(_eventsPos, SeekOrigin.Begin);
                    using var sr = new StreamReader(fs);
                    string line;
                    while ((line = sr.ReadLine()) != null)
                        if (line.Length > 0) await HandleLineAsync(line, ct);
                    _eventsPos = fs.Position;
                }
            }
            catch { /* transient IO - retry next tick */ }

            try { await Task.Delay(200, ct); } catch { return; }
        }
    }

    private async Task HandleLineAsync(string line, CancellationToken ct)
    {
        JObject ev;
        try { ev = JObject.Parse(line); } catch { return; }
        string id = (string)ev["requestId"];
        if (id == null || !_watches.TryGetValue(id, out var w)) return;

        // Turn the component's "**Previous Value:** X ... **Current Value:** Y (type)" message into
        // structured {previous, current, type} (numbers when numeric) - far easier to reason over than
        // re-parsing markdown. Drop the per-entry requestId/expression (already at the top level).
        string change = (string)ev["change"];
        string prev = ExtractValue(change, "Previous Value");
        string cur = ExtractValue(change, "Current Value");
        var entry = new JObject { ["previous"] = ToToken(prev), ["current"] = ToToken(cur), ["type"] = ExtractType(change) };
        lock (w.Changes) w.Changes.Add(entry);

        // Break on EVERY matching change (a data breakpoint halts each time the condition holds, not
        // just once). Each change line is processed exactly once, and a new write can only occur after
        // the debuggee resumes, so this naturally re-breaks on each subsequent match.
        if (w.StopOnChange && ConditionMatches(w.Condition, cur))
        {
            // RAW break (no drive gate): a model vs_continue in flight holds the gate while parked on the
            // next break, so a gated break would be rejected. This trips the debuggee and the parked
            // continue catches it and returns at the mutation.
            if (await _driver.RequestBreakAsync(ct)) { w.Broke = true; w.BreakCount++; }
        }
    }

    private static string ExtractValue(string change, string label)
    {
        if (change == null) return null;
        var m = Regex.Match(change, Regex.Escape(label) + @":\**\s*([^\s(]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string ExtractType(string change)
    {
        if (change == null) return null;
        var m = Regex.Match(change, @"Current Value:[^(\r\n]*\(([^)]+)\)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static JToken ToToken(string v)
    {
        if (v == null) return JValue.CreateNull();
        if (long.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out long l)) return new JValue(l);
        if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out double d)) return new JValue(d);
        return new JValue(v);
    }

    // condition: "&lt;op&gt; &lt;value&gt;" with op in &gt; &gt;= &lt; &lt;= == != (numeric, or ==/!= as string).
    // Empty/null => every change matches.
    internal static bool ConditionMatches(string condition, string newVal)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        var m = Regex.Match(condition.Trim(), @"^(>=|<=|==|!=|>|<)\s*(.+)$");
        if (!m.Success || newVal == null) return true;
        string op = m.Groups[1].Value, rhs = m.Groups[2].Value.Trim();
        if (double.TryParse(newVal, NumberStyles.Any, CultureInfo.InvariantCulture, out double nv) &&
            double.TryParse(rhs, NumberStyles.Any, CultureInfo.InvariantCulture, out double rv))
        {
            switch (op)
            {
                case ">": return nv > rv;
                case ">=": return nv >= rv;
                case "<": return nv < rv;
                case "<=": return nv <= rv;
                case "==": return nv == rv;
                case "!=": return nv != rv;
            }
        }
        if (op == "==") return string.Equals(newVal, rhs, StringComparison.Ordinal);
        if (op == "!=") return !string.Equals(newVal, rhs, StringComparison.Ordinal);
        return false;
    }

    private static string TryRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; } catch { return null; }
    }

    private static JObject Err(string msg) => new JObject { ["error"] = msg };

    public void Dispose() { try { _cts.Cancel(); } catch { } }
}
