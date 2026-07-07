namespace DataWatch
{
    // DataWatch - a fixture for MANAGED DATA BREAKPOINTS ("break when this field changes"), including
    // CONDITIONAL ("break only when the new value matches") and RECURRING ("stop on every matching
    // change") watches - the toughest debugger integration in this extension.
    //
    // THE MYSTERY: order.TotalCents ends up wrong (negative), but the final number never tells you WHICH
    // of the four pricing steps corrupted it - and each step writes the field from a DIFFERENT method,
    // so there is no single source line to breakpoint. That is exactly the case a data breakpoint owns:
    // you watch the FIELD, not a line, and the debugger stops on whoever writes it, wherever that is.
    //
    // DEMO (Allow Claude to drive debugger = ON, x64 Debug build):
    //   1. Break at the marked line in Main (order is constructed and in scope).
    //   2. Plain watch:   vs_set_data_breakpoint("order.TotalCents")
    //                     -> stops on every write; vs_get_data_changes(id) shows the full
    //                        previous->current timeline: 0 -> 30000 -> 42000 -> 54000 -> 48600
    //                        -> 52488 -> -47512.  The last jump is the corruption.
    //   3. Conditional:   vs_set_data_breakpoint("order.TotalCents", condition: "< 0")
    //                     -> skips the five legit writes and breaks EXACTLY on the step that drives the
    //                        total negative (ApplyLoyaltyCredit) - the bug, pinpointed.
    //   4. Recurring:     vs_set_data_breakpoint("order.TotalCents", condition: "> 40000",
    //                                             stopOnChange: true)
    //                     -> breaks on EACH of the four writes over $400.00 (the 2nd and 3rd line items,
    //                        the discount, and the tax), demonstrating a recurring conditional stop.
    //   5. Multi-watch:   also arm vs_set_data_breakpoint("order.Writes") at the same time - two live
    //                        watches on one object via the engine's per-address fan-out.
    //
    // ENGINE CONSTRAINTS honored here: TotalCents and Writes are PUBLIC INSTANCE FIELDS (the engine
    // watches a field's memory address - properties, locals, and statics are unsupported); each is
    // <= 8 bytes (long / int - a decimal is 16 bytes and exceeds the hardware data-watch limit, which is
    // why money is stored as integer CENTS here); the build is x64; the runtime is net8.0 (>= Core 3.0).
    // The stop lands ONE statement AFTER the write (a data breakpoint fires once the write completes).

    public sealed class Order
    {
        public string Id;
        public long TotalCents;   // <-- the watched field. PUBLIC INSTANCE FIELD, 8 bytes, on purpose.
        public int Writes;        // a second watchable field, for the multi-watch demo.
    }

    internal static class Program
    {
        private static void Main()
        {
            var order = new Order { Id = "SO-4471", TotalCents = 0, Writes = 0 };

            // >>> DEMO: put a breakpoint on the next line and press F5. `order` is constructed and in
            // >>> scope here, so you can arm vs_set_data_breakpoint("order.TotalCents") while paused,
            // >>> then continue to catch the writes. (Data breakpoints arm from a paused session; the
            // >>> "Allow Claude to drive debugger" toggle must be ON.)
            Console.WriteLine($"Pricing order {order.Id}...");

            AddItems(order);            // TotalCents: 0 -> 30000 -> 42000 -> 54000   (three line items)
            ApplyBulkDiscount(order);   //             54000 -> 48600                (10% off, correct)
            ApplyTax(order);            //             48600 -> 52488                (8% tax, correct)
            ApplyLoyaltyCredit(order);  //             52488 -> -47512               (BUG: corrupts total)

            Console.WriteLine($"Final total for {order.Id}: {Money(order.TotalCents)}  ({order.Writes} writes)");
            Console.WriteLine($"(Expected {Money(51488)}. A negative total means a step overwrote it wrongly.)");
        }

        // Three line items. Each writes order.TotalCents from INSIDE this method (not Main), so a line
        // breakpoint would have to be guessed here - a data breakpoint catches it without knowing where.
        private static void AddItems(Order order)
        {
            (string name, long unitCents, int qty)[] items =
            {
                ("Standing desk", 30000, 1),   // $300.00
                ("Monitor arm",    6000, 2),   // $60.00
                ("Cable tray",     3000, 4),   // $30.00
            };
            foreach (var (_, unitCents, qty) in items)
            {
                order.TotalCents += unitCents * qty;   // writes: 30000, then 42000, then 54000
                order.Writes++;
            }
        }

        private static void ApplyBulkDiscount(Order order)
        {
            long discount = order.TotalCents / 10;   // 10% off - correct
            order.TotalCents -= discount;            // 54000 -> 48600
            order.Writes++;
        }

        private static void ApplyTax(Order order)
        {
            long tax = order.TotalCents * 8 / 100;   // +8% tax - correct
            order.TotalCents += tax;                 // 48600 -> 52488  ($524.88)
            order.Writes++;
        }

        private static void ApplyLoyaltyCredit(Order order)
        {
            // BUG (deliberate, unit confusion): 100 loyalty points == $1.00, so 1 point == 1 cent and the
            // $10.00 credit should subtract `loyaltyPoints` cents. This multiplies by 100 - treating each
            // POINT as a whole DOLLAR - so it subtracts $1000.00 instead of $10.00 and drives the total
            // negative. The final output only shows a wrong (negative) number; nothing says THIS method,
            // or this line, did it. That is what the conditional data breakpoint ("< 0") pins down.
            int loyaltyPoints = 1000;                    // 1000 points = $10.00 credit
            long creditCents = loyaltyPoints * 100L;     // BUG: should be `loyaltyPoints` (1 point = 1 cent)
            order.TotalCents -= creditCents;             // 52488 - 100000 = -47512  (should be 52488 - 1000)
            order.Writes++;
        }

        private static string Money(long cents) => (cents / 100m).ToString("C");
    }
}
