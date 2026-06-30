namespace RefMaze;

internal static class Program
{
    private static void Main()
    {
        IShape[] shapes = { new Circle(2), new Square(3), new Triangle(4, 5) };

        Report(shapes);
        foreach (var s in shapes)
            System.Console.WriteLine(Describe(s));
        System.Console.WriteLine(Describe(shapes[0], verbose: true));
    }

    // An overload SET. go_to_definition on a Describe(...) call must resolve to the RIGHT one of these two,
    // where a text search for "Describe" returns both (plus this declaration).
    private static string Describe(IShape shape) => shape.Name;

    private static string Describe(IShape shape, bool verbose) =>
        verbose ? $"{shape.Name}: total-with-self={SummarizeAll(new[] { shape }):0.##}" : shape.Name;

    private static void Report(IShape[] shapes) =>
        System.Console.WriteLine($"total area = {SummarizeAll(shapes):0.##}");

    // The bottom of the call chain Main -> Report -> SummarizeAll -> IShape.Area().
    // vs_call_hierarchy(callers) of Area climbs back up this; SummarizeAll is also called from Describe.
    private static double SummarizeAll(IShape[] shapes)
    {
        double total = 0;
        foreach (var s in shapes)
            total += s.Area();   // interface-dispatched: reaches Circle/Square/Triangle Area() implementations
        return total;
    }
}
