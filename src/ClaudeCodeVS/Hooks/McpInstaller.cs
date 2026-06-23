using System;
using System.IO;
using System.Linq;
using ClaudeCodeVs.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Hooks;

/// <summary>
/// Registers the Phase 2 debug PULL channel as a project MCP server. Writes the embedded stdio shim into
/// the workspace's .claude/ folder and upserts an entry into the workspace .mcp.json so the CLI launches
/// it as an MCP server. The shim then discovers the live VS bridge and proxies JSON-RPC to POST /mcp,
/// where the real vs_* debug tools run (in-proc, EnvDTE-backed). Called from Launch alongside the hook
/// installer. Best-effort; idempotent; never throws into the launch path.
///
/// Note: a project-scoped .mcp.json makes the CLI prompt once to trust the server. That's expected.
/// </summary>
internal static class McpInstaller
{
    private const string ShimScript = "vs-mcp-shim.ps1";
    private const string ServerName = "vs-debug";

    public static void EnsureInstalled(string workspaceRoot)
    {
        try
        {
            var claudeDir = Path.Combine(workspaceRoot, ".claude");
            Directory.CreateDirectory(claudeDir);

            // 1) (Over)write the shim from the embedded copy, so updates ship with the extension.
            File.WriteAllText(Path.Combine(claudeDir, ShimScript), ReadEmbeddedScript(ShimScript));

            // 2) Upsert the server entry into <workspace>/.mcp.json, preserving any other servers. The
            //    relative -File path resolves against the CLI's cwd (the workspace root), matching where
            //    the shim was written. We always (re)write OUR entry so command/args updates ship, but
            //    leave the rest of the file untouched.
            var mcpPath = Path.Combine(workspaceRoot, ".mcp.json");
            JObject root;
            if (File.Exists(mcpPath))
            {
                try { root = JObject.Parse(File.ReadAllText(mcpPath)); }
                catch (Exception e)
                {
                    Log.Warn($"mcp: couldn't parse {mcpPath}; leaving it alone ({e.Message})");
                    return;
                }
            }
            else
            {
                root = new JObject();
            }

            var servers = root["mcpServers"] as JObject ?? new JObject();
            root["mcpServers"] = servers;

            var desired = new JObject
            {
                ["type"] = "stdio",
                ["command"] = "powershell",
                ["args"] = new JArray("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $".claude/{ShimScript}"),
            };

            bool changed = !JToken.DeepEquals(servers[ServerName], desired);
            if (!changed)
            {
                Log.Info($"mcp: '{ServerName}' already registered in {mcpPath}; nothing to change");
                return;
            }

            servers[ServerName] = desired;
            File.WriteAllText(mcpPath, root.ToString(Formatting.Indented));
            Log.Info($"mcp: registered '{ServerName}' MCP server in {mcpPath} (debug pull channel)");
        }
        catch (Exception e)
        {
            Log.Warn($"mcp install failed: {e.Message}");
        }
    }

    private static string ReadEmbeddedScript(string scriptFileName)
    {
        var asm = typeof(McpInstaller).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(scriptFileName, StringComparison.OrdinalIgnoreCase));
        if (name == null)
            throw new InvalidOperationException($"embedded shim script not found: {scriptFileName}");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
