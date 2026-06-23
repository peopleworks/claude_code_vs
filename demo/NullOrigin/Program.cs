using System;
using System.Collections.Generic;

namespace NullOrigin;

public sealed class Customer
{
    public string Name { get; set; } = "";
    public string Tier { get; set; } = "";
}

public sealed class Order
{
    public int Id { get; set; }
    public Customer Customer { get; set; }   // may be null for some orders
}

public static class Pricing
{
    private const decimal Base = 100m;

    public static decimal QuoteFor(Order order)
    {
        decimal discount = order.Customer.Tier == "Gold" ? 0.20m : 0m;
        return Base * (1 - discount);
    }
}

public static class Program
{
    public static void Main()
    {
        var orders = new List<Order>
        {
            new Order { Id = 101, Customer = new Customer { Name = "Acme",    Tier = "Gold"   } },
            new Order { Id = 102, Customer = new Customer { Name = "Globex",  Tier = "Silver" } },
            new Order { Id = 103, Customer = null },
            new Order { Id = 104, Customer = new Customer { Name = "Initech", Tier = "Gold"   } },
        };

        decimal total = 0;
        int priced = 0, skipped = 0;
        foreach (var order in orders)
        {
            try
            {
                total += Pricing.QuoteFor(order);
                priced++;
            }
            catch (Exception e)
            {
                skipped++;
                Console.WriteLine($"Order {order.Id}: skipped ({e.GetType().Name})");
            }
        }

        Console.WriteLine($"Priced {priced} orders, {skipped} skipped, total {total:C}");
    }
}
