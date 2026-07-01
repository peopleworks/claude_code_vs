using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Microsoft.ServiceHub.Framework;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.ServiceBroker;
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.Tools;

/// <summary>
/// SPIKE (branch spike_test_runner) - one-shot acquisition probe for the planned vs-test server.
///
/// Headless reflection of VS's Test Explorer assemblies found the controller has NO MEF [Export], so
/// whether we can get a LIVE test engine in-process is a runtime question this tool answers. It tries,
/// in order, every acquisition path and reports what worked:
///   Layer 1 (in-proc): MEF IOperationState / IRequestFactory (run + debug + the run-finished await on
///                      one object).
///   Layer 2 (brokered): ITestWindowService (GetTestsAsync/RunTestsAsync) + ITestWindowDebugLaunch
///                      (StartDebuggingTestAsync) via IServiceBroker + the TestWindow descriptors.
/// Then it does a READ-ONLY discovery (GetTestsAsync) and a capability check (TestHostMode.Profile +
/// TestRunOptions.CollectCoverage => coverage/profiling are the same call). It NEVER runs or debugs a
/// test. Delete this file before shipping vs-test.
///
/// Reflection (not direct refs) because the Test Explorer types are internal; the VS-plumbing types
/// (IComponentModel / IServiceBroker) come from the SDK meta-package and are used directly.
/// </summary>
internal sealed class VsTestProbeTool : IIdeTool
{
    public string Name => "vs_test_probe";

    public string Description =>
        "SPIKE: probe whether Visual Studio's Test Explorer engine can be acquired in-process (for the "
        + "planned test-runner integration). Opens Test Explorer itself (loads the engine), then tries MEF "
        + "(IOperationState/IRequestFactory) and the brokered ITestWindowService/ITestWindowDebugLaunch, "
        + "runs a READ-ONLY test discovery, and reports Test Explorer capabilities. Never runs or debugs a "
        + "test. Just open a C# solution that has a test project first (e.g. demo/TestLab). Returns JSON.";

    public JToken Schema => new JObject { ["type"] = "object", ["properties"] = new JObject() };

    public async Task<object> InvokeAsync(JToken args, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        JObject result;
        try { result = await TestRunnerProbe.RunAsync(ct); }
        catch (Exception e) { result = new JObject { ["ok"] = false, ["fatal"] = e.ToString() }; }
        Log.Info($"vs_test_probe -> ok={(bool?)result["ok"]}  ({(string?)result["recommendation"]})");
        Ui.BridgeStatus.RecordDebugInspect();
        return result;
    }
}

/// <summary>The reflection guts of the acquisition probe. All best-effort: every path is caught and
/// reported as JSON rather than thrown, so a single run surfaces the full feasibility picture.</summary>
internal static class TestRunnerProbe
{
    // Fully-qualified type names + their home assembly (dll under the TestWindow dir).
    private const string AsmInterfaces = "Microsoft.VisualStudio.TestWindow.Interfaces.dll";
    private const string AsmController = "Microsoft.VisualStudio.TestWindow.dll";
    private const string AsmInternal = "Microsoft.VisualStudio.TestWindow.Internal.dll";
    private const string AsmCopilot = "Microsoft.VisualStudio.TestWindow.Copilot.Internal.dll";

    public static async Task<JObject> RunAsync(CancellationToken ct)
    {
        var report = new JObject();
        string? dir = TestWindowDir();
        report["testWindowDir"] = dir ?? "(not found)";
        report["arch"] = RuntimeInformation.ProcessArchitecture.ToString();

        // Resolve the key contract types (from already-loaded assemblies, else LoadFrom the install dir).
        var tOpState = FindType("Microsoft.VisualStudio.TestWindow.Extensibility.IOperationState", AsmInterfaces);
        var tReqFactory = FindType("Microsoft.VisualStudio.TestWindow.Controller.IRequestFactory", AsmController);
        var tSvc = FindType("Microsoft.VisualStudio.TestWindow.Extensibility.ITestWindowService", AsmInternal);
        var tDebugLaunch = FindType("Microsoft.VisualStudio.TestWindow.Copilot.Internal.BrokeredServices.ITestWindowDebugLaunch", AsmCopilot);
        report["typesResolved"] = new JObject
        {
            ["IOperationState"] = tOpState?.FullName ?? "(missing)",
            ["IRequestFactory"] = tReqFactory?.FullName ?? "(missing)",
            ["ITestWindowService"] = tSvc?.FullName ?? "(missing)",
            ["ITestWindowDebugLaunch"] = tDebugLaunch?.FullName ?? "(missing)",
        };

        // Self-driving (like the debugger tools drive VS): open Test Explorer via a command, which loads
        // the Test Window package so its brokered services are proffered + discovery starts - no manual
        // step. Then give it a beat to initialize before we acquire (the brokered GetProxyAsync also
        // retries, below).
        report["packageLoad"] = await EnsureTestWindowLoadedAsync(ct);
        await Task.Delay(1500, ct).ConfigureAwait(true);

        var strategies = new JObject();
        report["strategies"] = strategies;

        // ---- Layer 1: MEF ----
        IComponentModel? cm = null;
        try { cm = (IComponentModel?)await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SComponentModel)); }
        catch (Exception e) { strategies["_componentModel"] = "error: " + e.Message; }
        if (cm != null)
        {
            if (tOpState != null) strategies["mef_IOperationState"] = TryMef(cm, tOpState, tReqFactory);
            if (tReqFactory != null) strategies["mef_IRequestFactory"] = TryMef(cm, tReqFactory, null);
        }

        // ---- Layer 2: brokered ----
        IServiceBroker? broker = null;
        try
        {
            var container = (IBrokeredServiceContainer?)await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(SVsBrokeredServiceContainer));
            broker = container?.GetFullAccessServiceBroker();
        }
        catch (Exception e) { strategies["_serviceBroker"] = "error: " + e.Message; }

        if (broker != null)
        {
            bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            var storeHolder = FindType("Microsoft.VisualStudio.TestWindow.Communication.Interfaces.TestWindowStoreServiceDescriptors", AsmInternal);
            var storeDesc = GetStatic(storeHolder, arm ? "TestWindowStoreServiceArm64Descriptor" : "TestWindowStoreServiceX64Descriptor");
            strategies["brokered_ITestWindowService"] = await TryBrokeredWithDiscoveryAsync(broker, storeDesc, tSvc, ct);

            var dlHolder = FindType("Microsoft.VisualStudio.TestWindow.Copilot.Internal.BrokeredServices.TestWindowDescriptors", AsmCopilot);
            var dlDesc = GetStatic(dlHolder, "TestWindowDebugLaunchDescriptor");
            strategies["brokered_ITestWindowDebugLaunch"] = await TryBrokeredAsync(broker, dlDesc, tDebugLaunch, ct);
        }

        // ---- capabilities: are coverage + profiling the same RunTestsAsync call? ----
        report["capabilities"] = Capabilities();

        report["ok"] = AcquiredAny(strategies);
        report["recommendation"] = Recommend(strategies);
        return report;
    }

    // ---------- acquisition strategies ----------

    private static JObject TryMef(IComponentModel cm, Type contract, Type? alsoCastableTo)
    {
        var node = new JObject { ["contract"] = contract.FullName };
        try
        {
            var get = typeof(IComponentModel).GetMethod("GetService")!.MakeGenericMethod(contract);
            object? svc = get.Invoke(cm, null);
            node["acquired"] = svc != null;
            if (svc != null)
            {
                node["type"] = svc.GetType().FullName;
                if (alsoCastableTo != null) node["alsoIsRequestFactory"] = alsoCastableTo.IsInstanceOfType(svc);
            }
        }
        catch (Exception e)
        {
            var x = e.InnerException ?? e;
            node["acquired"] = false;
            node["error"] = x.GetType().Name + ": " + x.Message; // e.g. ImportCardinalityMismatchException => not exported
        }
        return node;
    }

    /// <summary>Acquire a brokered proxy by descriptor + interface type (no follow-up call).</summary>
    private static async Task<JObject> TryBrokeredAsync(IServiceBroker broker, object? descriptor, Type? ifaceType, CancellationToken ct)
    {
        var node = new JObject();
        if (descriptor == null || ifaceType == null)
        {
            node["acquired"] = false;
            node["error"] = $"missing prerequisite (descriptor={descriptor != null}, iface={ifaceType != null})";
            return node;
        }
        node["descriptor"] = DescribeDescriptor(descriptor);
        var (proxy, err) = await GetProxyAsync(broker, descriptor, ifaceType, ct);
        node["acquired"] = proxy != null;
        if (err != null) node["error"] = err;
        if (proxy != null) node["type"] = proxy.GetType().FullName;
        (proxy as IDisposable)?.Dispose();
        return node;
    }

    /// <summary>Acquire ITestWindowService, then attempt a READ-ONLY GetTestsAsync discovery.</summary>
    private static async Task<JObject> TryBrokeredWithDiscoveryAsync(IServiceBroker broker, object? descriptor, Type? ifaceType, CancellationToken ct)
    {
        var node = new JObject();
        if (descriptor == null || ifaceType == null)
        {
            node["acquired"] = false;
            node["error"] = $"missing prerequisite (descriptor={descriptor != null}, iface={ifaceType != null})";
            return node;
        }
        node["descriptor"] = DescribeDescriptor(descriptor);
        var (proxy, err) = await GetProxyAsync(broker, descriptor, ifaceType, ct);
        node["acquired"] = proxy != null;
        if (err != null) node["error"] = err;
        if (proxy == null)
        {
            node["hint"] = "Null after the auto-open + retry: the service may not be proffered to this audience, or discovery hasn't run. Try building the solution / re-running once Test Explorer has listed tests.";
            return node;
        }
        node["type"] = proxy.GetType().FullName;
        try { node["discovery"] = await TryDiscoveryAsync(proxy, ifaceType, ct); }
        catch (Exception e) { node["discovery"] = new JObject { ["error"] = (e.InnerException ?? e).Message }; }
        (proxy as IDisposable)?.Dispose();
        return node;
    }

    private static async Task<(object? proxy, string? err)> GetProxyAsync(IServiceBroker broker, object descriptor, Type ifaceType, CancellationToken ct)
    {
        if (descriptor is not ServiceRpcDescriptor desc) return (null, "descriptor is not a ServiceRpcDescriptor: " + descriptor.GetType().FullName);
        var gp = typeof(IServiceBroker).GetMethod("GetProxyAsync")!.MakeGenericMethod(ifaceType);
        string? lastErr = null;
        // Retry: the Test Window package proffers asynchronously after the open-Test-Explorer command, so
        // the first request can race the proffer. ~3s of polling covers the init window.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                object vt = gp.Invoke(broker, new object?[] { desc, default(ServiceActivationOptions), ct })!;
                var task = (Task)vt.GetType().GetMethod("AsTask")!.Invoke(vt, null)!;
                await task.ConfigureAwait(true);
                object? proxy = task.GetType().GetProperty("Result")!.GetValue(task);
                if (proxy != null) return (proxy, null);
                lastErr = "GetProxyAsync returned null (service not proffered yet, or not visible to this audience)";
            }
            catch (Exception e) { lastErr = (e.InnerException ?? e).Message; }
            await Task.Delay(500, ct).ConfigureAwait(true);
        }
        return (null, lastErr);
    }

    /// <summary>
    /// Force the Test Window package to load (so its brokered services proffer + discovery starts) by
    /// opening Test Explorer through a command - the same DTE.ExecuteCommand drive the debugger tools use.
    /// Best-effort: tries a few command spellings and reports which took.
    /// </summary>
    private static async Task<JObject> EnsureTestWindowLoadedAsync(CancellationToken ct)
    {
        var node = new JObject();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var dte = ServiceProvider.GlobalProvider.GetService(typeof(Microsoft.VisualStudio.Shell.Interop.SDTE)) as EnvDTE.DTE;
        if (dte == null) { node["error"] = "no DTE"; return node; }
        string[] cmds = { "TestExplorer.ShowTestExplorer", "Test.ShowTestExplorer", "View.TestExplorer" };
        var errors = new JObject();
        string? worked = null;
        foreach (var c in cmds)
        {
            try { dte.ExecuteCommand(c); worked = c; break; }
            catch (Exception e) { errors[c] = (e.InnerException ?? e).Message; }
        }
        node["openedVia"] = worked ?? "(no command worked)";
        if (worked == null) node["commandErrors"] = errors;
        return node;
    }

    private static async Task<JObject> TryDiscoveryAsync(object svc, Type svcType, CancellationToken ct)
    {
        var node = new JObject();
        object? filter = BuildAllTestsFilter(node);
        var getTests = svcType.GetMethod("GetTestsAsync");
        if (getTests == null) { node["error"] = "GetTestsAsync not found on the proxy interface"; return node; }

        var task = (Task)getTests.Invoke(svc, new object?[] { filter, ct })!;
        await task.ConfigureAwait(true);
        object? res = task.GetType().GetProperty("Result")?.GetValue(task);
        node["resultType"] = res?.GetType().FullName;

        int count = 0;
        var sample = new JArray();
        if (res is IEnumerable en)
        {
            foreach (var item in en)
            {
                count++;
                if (item != null && sample.Count < 10) sample.Add(DescribeTest(item));
            }
        }
        node["count"] = count;
        node["sample"] = sample;
        return node;
    }

    /// <summary>TestFilterOptions with null scopes = "all tests"; report any ctor trouble.</summary>
    private static object? BuildAllTestsFilter(JObject node)
    {
        var t = FindType("Microsoft.VisualStudio.TestWindow.Extensibility.TestFilterOptions", AsmInternal);
        if (t == null) { node["filter"] = "TestFilterOptions type missing (passing null)"; return null; }
        try
        {
            var ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            return ctor.Invoke(new object?[ctor.GetParameters().Length]); // all null/default => all tests
        }
        catch (Exception e) { node["filterCtorError"] = (e.InnerException ?? e).Message; return null; }
    }

    // ---------- capability + summary ----------

    private static JObject Capabilities()
    {
        var caps = new JObject();
        var tMode = FindType("Microsoft.VisualStudio.TestWindow.Messages.TestHostMode", AsmInternal);
        if (tMode is { IsEnum: true }) caps["TestHostMode"] = new JArray(Enum.GetNames(tMode));
        var tRunOpts = FindType("Microsoft.VisualStudio.TestWindow.Extensibility.TestRunOptions", AsmInternal);
        if (tRunOpts != null)
        {
            caps["hasCollectCoverage"] = tRunOpts.GetProperty("CollectCoverage") != null; // coverage = a flag on the same call
            caps["hasProfilerToolId"] = tRunOpts.GetProperty("ProfilerToolId") != null;   // profiling = a flag + TestHostMode.Profile
            caps["hasUseHotReload"] = tRunOpts.GetProperty("UseHotReload") != null;
        }
        return caps;
    }

    private static bool AcquiredAny(JObject strategies) =>
        strategies.Properties().Any(p => p.Value is JObject o && (bool?)o["acquired"] == true);

    private static string Recommend(JObject s)
    {
        bool Ok(string k) => (s[k] as JObject)?["acquired"]?.Value<bool>() == true;
        if (Ok("mef_IRequestFactory")) return "BEST: Layer 1 via MEF IRequestFactory - run + debug + run-finished await on one in-proc object.";
        if (Ok("mef_IOperationState")) return "Layer 1 partial: MEF IOperationState acquired (the await signal). Check alsoIsRequestFactory for run/debug on the same object.";
        if (Ok("brokered_ITestWindowService") || Ok("brokered_ITestWindowDebugLaunch")) return "Layer 2 (brokered) works: build vs-test on ITestWindowService (discover/run) + ITestWindowDebugLaunch (debug one).";
        return "Neither layer acquired in-proc. If Test Explorer wasn't open, retry after opening it; else pivot to shell-out (dotnet test/vstest + VSTEST_HOST_DEBUG=1 + the existing vs_attach + vs_break_on_thrown).";
    }

    // ---------- reflection plumbing ----------

    private static readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);

    private static string? TestWindowDir()
    {
        try
        {
            string? ide = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName); // ...\Common7\IDE
            if (ide == null) return null;
            string dir = Path.Combine(ide, "CommonExtensions", "Microsoft", "TestWindow");
            return Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    private static Type? FindType(string fullName, string dll)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { var t = a.GetType(fullName, false); if (t != null) return t; } catch { }
        }
        string? dir = TestWindowDir();
        if (dir == null) return null;
        string path = Path.Combine(dir, dll);
        if (!File.Exists(path)) return null;
        try
        {
            if (!_loaded.TryGetValue(path, out var asm)) { asm = Assembly.LoadFrom(path); _loaded[path] = asm; }
            return asm.GetType(fullName, false);
        }
        catch { return null; }
    }

    private static object? GetStatic(Type? holder, string name)
    {
        if (holder == null) return null;
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        try
        {
            var f = holder.GetField(name, F);
            if (f != null) return f.GetValue(null);
            return holder.GetProperty(name, F)?.GetValue(null);
        }
        catch { return null; }
    }

    private static string DescribeDescriptor(object desc) =>
        desc is ServiceRpcDescriptor d ? d.Moniker.ToString() : desc.GetType().Name;

    private static JToken DescribeTest(object t)
    {
        var type = t.GetType();
        foreach (var p in new[] { "FullyQualifiedName", "DisplayName", "Name" })
        {
            try { if (type.GetProperty(p)?.GetValue(t) is string v && !string.IsNullOrEmpty(v)) return v; } catch { }
        }
        return t.ToString() ?? type.Name;
    }
}
