# Test integration — the fix-verify loop

`dotnet test` runs your tests, and the `claude` CLI can already shell out to it. What it **can't** do is run one failing test **under Visual Studio's debugger and stop at the fault** with the live frame, or **hammer a flaky test until it fails and catch that exact iteration red-handed**. This extension gives Claude Visual Studio's own **Test Explorer engine**, wired to the **live debugger** — turning "run the tests" into a closed **fix → verify → catch** loop.

The composition is the whole point. The extension already exposes what the IDE *knows* — runtime state (the debugger), compiler diagnostics, the semantic model — and the test engine now sits **on top of the debugger**, so a failing test isn't just a red line: Claude can break into it at the throw with `$exception` and locals live, and can *reproduce a heisenbug on purpose* and be paused inside it.

| Axis | What it is | Surfaced by |
|---|---|---|
| **Runtime state** | execution point, variable values, threads, heap | the debugger + ClrMD tools — see [`DEBUGGER.md`](DEBUGGER.md) |
| **Semantic model** | symbols, references, implementations, hierarchies | the `vs-semantic` tools — see [`SEMANTIC.md`](SEMANTIC.md) |
| **Diagnostics** | compiler errors/warnings | `getDiagnostics` (Error List) |
| **Tests** | discover, run, **debug**, and **force-reproduce** | **the test tools, this doc** |

The `claude` CLI does all the agent work; the extension exposes Test Explorer's engine to it over the same localhost bridge that powers the diff, diagnostics, debugger, and semantic features.

---

## Why this beats `dotnet test`

Running a test and getting pass/fail is table stakes — the CLI can do that itself. The value here is everything that needs the **IDE and its debugger**, which a shell-out can't reach:

- **Debug *one* test under VS.** `dotnet test --filter X` runs a test; it can't *stop inside it*. `vs_debug_test` launches a single test under Visual Studio's real debugger, and with break-on-thrown armed it halts at the **throw site** — not the catch that swallowed it — with the call stack, args, and `$exception` live.
- **Catch a transient bug red-handed.** The signature move (`vs_catch_flaky`): loop a flaky test under the debugger until the failing iteration happens, and **stop on it automatically**. A `dotnet test` re-run loop gives you a different red line each time; this leaves you *paused inside the failure* with the state that caused it.
- **Real per-test results, structured.** `vs_run_test` returns each test's `outcome` + `errorMessage` + `errorStackTrace` + `durationMs` as data (via Test Explorer's own result stream), not a text blob to parse.
- **It composes.** Every result feeds the debugger tools you already have — `vs_debug_state`, `vs_get_frame_locals`, `vs_exception` — because the test engine and the debugger are the same session.

Table stakes are here too (discover, run one/all, re-run failures, coverage), but the reason this exists is the debugger composition.

---

## See it: the TestLab fixture

[`demo/TestLab`](../demo/TestLab) is a small xUnit project built so each tool has a clean target:

| Test | Designed to |
|---|---|
| `ScoreTests.Add_TwoPositives_Sums` | **pass** (baseline) |
| `ScoreTests.ScoreRounds_FlatBonusPerRound` | **fail an assertion** — a per-round bonus that should reset compounds instead, so `Assert.Equal(45, …)` gets 60 |
| `ScoreTests.Ratio_ByZero_Throws` | **throw** `DivideByZeroException` |
| `FlakyTests.Flaky_IntermittentThrow` | throw ~**1-in-3** (`InvalidOperationException`) — the flaky-hunter/catcher target |
| `FlakyTests.Flaky_IntermittentAssert` | assert-fail ~**1-in-3** (`roll == 0`) — the assertion-flavored flaky target |

A representative sweep:

| Tool | Call | Result |
|---|---|---|
| `vs_list_tests` | — | all 5 tests with real fully-qualified names + source paths (Roslyn, not grep) |
| `vs_run_test` | `ScoreRounds_FlatBonusPerRound` | `outcome:"Failed"`, `errorMessage:"Assert.Equal() Failure … Expected: 45 Actual: 60"`, stack at `Tests.cs:20` |
| `vs_run_test` (`collectCoverage`) | `Add_TwoPositives_Sums` | passes + a real `.coverage` attachment path |
| `vs_hunt_flaky` | `Flaky_IntermittentThrow` | reproduces within a few runs; captures the `InvalidOperationException` + rate |
| `vs_catch_flaky` | `Flaky_IntermittentThrow` | **paused at `Tests.cs:45`**, `$exception = InvalidOperationException` live, caught on run N |

---

## The loop

1. **Discover** — `vs_list_tests` finds the tests (via Roslyn, so it needs no build to *list*).
2. **Run** — `vs_run_test` runs one/all and returns real per-test outcomes (`collectCoverage:true` attaches a `.coverage` file).
3. **Fix, then verify the fix** — `vs_rerun_failed` re-runs *only* the last run's failures instead of the whole suite.
4. **Corner a deterministic failure** — `vs_debug_test` launches the one failing test under the debugger; pair with `vs_break_on_thrown` to stop at the fault, then read it with the debugger tools.
5. **Corner a *transient* failure** — `vs_hunt_flaky` force-reproduces it and captures each failing run; `vs_catch_flaky` catches it red-handed (below).

Steps 4–5 are the payoff: a bug that only shows up sometimes goes from "I can't reproduce it" to "the debugger is paused on it."

---

## The headline: catch a heisenbug red-handed — `vs_catch_flaky`

The one motion nothing else in the toolbox (or `dotnet test`) can do. Point it at a flaky test and it:

1. **Learns what to break on.** A quick pre-hunt reproduces the failure once and parses the exception type from it (`System.InvalidOperationException`). For a bare **assertion** failure — whose message carries no type — it arms the framework's assertion **base** type instead (`Xunit.Sdk.XunitException` / `NUnit.Framework.AssertionException` / MSTest's `AssertFailedException`); break-on-thrown matches subclasses, so the base catches `TrueException`, `EqualException`, and the rest. You can also pass `exception` explicitly to skip the pre-hunt.
2. **Loops under the debugger.** It arms break-on-thrown and runs the test under Visual Studio's debugger over and over. Passing iterations finish and it moves on; the first **failing** iteration throws the armed exception and the debugger **halts at the throw site** (reusing the same `OnModeChange` await engine as the debugger's drive tools).
3. **Leaves you inside the failure.** On a catch it returns the live break snapshot and stays **paused** at the fault — `vs_debug_state` / `vs_get_frame_locals` / `vs_exception` read the frame that caused it. If it never catches within the run/time budget it disarms and stops cleanly.

Verified on `Flaky_IntermittentThrow`: caught on run 5, paused at `Tests.cs:45`, `$exception = System.InvalidOperationException {"Flaky throw: hit the 1-in-3 path."}` live in the frame.

It's **gated** behind the panel's **Allow Claude to drive debugger** toggle (it launches and pauses the debugger), same as the other drive tools.

---

## Tool catalog

The test tools live on the **`vs-debug` MCP server** — deliberately co-located with the debugger tools, because the whole point is that they compose with them. They appear to the model as `mcp__vs-debug__*`. Managed (**.NET**) test projects.

| Tool | What it does | Gated |
|---|---|---|
| `vs_list_tests` | Discover tests via **Roslyn** (methods marked `[Fact]`/`[Theory]`/`[Test]`/`[TestMethod]`/`[TestCase]`) → real FQNs. No engine callback, no build needed to list. | — |
| `vs_run_test` | Run one (by FQN) or all; returns per-test `{outcome, errorMessage, errorStackTrace, durationMs}` + overall status. `collectCoverage:true` attaches a `.coverage` file. Self-builds first. | — |
| `vs_rerun_failed` | Re-run **only** the tests that failed in the last run (`Scope.ForState(Failed)`) — the classic fix-verify move. | — |
| `vs_debug_test` | Launch **one** test under the VS debugger; pair with `vs_break_on_thrown` to stop at the fault with live locals. | ✅ drive |
| `vs_hunt_flaky` | **Force-reproduce** a flaky failure: hammer a test until it fails (or `maxRuns`), capturing each failing run's real outcome/message/stack. Runs in the **background** (returns a `huntId` if it exceeds a ~40s inline window). `measureRate:true` runs all `maxRuns` to estimate the rate. | — |
| `vs_hunt_result` | Poll a background hunt by `huntId` → live progress + the terminal `verdict`. | — |
| `vs_hunt_cancel` | Stop a background hunt. | — |
| `vs_catch_flaky` | **Catch red-handed** — loop a test under the debugger with break-on-thrown armed until the failing iteration halts at the throw, paused for inspection. Auto-learns the exception type. | ✅ drive |

Results and progress are **bounded but signaled** (call stacks capped with a `{truncated}` marker; hunts surface `inconclusiveRuns`/`attempts`/under-sampling so a verdict is never quietly built on too few runs).

---

## How it works (two things worth knowing)

**Real per-test results come through an emitted callback.** Visual Studio's test engine reports pass/fail only through an **internal** result-callback interface (`ITestWindowDataCallback`) — its `RunTestsAsync` return value is *identical* for pass and fail (it means "the run completed", not "the tests passed"). You can't implement an internal interface in C# or `DispatchProxy`, so the extension **`Reflection.Emit`s** a type that implements it (with `[IgnoresAccessChecksTo]`) and forwards each streamed `TestNodeData` — that's how `vs_run_test` gets the real `outcome`/`errorMessage`/`errorStackTrace` instead of a bare bool. The engine itself is acquired in-proc via MEF (`IRequestFactory`), the path VS's own Test Explorer uses.

**Long hunts run async (start + poll), not deferred.** The `vs-debug` tools reach the model over an **HTTP** shim with a ~60s per-request timeout, so a multi-minute `measureRate` hunt can't just hold the request open (unlike `openDiff`, which defers on the persistent WebSocket). `vs_hunt_flaky` starts the hunt on a **background task**, waits ≤40s inline for the fast case, and otherwise hands back a `huntId` you poll with `vs_hunt_result` — so the hunt outlives the request and never times out.

---

## Limitations

- **Managed (.NET) test projects.** Discovery is Roslyn (C#/VB test attributes); the run/debug engine is the managed Test Explorer path. C++ tests aren't covered.
- **Needs a loaded solution/project** (not loose files) — the engine and the Roslyn workspace both key off what's open in VS, not the CLI's working directory if they differ.
- **`profile:true` is deferred.** `TestHostMode.Profile` needs a Diagnostics-Hub `ProfilerToolId` that isn't wired yet; the tool returns an honest note rather than a silent cancel. Coverage works.
- **`measureRate` under-samples on churn.** The engine cancels rapid back-to-back runs; those are retried and never counted as passes, but a long rate measurement can hit its budget under-sampled (surfaced in the result). The clean fix — waiting on `IOperationState` for engine-idle between runs — is a follow-up.
- **`vs_catch_flaky` needs the drive toggle** and, for a bare assertion with no framework hint, an explicit `exception`. It leaves the session **paused** on a catch (by design) with break-on-thrown still armed — clear it with `vs_break_on_thrown enabled:false` after inspecting.
- **`vs_debug_test`/`vs_catch_flaky` are managed-debugger only** and target first-chance exceptions; they don't catch a silent wrong-value assertion that never throws (use `vs_run_test` + `vs_debug_test` at a breakpoint for those).

---

## Try it

Open [`demo/TestLab/TestLab.slnx`](../demo/TestLab) in Visual Studio 2026, Launch Claude Code, and:

1. Ask Claude to **run the tests** — watch it report the assertion diff and the exception, per test.
2. Ask it to **hunt the flaky one** (`Flaky_IntermittentThrow`) — it reproduces the ~1-in-3 failure and captures it.
3. Tick **Allow Claude to drive debugger**, then ask it to **catch that flaky test red-handed** — it loops under the debugger and pauses you at the throw with `$exception` live.

---

## Next

- **Run tests affected by a change** — `Scope.ForFile`/`ForSymbol` + the `vs-semantic` call-graph: "run the tests that touch the code I just edited."
- **Profiling** — wire the `ProfilerToolId` so `profile:true` runs under the CPU/allocation profiler.
- **Idle-wait between hunt runs** — replace the settle delay with an `IOperationState` engine-idle wait, so `measureRate` never under-samples.
