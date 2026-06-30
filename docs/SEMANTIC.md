# Semantic code navigation

Most coding assistants read your *source* and search it with text (grep/ripgrep). This extension also gives Claude Visual Studio's **resolved semantic model of the code** — Roslyn's own understanding of symbols, references, implementations, and type/call hierarchies — so "where is this used", "which overload", "what implements this" come back as **ground truth** instead of a text guess that over- or under-counts.

It's the third axis of what an IDE knows, alongside the two this extension already exposed:

| Axis | What it is | Surfaced by |
|---|---|---|
| **Runtime state** | where execution is, variable values, threads, heap | the debugger + ClrMD tools (`vs-debug`) — see [`DEBUGGER.md`](DEBUGGER.md) |
| **Diagnostics** | compiler errors/warnings | `getDiagnostics` (Error List) |
| **Semantic model** | symbols, references, implementations, hierarchies | **the `vs-semantic` tools, this doc** |

The `claude` CLI does all the agent work; the extension exposes Roslyn to it over the same localhost bridge that powers the diff, diagnostics, and debugger features.

---

## Why this beats grep

Text search is the assistant's single most common operation ("where is this used") and one of its least reliable. Grep:

- **Misses indirect references** — a call through an interface, a virtual dispatch, an *explicit* interface implementation, a type alias, a `using static`.
- **Over-counts** — matches in comments, strings, and unrelated same-named symbols (ten `Helper`s, five `Save()` overloads).
- **Can't disambiguate** — `Describe(` returns every overload for *any* call site.

Roslyn resolves all of that against the real compilation. The `vs-semantic` tools turn a long, error-prone chain of grep-and-read into one authoritative answer — and unlike the debugger tools, they need **no debug session**: they work the moment a C#/VB solution is loaded.

---

## See it: the RefMaze fixture

[`demo/RefMaze`](../demo/RefMaze) is a small "reference maze" built so each tool returns something text search gets wrong: an `IShape` interface with three implementors (one via an **explicit** interface implementation, `Triangle.IShape.Area`), a `ShapeBase` split (Circle/Square derive from it, Triangle implements `IShape` directly), an overloaded `Describe(...)`, and a `Main -> Report -> SummarizeAll -> IShape.Area()` call chain.

A representative run against it:

| Tool | Call | Result (ground truth grep can't match) |
|---|---|---|
| `vs_search_symbols` | `"Area"` | 5 distinct declarations: the interface member, `ShapeBase.Area` (abstract), `Circle`/`Square` overrides, and `Triangle.IShape.Area` (explicit) — each with a `symbolId` |
| `vs_find_implementations` | `IShape` | `Circle, Square, Triangle, ShapeBase` — including the abstract base and the explicit implementor |
| `vs_find_references` | `IShape` | 8 uses across `Shapes.cs` + `Program.cs`, excluding comments |
| `vs_call_hierarchy` (callers) | `IShape.Area` | `SummarizeAll` (interface-dispatched) **and** `ShapeBase.ToString` — both real callers |
| `vs_type_hierarchy` (derived) | `IShape` | all four implementors, transitive (`Square` via `ShapeBase`) + direct (`Triangle`) |
| `vs_go_to_definition` | cursor on `Describe(shapes[0], verbose: true)` | the **2-arg** overload, not both |

---

## Two workflows it unlocks

The tools compose. `vs_search_symbols` (or `vs_get_selection`) yields a `symbolId`; everything else consumes one. Two end-to-end examples of how that changes what the agent can do:

### 1. "I'm about to change `Area()` — what's the blast radius?"

Impact analysis that grep can only approximate:

1. **`vs_search_symbols("Area")`** → the `IShape.Area` member, `symbolId: M:RefMaze.IShape.Area`.
2. **`vs_find_implementations(M:RefMaze.IShape.Area)`** → every implementor, including `ShapeBase.Area` (abstract) and the **explicit** `Triangle.IShape.Area` — the one a `grep "\.Area"` can't tie back to the interface.
3. **`vs_call_hierarchy(M:RefMaze.IShape.Area, callers)`** → the transitive caller tree: `SummarizeAll ← {Report, Describe} ← Main`, plus the surprise caller `ShapeBase.ToString`. That's the real set of places to re-test — derived semantically, not guessed from text.

Where grep would over-count (every `.Area` in comments/strings/unrelated types) *and* under-count (missing the interface-dispatched and explicit-impl call sites), this is the exact, complete answer.

### 2. "What does this library call actually do?"

The capability that has no grep equivalent at all — reading a body that isn't in your repo:

1. The agent sees `JsonConvert.SerializeObject(order)` in your code and wants to know what it does. It can't — that body lives in `Newtonsoft.Json.dll`.
2. **`vs_go_to_definition`** on the call site → resolves the metadata symbol and hands back its `symbolId` (`M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object)`).
3. **`vs_decompile`** that `symbolId` → the **real decompiled body**: `return SerializeObject(value, (Type?)null, (JsonSerializerSettings?)null);`.

Point it at `String.Substring` instead and it decompiles to a stub, then auto-fetches the **real .NET runtime source** via SourceLink (see below). Either way the agent reads what the call does instead of guessing from the name.

---

## How it reaches the model: a third MCP server

The IDE-integration protocol (the WebSocket the CLI connects to) is **CLI-curated** — it surfaces only `getDiagnostics` (+ `executeCode`) and drives the rest itself, so a tool added there would never be called by the model. So, exactly like the debugger's pull channel, these tools live on a **user-registered MCP server** — the open plugin door the CLI surfaces in full.

There are now two such servers, both backed by the **same stdio shim** (`vs-mcp-shim.ps1`) with a different `-Route`, both reaching the bridge's localhost `HttpListener`:

```
  Claude (CLI) --stdio JSON-RPC--> vs-mcp-shim.ps1 -Route /mcp           --> POST /mcp           --> vs-debug    (runtime tools)
               --stdio JSON-RPC--> vs-mcp-shim.ps1 -Route /mcp-semantic  --> POST /mcp-semantic  --> vs-semantic (Roslyn tools)
                                                                                      |
                                                                                      v
                                                              RoslynReader  --VisualStudioWorkspace-->  Roslyn semantic model
```

`McpInstaller` registers both servers in your workspace `.mcp.json` at Launch (a one-time CLI trust prompt for the new one). The tools run **in-proc in C#** against the live `VisualStudioWorkspace`; the shim is a dumb pipe.

**Threading note.** Unlike the EnvDTE debugger path (UI-thread-bound end to end), the Roslyn `Solution` is an immutable, free-threaded snapshot. The tools take the workspace handle on the UI thread, then hop **off** it for the heavy `SymbolFinder` query — so navigation never stalls the editor.

---

## Tool catalog

All tools live on the `vs-semantic` MCP server and appear to the model as `mcp__vs-semantic__*`. **All read-only and ungated** (no execution, no mutation). Managed (**C#/VB only**); each returns `{"available":false}` when no project is loaded.

### Addressing: how you name a symbol

Every navigation tool takes **either**:
- `symbolId` — a stable Roslyn **DocumentationCommentId** (e.g. `M:RefMaze.IShape.Area`, `T:RefMaze.Circle`), **preferred**, obtained from `vs_search_symbols`; or
- `file` + `line` (+ optional `column`) — cursor-style, resolves the symbol referenced at that position (great for disambiguating a specific call site).

The intended workflow is **search -> take the symbolId -> navigate**.

| Tool | What it returns |
|---|---|
| `vs_get_selection` | What the user currently has selected (or where the caret is) in the active editor: selected text, file, and range — **plus the Roslyn symbol at that position with its `symbolId`** when the file is in the loaded solution. Lets the model act on "this" / "the selected code" and immediately navigate from it (selection -> `symbolId` -> references/callers). The selection read works in any language; the symbol enrichment is C#/VB. |
| `vs_search_symbols` | Declarations whose name contains the query (case-insensitive), each with `symbolId`, kind, signature, container, and source `file:line`. The addressing primitive **and** the semantic "where is X declared". |
| `vs_find_references` | All references to a symbol across the solution, each as `file:line:column` + a code snippet — resolving through interfaces, overrides, partials, generics; comments/strings excluded. |
| `vs_go_to_definition` | The resolved symbol's declaration(s) + signature + XML doc — the **right** one among overloads / many same-named types. Metadata-only symbols report the defining assembly. |
| `vs_find_implementations` | Concrete implementations of an interface/interface-member, overrides of an abstract/virtual member, or derived classes of a base — exact via Roslyn. |
| `vs_call_hierarchy` | `direction:"callers"` (default): who **transitively** calls a method, as a depth-limited, cycle-guarded tree with call sites — impact analysis. `direction:"callees"`: what the method directly calls (depth 1). |
| `vs_type_hierarchy` | `direction:"derived"` (default): subtypes/implementors. `direction:"base"`: the base-class chain + implemented interfaces. |
| `vs_decompile` | A framework/NuGet symbol **with no source** (a method/type in a referenced DLL) → its **decompiled C#** — the one thing reading the repo can't give you. See below. |

Output is **bounded but signaled**: large results are capped (references 200, implementations/hierarchy 120, search 80, caller depth 3) and carry `{"truncated":true,"note":"..."}` so the model knows to narrow its query.

---

## Reading library bodies: `vs_decompile`

This is the headline of the semantic surface and the **one thing reading the repo fundamentally cannot do**: show the *body* of a method that lives in a referenced DLL with no source — `JsonConvert.SerializeObject`, `Enumerable.Where`, `String.Substring`. The CLI can grep your code; it can never read what a library call actually does. `vs_decompile` can.

It works the way **Go-To-Definition** does, by reusing **VS's own metadata-as-source service** (ILSpy under the hood) — no new dependency, just reflection against the already-loaded Roslyn. Address the symbol by `symbolId` (e.g. `T:System.Linq.Enumerable`, or the `symbolId` that `vs_go_to_definition` / `vs_get_selection` hand back for a metadata symbol) or by `file`+`line` on a call site. By default you get **just the requested member** (its declaration + doc comments); `wholeType:true` returns the whole containing type. Output is capped + signaled.

Three source paths, each marked in the result (`source` = `decompiled` | `source`, plus `bodyAvailable`):

| Target | Path | Result |
|---|---|---|
| `JsonConvert.SerializeObject` (NuGet) | ILSpy decompile of the `lib/` DLL | real body |
| `Enumerable.Where` (framework) | ILSpy decompile of the runtime impl | real body (`WhereArrayIterator`, the fast paths) — **not** a ref-assembly stub |
| `String.Substring` (core BCL) | stub → **SourceLink auto-retry** | the **real .NET runtime source** |

**The BCL wrinkle, handled.** Core BCL types (`String`, `Int32`, …) are type-forwarded to `System.Private.CoreLib`, whose implementation the decompiler can't resolve from the project's references — so they decompile to a **signature-only stub**. The tool detects this (`bodyAvailable:false`) and **automatically retries via SourceLink** (`NavigateToSourceLinkAndEmbeddedSources`), which fetches the *real* source from `dotnet/runtime` at the exact commit. That retry is **bounded (20s)** so an offline/slow symbol server can't hang the call, and it's skipped when the body decompiled fine. Pass `preferSource:true` to go SourceLink-first (real source for everything that has it, at the cost of a network round-trip). Offline, a core-BCL symbol returns the stub with `bodyAvailable:false` + `sourceLinkTried:true` — honest, never a silent stub-masquerading-as-body.

---

## Limitations

- **Managed (C#/VB) only.** Roslyn has no C++ model. (C++ *build* diagnostics still flow through the Error List; C++ *navigation* is not covered.)
- **Needs a loaded project.** Loose files in Open-Folder mode have no Roslyn workspace -> `{"available":false}`. Same caveat as diagnostics.
- **Scope is the solution loaded in VS**, not the CLI's working directory. If they differ, the tools see what VS has open.
- **`callees` is direct-only (depth 1).** Transitive callees aren't reconstructed (read the source or use `callers` from the other end). Callers *is* transitive.
- **Generated/source-gen symbols** resolve, but their locations may point at generated files.
- **No mutation.** Rename/refactor/code-fixes are deliberately out of scope (they'd cross the diff-gate boundary). This surface is navigation/comprehension only.

---

## Try it

Open [`demo/RefMaze/RefMaze.sln`](../demo/RefMaze) in Visual Studio 2026, Launch Claude Code, and ask Claude to map the `IShape` hierarchy, find every implementor, or trace who calls `Area()` — then compare against `grep IShape` / `grep Area`. The maze's own comments name what each tool should prove.

---

## Next

- **Transitive callees** (full call-graph in both directions).
- **Symbol rename / safe refactors**, routed through the existing diff gate so each edit is still a single accept/reject.
- **Roslyn-precise diagnostic spans** — the same `VisualStudioWorkspace` powers tighter `getDiagnostics` ranges (see [`ROADMAP.md`](../ROADMAP.md)).
