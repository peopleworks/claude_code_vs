using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeVs.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;   // IInvocationOperation / IObjectCreationOperation (callees walk)
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;   // TaskScheduler.GetAwaiter — await TaskScheduler.Default hops off the UI thread
using Newtonsoft.Json.Linq;

namespace ClaudeCodeVs.CodeModel;

/// <summary>
/// The semantic-model reader behind the <c>vs-semantic</c> MCP server — the static-analysis twin of
/// <see cref="ClaudeCodeVs.Debugging.DebuggerReader"/>. Where the debugger reader exposes RUNTIME state,
/// this exposes Visual Studio's resolved understanding of the CODE: symbols, references, implementations,
/// and hierarchies, straight from the live <see cref="VisualStudioWorkspace"/> Roslyn snapshot. It replaces
/// the CLI's text/grep guesses with ground truth that resolves through interfaces, overrides, partials,
/// and generics.
///
/// Threading: only acquiring the workspace + reading <see cref="VisualStudioWorkspace.CurrentSolution"/>
/// needs the UI thread (a COM service grab). The Roslyn <see cref="Solution"/> is an immutable,
/// free-threaded snapshot, so the heavy <see cref="SymbolFinder"/> queries run off-thread without
/// stalling the editor — a cleaner story than the EnvDTE debugger path, which is UI-thread-bound end to end.
///
/// Addressing: every navigation takes a stable <c>symbolId</c> (a Roslyn DocumentationCommentId such as
/// "M:Ns.Type.Method(System.Int32)") OR a <c>file</c>+<c>line</c>(+<c>column</c>) position. The first comes
/// from <see cref="SearchSymbolsAsync"/>; the second is the cursor-style fallback. See <see cref="ResolveSymbolAsync"/>.
///
/// Managed (C#/VB) only: Roslyn has no C++ model. Loose files in Open-Folder mode have no workspace, so
/// queries return {"available":false} — the model can tell "no project loaded" from a real empty result.
/// </summary>
internal static class RoslynReader
{
    // Output caps (mirror the debugger reader's "bounded but signaled" convention). When a cap truncates,
    // the payload carries {"truncated":true,...} so the model knows data was cut and can narrow its query.
    private const int MaxSearchResults = 80;
    private const int MaxReferences = 200;
    private const int MaxImplementations = 120;
    private const int MaxHierarchyNodes = 120;
    private const int MaxCallerDepth = 3;
    private const int SnippetChars = 200;

    /// <summary>Grab the live workspace. MUST be called on the UI thread (COM service acquisition).</summary>
    private static VisualStudioWorkspace? GetWorkspace()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var cm = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
        return cm?.GetService<VisualStudioWorkspace>();
    }

    /// <summary>
    /// Take the workspace handle + immutable solution snapshot on the UI thread, then hop OFF it so the heavy
    /// SymbolFinder work never blocks the editor. Returns null if there's no loaded C#/VB solution.
    /// </summary>
    private static async Task<Solution?> GetSolutionOffThreadAsync(CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var solution = GetWorkspace()?.CurrentSolution;
        await TaskScheduler.Default;   // immutable snapshot is free-threaded from here
        return solution;
    }

    /// <summary>{"available":false} envelope when there's no loaded C#/VB solution to query.</summary>
    private static JObject Unavailable() =>
        new JObject
        {
            ["available"] = false,
            ["reason"] = "No C#/VB solution is loaded (Roslyn workspace empty). Open the code as a project, not a loose file.",
        };

    private static JObject Err(string message) => new JObject { ["available"] = true, ["error"] = message };

    // ---- Search (the addressing primitive) -------------------------------------------------------------

    /// <summary>
    /// Symbol search → candidate declarations, each carrying a stable <c>symbolId</c> the navigation tools
    /// take to name one exact symbol. Case-insensitive substring match across the whole solution.
    /// </summary>
    public static async Task<JObject> SearchSymbolsAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Err("query is required");
        var solution = await GetSolutionOffThreadAsync(ct);
        if (solution == null || !solution.Projects.Any()) return Unavailable();

        var matches = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            name => name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0,
            SymbolFilter.TypeAndMember,
            ct).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new JArray();
        bool truncated = false;
        foreach (var sym in matches)
        {
            ct.ThrowIfCancellationRequested();
            var id = sym.GetDocumentationCommentId();
            if (id == null || !seen.Add(id)) continue;
            if (results.Count >= MaxSearchResults) { truncated = true; break; }
            results.Add(DescribeSymbol(sym, id));
        }

        var payload = new JObject
        {
            ["available"] = true,
            ["query"] = query,
            ["count"] = results.Count,
            ["symbols"] = results,
        };
        Mark(payload, truncated, MaxSearchResults, "results");
        return payload;
    }

    // ---- Navigation ------------------------------------------------------------------------------------

    /// <summary>Semantic Find-All-References: resolves through interfaces, overrides, partials, generics.</summary>
    public static async Task<JObject> FindReferencesAsync(JToken args, CancellationToken ct)
    {
        var solution = await GetSolutionOffThreadAsync(ct);
        if (solution == null || !solution.Projects.Any()) return Unavailable();
        var (symbol, error) = await ResolveSymbolAsync(solution, args, ct);
        if (symbol == null) return error!;

        var found = await SymbolFinder.FindReferencesAsync(symbol, solution, ct).ConfigureAwait(false);
        var cache = new Dictionary<DocumentId, SourceText>();
        var locations = new JArray();
        bool truncated = false;
        foreach (var rs in found)
        {
            foreach (var loc in rs.Locations)
            {
                ct.ThrowIfCancellationRequested();
                if (locations.Count >= MaxReferences) { truncated = true; break; }
                if (loc.Location.IsInSource)
                    locations.Add(await LocationJsonAsync(loc.Document, loc.Location, cache, loc.IsImplicit, ct));
            }
            if (truncated) break;
        }

        var payload = new JObject
        {
            ["available"] = true,
            ["symbol"] = DescribeSymbol(symbol, symbol.GetDocumentationCommentId()),
            ["referenceCount"] = locations.Count,
            ["references"] = locations,
        };
        Mark(payload, truncated, MaxReferences, "reference locations");
        return payload;
    }

    /// <summary>Go to Definition: the resolved symbol's declaration(s) — the right one, not grep's ten.</summary>
    public static async Task<JObject> GoToDefinitionAsync(JToken args, CancellationToken ct)
    {
        var solution = await GetSolutionOffThreadAsync(ct);
        if (solution == null || !solution.Projects.Any()) return Unavailable();
        var (symbol, error) = await ResolveSymbolAsync(solution, args, ct);
        if (symbol == null) return error!;

        var cache = new Dictionary<DocumentId, SourceText>();
        var defs = new JArray();
        foreach (var loc in symbol.Locations.Where(l => l.IsInSource))
        {
            var doc = solution.GetDocument(loc.SourceTree);
            defs.Add(await LocationJsonAsync(doc, loc, cache, false, ct));
        }

        var payload = new JObject
        {
            ["available"] = true,
            ["symbol"] = DescribeSymbol(symbol, symbol.GetDocumentationCommentId()),
            ["definitions"] = defs,
        };
        // Metadata-only symbols (framework/3rd-party) have no source location — say so rather than empty.
        if (defs.Count == 0)
            payload["note"] = $"'{symbol.Name}' is defined in metadata (assembly {symbol.ContainingAssembly?.Name}), not in source.";
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: ct);
        if (!string.IsNullOrWhiteSpace(xml)) payload["xmlDoc"] = Cap(xml.Trim(), 600);
        return payload;
    }

    /// <summary>Find Implementations / overrides: interfaces &amp; abstract/virtual members → concrete code.</summary>
    public static async Task<JObject> FindImplementationsAsync(JToken args, CancellationToken ct)
    {
        var solution = await GetSolutionOffThreadAsync(ct);
        if (solution == null || !solution.Projects.Any()) return Unavailable();
        var (symbol, error) = await ResolveSymbolAsync(solution, args, ct);
        if (symbol == null) return error!;

        var cache = new Dictionary<DocumentId, SourceText>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var results = new JArray();
        bool truncated = false;

        async Task AddAsync(IEnumerable<ISymbol> syms)
        {
            foreach (var s in syms)
            {
                ct.ThrowIfCancellationRequested();
                var id = s.GetDocumentationCommentId();
                if (id != null && !seen.Add(id)) continue;
                if (results.Count >= MaxImplementations) { truncated = true; return; }
                results.Add(DescribeSymbol(s, id));
            }
        }

        if (symbol is INamedTypeSymbol nt && nt.TypeKind == TypeKind.Interface)
        {
            await AddAsync(await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct).ConfigureAwait(false));
            if (!truncated)
                await AddAsync((await SymbolFinder.FindDerivedInterfacesAsync(nt, solution, cancellationToken: ct).ConfigureAwait(false)).Cast<ISymbol>());
        }
        else if (symbol is INamedTypeSymbol cls && cls.TypeKind == TypeKind.Class)
        {
            // "Implementations" of an abstract/base class = the classes that derive from it.
            await AddAsync((await SymbolFinder.FindDerivedClassesAsync(cls, solution, cancellationToken: ct).ConfigureAwait(false)).Cast<ISymbol>());
        }
        else if (symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
        {
            // Interface-member implementors…
            await AddAsync(await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct).ConfigureAwait(false));
            // …and, if it's polymorphic, the overrides.
            if (!truncated && (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride))
                await AddAsync(await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: ct).ConfigureAwait(false));
        }
        else
        {
            return Err($"'{symbol.Name}' ({symbol.Kind}) isn't an interface, class, or overridable member; nothing to find implementations for.");
        }

        var payload = new JObject
        {
            ["available"] = true,
            ["symbol"] = DescribeSymbol(symbol, symbol.GetDocumentationCommentId()),
            ["implementationCount"] = results.Count,
            ["implementations"] = results,
        };
        Mark(payload, truncated, MaxImplementations, "implementations");
        return payload;
    }

    /// <summary>Type hierarchy: base chain + interfaces (direction "base"), or derived types ("derived").</summary>
    public static async Task<JObject> TypeHierarchyAsync(JToken args, CancellationToken ct)
    {
        var solution = await GetSolutionOffThreadAsync(ct);
        if (solution == null || !solution.Projects.Any()) return Unavailable();
        var (symbol, error) = await ResolveSymbolAsync(solution, args, ct);
        if (symbol == null) return error!;
        if (symbol is not INamedTypeSymbol type)
            return Err($"'{symbol.Name}' is a {symbol.Kind}, not a type — type hierarchy needs a class/interface/struct.");

        var direction = ((string?)args?["direction"] ?? "derived").ToLowerInvariant();
        var nodes = new JArray();
        bool truncated = false;

        if (direction == "base")
        {
            for (var bt = type.BaseType; bt != null && bt.SpecialType != SpecialType.System_Object; bt = bt.BaseType)
            {
                if (nodes.Count >= MaxHierarchyNodes) { truncated = true; break; }
                var n = DescribeSymbol(bt, bt.GetDocumentationCommentId());
                n["relation"] = "base";
                nodes.Add(n);
            }
            foreach (var iface in type.AllInterfaces)
            {
                if (nodes.Count >= MaxHierarchyNodes) { truncated = true; break; }
                var n = DescribeSymbol(iface, iface.GetDocumentationCommentId());
                n["relation"] = "interface";
                nodes.Add(n);
            }
        }
        else
        {
            IEnumerable<INamedTypeSymbol> derived = type.TypeKind == TypeKind.Interface
                ? (await SymbolFinder.FindImplementationsAsync(type, solution, cancellationToken: ct).ConfigureAwait(false)).OfType<INamedTypeSymbol>()
                    .Concat(await SymbolFinder.FindDerivedInterfacesAsync(type, solution, cancellationToken: ct).ConfigureAwait(false))
                : await SymbolFinder.FindDerivedClassesAsync(type, solution, cancellationToken: ct).ConfigureAwait(false);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in derived)
            {
                var id = d.GetDocumentationCommentId();
                if (id != null && !seen.Add(id)) continue;
                if (nodes.Count >= MaxHierarchyNodes) { truncated = true; break; }
                nodes.Add(DescribeSymbol(d, id));
            }
        }

        var payload = new JObject
        {
            ["available"] = true,
            ["type"] = DescribeSymbol(type, type.GetDocumentationCommentId()),
            ["direction"] = direction,
            ["count"] = nodes.Count,
            ["types"] = nodes,
        };
        Mark(payload, truncated, MaxHierarchyNodes, "types");
        return payload;
    }

    /// <summary>
    /// Call hierarchy. "callers": who transitively calls this method (recursive to a depth, cycle-guarded) —
    /// the high-value impact-analysis direction. "callees": what this method directly calls (depth 1, via the
    /// language-agnostic IOperation tree).
    /// </summary>
    public static async Task<JObject> CallHierarchyAsync(JToken args, CancellationToken ct)
    {
        var solution = await GetSolutionOffThreadAsync(ct);
        if (solution == null || !solution.Projects.Any()) return Unavailable();
        var (symbol, error) = await ResolveSymbolAsync(solution, args, ct);
        if (symbol == null) return error!;

        var direction = ((string?)args?["direction"] ?? "callers").ToLowerInvariant();
        int nodeBudget = MaxHierarchyNodes;
        bool truncated = false;

        if (direction == "callees")
        {
            var cache = new Dictionary<DocumentId, SourceText>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var callees = new JArray();
            foreach (var sref in symbol.DeclaringSyntaxReferences)
            {
                var node = await sref.GetSyntaxAsync(ct).ConfigureAwait(false);
                var doc = solution.GetDocument(node.SyntaxTree);
                if (doc == null) continue;
                var model = await doc.GetSemanticModelAsync(ct).ConfigureAwait(false);
                var op = model?.GetOperation(node, ct);
                if (op == null) continue;
                // Explicit IOperation walk (ChildOperations) rather than the Descendants() extension, which
                // collides with an unrelated Descendants<T> overload. Language-agnostic: works for C# and VB.
                var stack = new Stack<IOperation>();
                stack.Push(op);
                while (stack.Count > 0)
                {
                    var d = stack.Pop();
                    foreach (var child in d.ChildOperations) stack.Push(child);
                    IMethodSymbol? target = d switch
                    {
                        IInvocationOperation io => io.TargetMethod,
                        IObjectCreationOperation oco => oco.Constructor,
                        _ => null,
                    };
                    if (target == null) continue;
                    var id = target.GetDocumentationCommentId();
                    if (id != null && !seen.Add(id)) continue;
                    if (callees.Count >= nodeBudget) { truncated = true; break; }
                    callees.Add(DescribeSymbol(target, id));
                }
                if (truncated) break;
            }
            var p = new JObject
            {
                ["available"] = true,
                ["method"] = DescribeSymbol(symbol, symbol.GetDocumentationCommentId()),
                ["direction"] = "callees",
                ["note"] = "Direct callees only (depth 1).",
                ["count"] = callees.Count,
                ["callees"] = callees,
            };
            Mark(p, truncated, nodeBudget, "callees");
            return p;
        }

        // direction == callers (default): transitive, depth-limited, cycle-guarded.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var cacheC = new Dictionary<DocumentId, SourceText>();

        async Task<JArray> CallersOfAsync(ISymbol sym, int depth)
        {
            var arr = new JArray();
            if (depth > MaxCallerDepth || nodeBudget <= 0) return arr;
            var callers = await SymbolFinder.FindCallersAsync(sym, solution, ct).ConfigureAwait(false);
            foreach (var c in callers)
            {
                ct.ThrowIfCancellationRequested();
                if (nodeBudget <= 0) { truncated = true; break; }
                nodeBudget--;
                var id = c.CallingSymbol.GetDocumentationCommentId();
                var node = DescribeSymbol(c.CallingSymbol, id);
                var sites = new JArray();
                foreach (var loc in c.Locations.Where(l => l.IsInSource).Take(5))
                {
                    var doc = solution.GetDocument(loc.SourceTree);
                    sites.Add(await LocationJsonAsync(doc, loc, cacheC, false, ct));
                }
                if (sites.Count > 0) node["callSites"] = sites;
                if (id != null && visited.Add(id))
                {
                    var sub = await CallersOfAsync(c.CallingSymbol, depth + 1);
                    if (sub.Count > 0) node["callers"] = sub;
                }
                else if (id != null)
                {
                    node["recursionElided"] = true;   // already expanded elsewhere — don't loop
                }
                arr.Add(node);
            }
            return arr;
        }

        var tree = await CallersOfAsync(symbol, 1);
        var payload = new JObject
        {
            ["available"] = true,
            ["method"] = DescribeSymbol(symbol, symbol.GetDocumentationCommentId()),
            ["direction"] = "callers",
            ["maxDepth"] = MaxCallerDepth,
            ["callers"] = tree,
        };
        Mark(payload, truncated, MaxHierarchyNodes, "caller nodes");
        return payload;
    }

    /// <summary>
    /// The symbol at a 1-based file position (the caret / selection start), described like a search result
    /// (incl. its <c>symbolId</c>) - or null if no solution/file/symbol. Used to enrich vs_get_selection so
    /// "what I have selected" comes back with a navigable symbolId.
    /// </summary>
    public static async Task<JObject?> SymbolAtPositionAsync(string file, int line1Based, int col1Based, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        var solution = await GetSolutionOffThreadAsync(ct);
        if (solution == null) return null;
        var doc = FindDocument(solution, file);
        if (doc == null) return null;
        var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
        if (line1Based < 1 || line1Based > text.Lines.Count) return null;
        var line = text.Lines[line1Based - 1];
        int pos = Math.Min(line.Start + Math.Max(0, col1Based - 1), Math.Max(line.Start, line.EndIncludingLineBreak - 1));
        var sym = await SymbolFinder.FindSymbolAtPositionAsync(doc, pos, ct).ConfigureAwait(false);
        return sym == null ? null : DescribeSymbol(sym, sym.GetDocumentationCommentId());
    }

    // ---- Symbol resolution (symbolId | file:line) ------------------------------------------------------

    /// <summary>
    /// Resolve the tool args to exactly one <see cref="ISymbol"/>: prefer a <c>symbolId</c>
    /// (DocumentationCommentId), else a <c>file</c>+<c>line</c>(+<c>column</c>) position. Returns
    /// (symbol, null) on success or (null, errorJson) with a model-actionable message.
    /// </summary>
    private static async Task<(ISymbol? symbol, JObject? error)> ResolveSymbolAsync(Solution solution, JToken args, CancellationToken ct)
    {
        var symbolId = (string?)args?["symbolId"];
        if (!string.IsNullOrWhiteSpace(symbolId))
        {
            foreach (var project in solution.Projects)
            {
                ct.ThrowIfCancellationRequested();
                var comp = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (comp == null) continue;
                var sym = DocumentationCommentId.GetFirstSymbolForDeclarationId(symbolId, comp);
                if (sym != null) return (sym, null);
            }
            return (null, Err($"No symbol found for symbolId '{symbolId}'. Get a valid id from vs_search_symbols."));
        }

        var file = (string?)args?["file"];
        var line = (int?)args?["line"];
        if (!string.IsNullOrWhiteSpace(file) && line.HasValue)
        {
            int col = (int?)args?["column"] ?? 1;
            var doc = FindDocument(solution, file!);
            if (doc == null) return (null, Err($"File is not part of the loaded solution: {file}"));
            var text = await doc.GetTextAsync(ct).ConfigureAwait(false);
            if (line.Value < 1 || line.Value > text.Lines.Count)
                return (null, Err($"line {line} is out of range for {file} (1..{text.Lines.Count})."));
            var lineStart = text.Lines[line.Value - 1];
            int pos = Math.Min(lineStart.Start + Math.Max(0, col - 1), lineStart.EndIncludingLineBreak - 1);
            var sym = await SymbolFinder.FindSymbolAtPositionAsync(doc, pos, ct).ConfigureAwait(false);
            if (sym == null) return (null, Err($"No symbol at {file}:{line}:{col}. Point at an identifier."));
            return (sym, null);
        }

        return (null, Err("Provide either 'symbolId' (from vs_search_symbols) or 'file'+'line' (+optional 'column')."));
    }

    /// <summary>Find a document by path — exact first, then a case-insensitive fallback (Windows paths).</summary>
    private static Document? FindDocument(Solution solution, string filePath)
    {
        var ids = solution.GetDocumentIdsWithFilePath(filePath);
        if (!ids.IsEmpty) return solution.GetDocument(ids[0]);
        foreach (var project in solution.Projects)
            foreach (var doc in project.Documents)
                if (string.Equals(doc.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    return doc;
        return null;
    }

    // ---- Shaping helpers -------------------------------------------------------------------------------

    /// <summary>Compact, model-friendly description of one symbol + its primary source location.</summary>
    private static JObject DescribeSymbol(ISymbol sym, string? symbolId)
    {
        var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
        var obj = new JObject
        {
            ["name"] = sym.Name,
            ["kind"] = sym.Kind.ToString(),
            ["signature"] = sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            ["container"] = sym.ContainingSymbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            ["symbolId"] = symbolId,
        };
        if (loc != null)
        {
            var span = loc.GetLineSpan();
            obj["file"] = span.Path;
            obj["line"] = span.StartLinePosition.Line + 1;     // 1-based for the model / editor
            obj["column"] = span.StartLinePosition.Character + 1;
        }
        return obj;
    }

    /// <summary>{file, line, column, snippet} for a source location, with a per-call text cache.</summary>
    private static async Task<JObject> LocationJsonAsync(
        Document? doc, Location loc, Dictionary<DocumentId, SourceText> cache, bool isImplicit, CancellationToken ct)
    {
        var span = loc.GetLineSpan();
        int lineIdx = span.StartLinePosition.Line;
        var obj = new JObject
        {
            ["file"] = span.Path,
            ["line"] = lineIdx + 1,
            ["column"] = span.StartLinePosition.Character + 1,
        };
        if (isImplicit) obj["implicit"] = true;
        if (doc != null)
        {
            if (!cache.TryGetValue(doc.Id, out var text))
            {
                text = await doc.GetTextAsync(ct).ConfigureAwait(false);
                cache[doc.Id] = text;
            }
            if (lineIdx >= 0 && lineIdx < text.Lines.Count)
                obj["snippet"] = Cap(text.Lines[lineIdx].ToString().Trim(), SnippetChars);
        }
        return obj;
    }

    private static void Mark(JObject payload, bool truncated, int cap, string unit)
    {
        if (!truncated) return;
        payload["truncated"] = true;
        payload["note"] = $"capped at {cap} {unit}; narrow the query for more.";
    }

    private static string Cap(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";
}
