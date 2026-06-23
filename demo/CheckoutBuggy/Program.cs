namespace CheckoutBuggy;

public record LineItem(string Name, decimal Price, int Quantity);

public static class Checkout
{
    /// <summary>Sum of price * quantity across the cart. This part is correct.</summary>
    public static decimal Subtotal(IReadOnlyList<LineItem> items)
    {
        decimal sum = 0m;
        foreach (var item in items)
            sum += item.Price * item.Quantity;
        return sum;
    }

    /// <summary>
    /// BUG (deliberate, runtime-only)
    /// </summary>
    public static decimal ApplyDiscount(decimal subtotal, int discountPercent)
    {
        bool eligible = subtotal > 0m && discountPercent > 0;
        string label = $"{discountPercent}% off";
        int rawPercent = discountPercent;

        decimal factor = rawPercent / 100;  
        decimal discount = eligible ? subtotal * factor : 0m; 
        decimal expected = subtotal * (discountPercent / 100m);

        Console.WriteLine($"  [discount] {label}: factor={factor}, got {discount:C}, expected {expected:C}");
        return discount;
    }

    public static decimal ApplyTax(decimal amount, int taxPercent)
    {
        decimal factor = 1m + (decimal)taxPercent / 100m;
        return amount * factor;
    }

    public static decimal GrandTotal(IReadOnlyList<LineItem> cart, int discountPercent, int taxPercent)
    {
        decimal subtotal = Subtotal(cart);
        decimal discount = ApplyDiscount(subtotal, discountPercent);
        decimal taxed = ApplyTax(subtotal - discount, taxPercent);
        return taxed;
    }
}

public static class Program
{
    public static void Main()
    {
        var cart = new List<LineItem>
        {
            new("Mechanical Keyboard", 89.99m, 1),
            new("USB-C Cable", 12.50m, 3),
            new("Laptop Stand", 34.00m, 2),
        };

        // Subtotal = 89.99 + 37.50 + 68.00 = 195.49
        // Expected: 10% off -> 175.94, +8% tax -> ~190.02
        decimal total = Checkout.GrandTotal(cart, discountPercent: 10, taxPercent: 8);

        Console.WriteLine($"Items in cart : {cart.Count}");
        Console.WriteLine($"Grand total   : {total:C}");
        Console.WriteLine("(Expected ~$190.02 with the 10% discount applied.)");
    }
}
