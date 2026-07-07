# DataWatch — managed data-breakpoint fixture (watch a field, conditionally)

A fixture for the extension's toughest debugger integration: **managed data breakpoints** — "break when
this field changes" — including **conditional** ("break only when the new value matches") and **recurring**
("stop on every matching change") watches. Visual Studio's own UI can set these, but there is *no*
automation API for them, so the extension arms them through a bundled Concord debug-engine component. This
fixture gives that capability a clean target.

**The bug:** `Program.Main` prices one order by mutating `order.TotalCents` through four steps — add line
items, apply a bulk discount, apply tax, apply a loyalty credit. The final total comes out **negative**
(`($475.12)`), but the printed number never tells you *which* step corrupted it. The culprit is
`ApplyLoyaltyCredit`: a unit mix-up (100 points = $1.00, so 1 point = 1 cent) multiplies by 100 and treats
each **point as a dollar**, subtracting **$1000.00** instead of $10.00.

## Why a data breakpoint (and not a line breakpoint)

Each of the four steps writes `order.TotalCents` from a **different method**, so there is no single line to
put a breakpoint on — you'd have to guess which of four writes went wrong, or breakpoint all of them and
step. A data breakpoint watches the **field's memory**, so the debugger stops on **whoever writes it,
wherever that write lives**. That's the whole point: you don't need to know where the bug is to catch it.

## Engine constraints this fixture respects

- **Instance fields only.** `TotalCents` and `Writes` are **public instance fields** on the `Order` class.
  The engine watches a field's address — properties (no stable address), locals, and statics are not
  supported.
- **≤ 8 bytes.** `TotalCents` is a `long` (8 bytes), not a `decimal` (16 bytes). Managed data breakpoints
  watch through hardware debug registers, which top out at 8 bytes — so money is stored as **integer
  cents**. (A `decimal` field can silently miss high-order changes.)
- **x64, .NET Core 3.0+.** The project pins `<PlatformTarget>x64</PlatformTarget>` and targets `net8.0`.
- **The stop lands one statement *after* the write** — a data breakpoint fires once the write completes, so
  execution halts on the line following the mutation (e.g. on `order.Writes++;`).

## Run it

1. Open **`demo/DataWatch/DataWatch.slnx`** in VS 2026 (**Debug**, x64, so symbols are present).
2. Claude Code panel → tick **Allow Claude to drive debugger** (data breakpoints arm from a paused,
   driven session) → **Launch Claude Code**.
3. Put a breakpoint on the marked line in `Main` (the `Console.WriteLine($"Pricing order …")` line) and
   press **F5**. Execution stops there with `order` constructed and in scope — the point you arm from.

Sanity check without the debugger: `dotnet run --project demo/DataWatch` prints
`Final total for SO-4471: ($475.12)  (6 writes)` and the expected `$514.88`.

## The four demos

Paused at the marked line, ask Claude to drive. Each call returns a `requestId` you read changes from.

### A. Plain watch + the change timeline
> Set a data breakpoint on `order.TotalCents`, continue, and show me every value it takes.

- `vs_set_data_breakpoint("order.TotalCents")` → arms the watch.
- `vs_continue` → stops on each write (one statement after it).
- `vs_get_data_changes(requestId)` → the full `[{previous, current}]` timeline, in cents:

  `0 → 30000 → 42000 → 54000 → 48600 → 52488 → -47512`

  That last jump (`52488 → -47512`, i.e. **$524.88 → −$475.12**) is the corruption, and the frame it
  breaks in is `ApplyLoyaltyCredit`.

### B. Conditional — pinpoint the bug (the headline)
> Break only when `order.TotalCents` goes negative.

- `vs_set_data_breakpoint("order.TotalCents", condition: "< 0")` → skips all five legitimate writes and
  **breaks exactly on the one write that drives the total negative** — inside `ApplyLoyaltyCredit`, with
  `creditCents == 100000` and `loyaltyPoints == 1000` live in the frame. The bug, located in one shot.

### C. Recurring — stop on each matching change
> Break on every change where `order.TotalCents` is over $400.00.

- `vs_set_data_breakpoint("order.TotalCents", condition: "> 40000", stopOnChange: true)` → breaks on **each
  of the four** writes above `40000` (the 2nd and 3rd line items `42000`/`54000`, the discount `48600`, and
  the tax `52488`), skipping the first line item (`30000`) and the negative final value. Demonstrates a
  recurring conditional stop, not a one-shot.

### D. Multi-watch — two fields at once
> Also watch `order.Writes` at the same time.

- Arm a second `vs_set_data_breakpoint("order.Writes")` while the `TotalCents` watch is still live. Both
  fire independently (the engine fans out per address), so you see the write **counter** tick `1 → … → 6`
  alongside the total — two live watches on one object.

Disarm any watch with `vs_remove_data_breakpoint(requestId)` when done.

## PASS / FAIL

- **PASS** = demo B breaks **inside `ApplyLoyaltyCredit`** (not at a guessed line, not in `Main`), the
  change timeline in demo A shows the `52488 → -47512` jump, and demo C stops multiple times.
- **FAIL** = the watch never breaks (check the build is **x64 Debug** and the field is the **instance
  field** `order.TotalCents`, not a property), or only the first change is reported (a sign the watched
  field exceeded 8 bytes — it shouldn't here, `TotalCents` is a `long`).

## The fix (don't apply unless you're testing the diff)

```diff
- long creditCents = loyaltyPoints * 100L;   // treats each point as a dollar
+ long creditCents = loyaltyPoints;          // 1 point = 1 cent; 1000 points = $10.00
```

(The fixture stays buggy so it's reusable.)
