using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace ClaudeCodeVs.Debugging;

/// <summary>
/// ClrMD (Microsoft.Diagnostics.Runtime) targets netstandard2.0 and drags in BCL shims
/// (System.Collections.Immutable, System.Runtime.CompilerServices.Unsafe, System.Memory, …).
/// We can't add binding redirects to devenv.exe.config, so when devenv fails to bind one of those at
/// the exact version ClrMD wants, fall back to the copy we ship in the extension folder.
///
/// Installed once at package init, before any ClrMD type is touched. Scoped to assemblies we actually
/// ship (the DLL is present in our folder) so it can't clobber unrelated loads. AssemblyResolve only
/// fires when normal binding has already failed, so this is a pure safety net.
/// </summary>
internal static class ClrMdDependencyResolver
{
    private static int _installed;
    private static string? _dir;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        _dir = Path.GetDirectoryName(typeof(ClrMdDependencyResolver).Assembly.Location);
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    private static Assembly? Resolve(object sender, ResolveEventArgs args)
    {
        try
        {
            if (string.IsNullOrEmpty(_dir)) return null;
            string? simple = new AssemblyName(args.Name).Name;
            if (string.IsNullOrEmpty(simple)) return null;
            string path = Path.Combine(_dir!, simple + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
        catch { return null; }
    }
}
