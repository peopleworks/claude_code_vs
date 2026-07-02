using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

// The runtime honors this attribute (by full name) on a dynamic assembly to skip visibility checks against
// the named assembly - the standard way to implement an INTERNAL interface (ITestWindowDataCallback) from
// emitted code. Defined here because .NET Framework's BCL doesn't ship it.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName) => AssemblyName = assemblyName;
        public string AssemblyName { get; }
    }
}

namespace ClaudeCodeVs.Testing
{
    /// <summary>
    /// Managed sink the emitted callback forwards to. Reads each streamed TestNodeData reflectively (its
    /// type is internal) into plain JSON: the real per-test outcome + error message + stack + duration.
    /// </summary>
    public sealed class TestResultSink
    {
        private readonly List<JObject> _tests = new();
        private readonly object _lock = new();

        /// <summary>Called from the emitted OnTestDataAsync with the IReadOnlyCollection&lt;TestNodeData&gt;.</summary>
        public void Receive(object? tests)
        {
            if (tests is not IEnumerable en) return;
            lock (_lock)
                foreach (var t in en)
                    if (t != null) _tests.Add(ReadNode(t));
        }

        public JArray Snapshot() { lock (_lock) return new JArray(_tests.Cast<object>().ToArray()); }
        public int Count { get { lock (_lock) return _tests.Count; } }

        private static JObject ReadNode(object t)
        {
            var tt = t.GetType();
            var o = new JObject
            {
                ["fullyQualifiedName"] = Str(tt, t, "FullyQualifiedName"),
                ["displayName"] = Str(tt, t, "DisplayName"),
                ["state"] = tt.GetProperty("State")?.GetValue(t)?.ToString(),
            };
            // Results is IReadOnlyCollection<TestNodeResult> - take the first result's detail.
            if (tt.GetProperty("Results")?.GetValue(t) is IEnumerable results)
            {
                foreach (var r in results)
                {
                    if (r == null) continue;
                    var rt = r.GetType();
                    o["outcome"] = rt.GetProperty("Outcome")?.GetValue(r)?.ToString();
                    o["errorMessage"] = Str(rt, r, "ErrorMessage");
                    o["errorStackTrace"] = Str(rt, r, "ErrorStackTrace");
                    try { o["durationMs"] = Convert.ToInt64(rt.GetProperty("DurationInMs")?.GetValue(r) ?? 0L); } catch { }
                    break;
                }
            }
            return o;
        }

        private static string? Str(Type t, object o, string prop) { try { return t.GetProperty(prop)?.GetValue(o) as string; } catch { return null; } }
    }

    /// <summary>
    /// Emits a concrete type implementing the internal <c>ITestWindowDataCallback</c> whose OnTestDataAsync
    /// forwards the streamed nodes to a <see cref="TestResultSink"/>. This is the one thing reflection can't
    /// do by instantiation - you can't IMPLEMENT an internal interface without emitting a type + granting the
    /// dynamic assembly access via [IgnoresAccessChecksTo].
    /// </summary>
    internal static class TestCallbackFactory
    {
        private static readonly Dictionary<string, Type> _cache = new(StringComparer.Ordinal);

        /// <summary>Create a callback instance for the given ITestWindowDataCallback type, wired to <paramref name="sink"/>.</summary>
        public static object Create(Type callbackInterface, TestResultSink sink)
        {
            var type = GetOrBuild(callbackInterface);
            object inst = Activator.CreateInstance(type)!;
            type.GetField("Sink")!.SetValue(inst, sink);
            return inst;
        }

        private static Type GetOrBuild(Type iface)
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(iface.AssemblyQualifiedName!, out var cached)) return cached;
                var built = Build(iface);
                _cache[iface.AssemblyQualifiedName!] = built;
                return built;
            }
        }

        private static Type Build(Type iface)
        {
            var asmName = new AssemblyName("ClaudeCodeVs.TestCallback.Dynamic");
            var ab = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.RunAndCollect);

            // Grant the dynamic assembly access to the internals of the interface's (and node types') assembly.
            var iaca = typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute);
            var iacaCtor = iaca.GetConstructor(new[] { typeof(string) })!;
            ab.SetCustomAttribute(new CustomAttributeBuilder(iacaCtor, new object[] { iface.Assembly.GetName().Name! }));

            var mb = ab.DefineDynamicModule("m");
            var tb = mb.DefineType("EmittedTestDataCallback", TypeAttributes.Public | TypeAttributes.Class, typeof(object), new[] { iface });
            var sinkField = tb.DefineField("Sink", typeof(TestResultSink), FieldAttributes.Public);

            var receive = typeof(TestResultSink).GetMethod(nameof(TestResultSink.Receive))!;
            var completedTaskGetter = typeof(Task).GetProperty(nameof(Task.CompletedTask))!.GetGetMethod()!;

            // Implement every method of the interface + its bases (ITestWindowDataCallback : IDisposable).
            var methods = new List<MethodInfo>(iface.GetMethods());
            foreach (var bi in iface.GetInterfaces()) methods.AddRange(bi.GetMethods());

            foreach (var im in methods)
            {
                var pars = im.GetParameters().Select(p => p.ParameterType).ToArray();
                var m = tb.DefineMethod(im.Name,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final,
                    im.ReturnType, pars);
                var il = m.GetILGenerator();

                if (im.Name == "OnTestDataAsync")
                {
                    // this.Sink.Receive(arg1)
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, sinkField);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Callvirt, receive);
                }

                // Return: Task.CompletedTask for Task-returning methods; nothing for void (Dispose).
                if (im.ReturnType == typeof(Task))
                    il.Emit(OpCodes.Call, completedTaskGetter);
                else if (im.ReturnType != typeof(void))
                    il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);

                tb.DefineMethodOverride(m, im);
            }

            return tb.CreateType()!;
        }
    }
}
