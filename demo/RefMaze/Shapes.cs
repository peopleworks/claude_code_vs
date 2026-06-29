namespace RefMaze;

/// <summary>The interface every shape implements. The hub of the reference maze.</summary>
public interface IShape
{
    string Name { get; }
    double Area();
}

/// <summary>
/// A base some shapes share. Splits the hierarchy: Circle/Square derive from THIS (and get IShape
/// transitively), while Triangle implements IShape directly. So type_hierarchy has real shape.
/// </summary>
public abstract class ShapeBase : IShape
{
    public abstract string Name { get; }
    public abstract double Area();
    public override string ToString() => $"{Name} ({Area():0.##})";
}

public sealed class Circle : ShapeBase
{
    private readonly double _r;
    public Circle(double r) => _r = r;
    public override string Name => "Circle";
    public override double Area() => System.Math.PI * _r * _r;
}

public sealed class Square : ShapeBase
{
    private readonly double _s;
    public Square(double s) => _s = s;
    public override string Name => "Square";
    public override double Area() => _s * _s;
}

/// <summary>
/// Implements IShape DIRECTLY and via an EXPLICIT interface implementation for Area(). A text search for
/// ".Area()" call sites won't connect this member to IShape.Area - but vs_find_implementations(IShape.Area)
/// will, and vs_find_references on it sees the interface-dispatched call in SummarizeAll.
/// </summary>
public sealed class Triangle : IShape
{
    private readonly double _b, _h;
    public Triangle(double b, double h) { _b = b; _h = h; }
    public string Name => "Triangle";
    double IShape.Area() => 0.5 * _b * _h;   // explicit interface implementation
}
