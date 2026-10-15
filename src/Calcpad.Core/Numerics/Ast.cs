namespace Calcpad.Core.Numerics;

/// <summary>
/// Base class of the abstract syntax tree produced by <see cref="Parser"/>. Nodes render themselves
/// back to text, optionally with some sub-expressions replaced by their values, which is how the
/// Math Notes step list shows the reduction from the original expression to the result.
/// </summary>
internal abstract class Node
{
    /// <summary>Binding strength, used to decide where parentheses are needed when printing.</summary>
    public abstract int Precedence { get; }

    /// <summary>Renders this node, replacing any node present in <paramref name="subs"/> by its value.</summary>
    public string Render(IReadOnlyDictionary<Node, string>? subs = null)
    {
        if (subs is not null && subs.TryGetValue(this, out var text))
        {
            return text;
        }

        return RenderCore(subs);
    }

    /// <summary>Renders this node, wrapping it in parentheses when its precedence is too low.</summary>
    public string RenderMin(int min, IReadOnlyDictionary<Node, string>? subs = null)
    {
        var text = Render(subs);
        return Precedence < min ? $"({text})" : text;
    }

    /// <summary>Node specific rendering; <paramref name="subs"/> must already have been consulted.</summary>
    protected abstract string RenderCore(IReadOnlyDictionary<Node, string>? subs);
}

/// <summary>A numeric literal.</summary>
internal sealed class NumberNode : Node
{
    public NumberNode(double value) => Value = value;

    /// <summary>The literal value.</summary>
    public double Value { get; }

    /// <inheritdoc />
    public override int Precedence => 6;

    /// <inheritdoc />
    protected override string RenderCore(IReadOnlyDictionary<Node, string>? subs) =>
        NumberFormatter.Format(Value, 15);
}

/// <summary>A named constant such as <c>π</c> or <c>e</c>.</summary>
internal sealed class ConstantNode : Node
{
    public ConstantNode(string name, double value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>Name as typed.</summary>
    public string Name { get; }

    /// <summary>Numeric value.</summary>
    public double Value { get; }

    /// <inheritdoc />
    public override int Precedence => 6;

    /// <inheritdoc />
    protected override string RenderCore(IReadOnlyDictionary<Node, string>? subs) => Name;
}

/// <summary>A name: a variable, a unit or a dimensionless constant looked up at evaluation time.</summary>
internal sealed class IdentifierNode : Node
{
    public IdentifierNode(string name) => Name = name;

    /// <summary>Name as typed.</summary>
    public string Name { get; }

    /// <inheritdoc />
    public override int Precedence => 6;

    /// <inheritdoc />
    protected override string RenderCore(IReadOnlyDictionary<Node, string>? subs) => Name;
}

/// <summary>A prefix operator: <c>-</c>, <c>+</c>, <c>√</c> or <c>∛</c>.</summary>
internal sealed class UnaryNode : Node
{
    public UnaryNode(string op, Node operand)
    {
        Op = op;
        Operand = operand;
    }

    /// <summary>The operator.</summary>
    public string Op { get; }

    /// <summary>The operand.</summary>
    public Node Operand { get; }

    /// <inheritdoc />
    public override int Precedence => 3;

    /// <inheritdoc />
    protected override string RenderCore(IReadOnlyDictionary<Node, string>? subs) => Op switch
    {
        "√" => $"√{Operand.RenderMin(3, subs)}",
        "∛" => $"∛{Operand.RenderMin(3, subs)}",
        _ => $"{Op}{Operand.RenderMin(3, subs)}"
    };
}

/// <summary>A postfix operator: factorial, percent, or the keypad superscripts.</summary>
internal sealed class PostfixNode : Node
{
    public PostfixNode(string op, Node operand)
    {
        Op = op;
        Operand = operand;
    }

    /// <summary>The operator.</summary>
    public string Op { get; }

    /// <summary>The operand.</summary>
    public Node Operand { get; }

    /// <inheritdoc />
    public override int Precedence => 5;

    /// <inheritdoc />
    protected override string RenderCore(IReadOnlyDictionary<Node, string>? subs) =>
        $"{Operand.RenderMin(5, subs)}{Op}";
}

/// <summary>A binary operator: <c>+ - * / ^ %</c>.</summary>
internal sealed class BinaryNode : Node
{
    public BinaryNode(string op, Node left, Node right, bool implicitMultiply = false)
    {
        Op = op;
        Left = left;
        Right = right;
        ImplicitMultiply = implicitMultiply;
    }

    /// <summary>The operator.</summary>
    public string Op { get; }

    /// <summary>Left operand.</summary>
    public Node Left { get; }

    /// <summary>Right operand.</summary>
    public Node Right { get; }

    /// <summary>True when the multiplication was written implicitly, as in <c>2π</c> or <c>3x</c>.</summary>
    public bool ImplicitMultiply { get; }

    /// <inheritdoc />
    public override int Precedence => Op switch
    {
        "|" => 0,
        "&" => 1,
        "+" or "-" => 2,
        "*" or "/" or "%" => 3,
        "^" => 4,
        _ => 3
    };

    /// <inheritdoc />
    protected override string RenderCore(IReadOnlyDictionary<Node, string>? subs)
    {
        var precedence = Precedence;
        var left = Left.RenderMin(precedence, subs);
        var right = Op switch
        {
            "^" => Right.RenderMin(precedence, subs),
            _ => Right.RenderMin(precedence + 1, subs)
        };

        if (Op == "^")
        {
            return $"{left}^{right}";
        }

        var symbol = Op == "*" && ImplicitMultiply ? "·" : Op;
        return $"{left} {symbol} {right}";
    }
}

/// <summary>A function call such as <c>sin(30)</c> or <c>nCr(5, 2)</c>.</summary>
internal sealed class CallNode : Node
{
    public CallNode(string name, IReadOnlyList<Node> arguments)
    {
        Name = name;
        Arguments = arguments;
    }

    /// <summary>Function name.</summary>
    public string Name { get; }

    /// <summary>Arguments in source order.</summary>
    public IReadOnlyList<Node> Arguments { get; }

    /// <inheritdoc />
    public override int Precedence => 6;

    /// <inheritdoc />
    protected override string RenderCore(IReadOnlyDictionary<Node, string>? subs)
    {
        var rendered = new string[Arguments.Count];
        for (var i = 0; i < Arguments.Count; i++)
        {
            rendered[i] = Arguments[i].Render(subs);
        }

        return $"{Name}({string.Join(", ", rendered)})";
    }
}

/// <summary>A variable assignment, <c>x = 3</c>.</summary>
internal sealed class AssignNode : Node
{
    public AssignNode(string name, Node value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>Variable name.</summary>
    public string Name { get; }

    /// <summary>Assigned expression.</summary>
    public Node Value { get; }

    /// <inheritdoc />
    public override int Precedence => 0;

    /// <inheritdoc />
    protected override string RenderCore(IReadOnlyDictionary<Node, string>? subs) =>
        $"{Name} = {Value.RenderMin(1, subs)}";
}

/// <summary>Unit conversion, <c>3 m to cm</c>.</summary>
internal sealed class ConvertNode : Node
{
    public ConvertNode(Node value, Node target)
    {
        Value = value;
        Target = target;
    }

    /// <summary>Expression being converted.</summary>
    public Node Value { get; }

    /// <summary>Target unit expression.</summary>
    public Node Target { get; }

    /// <inheritdoc />
    public override int Precedence => 0;

    /// <inheritdoc />
    protected override string RenderCore(IReadOnlyDictionary<Node, string>? subs) =>
        $"{Value.RenderMin(1, subs)} to {Target.RenderMin(1, subs)}";
}

/// <summary>A sequence of statements separated by semicolons.</summary>
internal sealed class SequenceNode : Node
{
    public SequenceNode(IReadOnlyList<Node> statements) => Statements = statements;

    /// <summary>The statements; empty for an empty expression.</summary>
    public IReadOnlyList<Node> Statements { get; }

    /// <inheritdoc />
    public override int Precedence => 0;

    /// <inheritdoc />
    protected override string RenderCore(IReadOnlyDictionary<Node, string>? subs)
    {
        var rendered = new string[Statements.Count];
        for (var i = 0; i < Statements.Count; i++)
        {
            rendered[i] = Statements[i].Render(subs);
        }

        return string.Join("; ", rendered);
    }
}
