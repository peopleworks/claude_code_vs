using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Debugging;

/// <summary>
/// Bridges to the out-of-process ClrMD worker (ClrMdWorker.exe, shipped in the extension's ClrMdWorker\
/// subfolder). ClrMD can't load reliably in-proc in devenv - its System.Collections.Immutable bind
/// collides with devenv's own binding policy (MissingMethodException on DataTarget.get_ClrVersions). A
/// separate process has its own .exe.config binding, so it works (proven by the standalone spike). We
/// launch it with the PID and parse the JSON it writes to stdout. Callers run this OFF the UI thread
/// (Task.Run): it spawns a process and waits.
/// </summary>
internal static class ClrMdReader
{
    private const int TimeoutMs = 30000;

    public static JObject ReadWaitChains(int pid) => RunWorker("waitchains", pid);

    public static JObject ReadAsyncStacks(int pid) => RunWorker("asyncstacks", pid);

    private static JObject RunWorker(string command, int pid)
    {
        string dir = Path.GetDirectoryName(typeof(ClrMdReader).Assembly.Location) ?? "";
        string exe = Path.Combine(dir, "ClrMdWorker", "ClrMdWorker.exe");
        if (!File.Exists(exe))
            return new JObject { ["error"] = $"ClrMD worker not found at {exe}" };

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{command} {pid}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return new JObject { ["error"] = "failed to start ClrMD worker" };

            // Read both streams async, then bound the wait - so neither a full pipe buffer nor a hung
            // snapshot can block us forever.
            Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> errTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(TimeoutMs))
            {
                try { proc.Kill(); } catch { }
                return new JObject { ["error"] = $"ClrMD worker timed out ({TimeoutMs / 1000}s)" };
            }
            string stdout = SafeResult(outTask);
            string stderr = SafeResult(errTask);

            if (string.IsNullOrWhiteSpace(stdout))
                return new JObject { ["error"] = $"ClrMD worker produced no output (exit {proc.ExitCode})" + (string.IsNullOrWhiteSpace(stderr) ? "" : $": {stderr.Trim()}") };
            try { return JObject.Parse(stdout); }
            catch (Exception pe) { return new JObject { ["error"] = $"unparseable worker output: {pe.Message}", ["raw"] = stdout.Length > 400 ? stdout.Substring(0, 400) : stdout }; }
        }
        catch (Exception e)
        {
            return new JObject { ["error"] = $"{e.GetType().Name}: {e.Message}" };
        }
    }

    // RunWorker only runs on a background thread (the tool invokes it via Task.Run), so there is no UI
    // SynchronizationContext to deadlock on - the VSTHRD002 sync-wait warning is a false positive here.
#pragma warning disable VSTHRD002
    private static string SafeResult(Task<string> t) { try { return t.GetAwaiter().GetResult(); } catch { return ""; } }
#pragma warning restore VSTHRD002
}
