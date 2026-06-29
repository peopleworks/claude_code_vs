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

Output is **bounded but signaled**: large results are capped (references 200, implementations/hierarchy 120, search 80, caller depth 3) and carry `{"truncated":true,"note":"..."}` so the model knows to narrow its query.

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
