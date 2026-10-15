namespace Calcpad.Core.Numerics;

/// <summary>
/// Recursive descent parser for the calculator language. Operator precedence, right associative
/// powers, postfix factorial/percent, implicit multiplication, assignments, semicolon separated
/// statements and the unit conversion operator are all handled here; there is no access to
/// <c>DataTable</c> or any other expression evaluator.
/// </summary>
internal sealed class Parser
{
    private const int MaxDepth = 96;

    private readonly List<Token> _tokens;
    private int _index;
    private int _depth;

    private Parser(List<Token> tokens) => _tokens = tokens;

    /// <summary>Parses a token stream into an AST.</summary>
    public static Node Parse(List<Token> tokens) => new Parser(tokens).ParseProgram();

    private Token Current => _tokens[_index];

    private bool AtEnd => Current.Kind == TokenKind.End;

    private Token Peek(int offset = 1) => _tokens[Math.Min(_index + offset, _tokens.Count - 1)];

    private Token Advance() => _tokens[_index++];

    private Node ParseProgram()
    {
        var statements = new List<Node>();
        while (!AtEnd)
        {
            if (Current.Kind == TokenKind.Semicolon)
            {
                _index++;
                continue;
            }

            statements.Add(ParseStatement());

            if (Current.Kind == TokenKind.Semicolon)
            {
                _index++;
                continue;
            }

            if (AtEnd)
            {
                break;
            }

            if (Current.Kind == TokenKind.RightParen)
            {
                throw new CalcException(Errors.UnbalancedParen);
            }

            throw new CalcException(Errors.UnexpectedToken(Current.Text));
        }

        if (statements.Count == 0)
        {
            throw new CalcException(Errors.Empty);
        }

        return statements.Count == 1 ? statements[0] : new SequenceNode(statements);
    }

    private Node ParseStatement()
    {
        if (Current.Kind == TokenKind.Identifier && Peek().Kind == TokenKind.Assign)
        {
            var name = Advance().Text;
            _index++; // '='
            var value = ParseConvert();
            return new AssignNode(name, value);
        }

        return ParseConvert();
    }

    private Node ParseConvert()
    {
        var node = ParseAdditive();
        while (Current.Kind == TokenKind.Convert)
        {
            _index++;
            var target = ParseAdditive();
            node = new ConvertNode(node, target);
        }

        return node;
    }

    private Node ParseAdditive()
    {
        var node = ParseMultiplicative();
        while (Current.IsOperator("+") || Current.IsOperator("-"))
        {
            var op = Advance().Text;
            node = new BinaryNode(op, node, ParseMultiplicative());
        }

        return node;
    }

    private Node ParseMultiplicative()
    {
        var node = ParseImplicitProduct();
        while (true)
        {
            if (Current.IsOperator("*") || Current.IsOperator("/"))
            {
                var op = Advance().Text;
                node = new BinaryNode(op, node, ParseImplicitProduct());
                continue;
            }

            if (Current.IsOperator("%") && StartsOperand(Peek()))
            {
                _index++;
                node = new BinaryNode("%", node, ParseImplicitProduct());
                continue;
            }

            break;
        }

        return node;
    }

    /// <summary>
    /// Implicit multiplication: 2(3+4), 2π, 3x, 2sin(30). It binds tighter than '*' and '/', so
    /// <c>1 m / 1 s</c> is <c>(1 m) / (1 s)</c> and <c>2 kg * 3</c> stays <c>(2 kg) * 3</c>.
    /// </summary>
    private Node ParseImplicitProduct()
    {
        var node = ParseUnary();
        while (StartsOperand(Current))
        {
            node = new BinaryNode("*", node, ParseUnary(), implicitMultiply: true);
        }

        return node;
    }

    private Node ParseUnary()
    {
        EnterDepth();
        try
        {
            if (Current.IsOperator("-"))
            {
                _index++;
                return new UnaryNode("-", ParseUnary());
            }

            if (Current.IsOperator("+"))
            {
                _index++;
                return ParseUnary();
            }

            if (Current.Kind == TokenKind.Sqrt)
            {
                _index++;
                return new UnaryNode("√", ParseUnary());
            }

            if (Current.Kind == TokenKind.Cbrt)
            {
                _index++;
                return new UnaryNode("∛", ParseUnary());
            }

            return ParsePower();
        }
        finally
        {
            _depth--;
        }
    }

    private Node ParsePower()
    {
        var node = ParsePostfix();
        if (Current.IsOperator("^"))
        {
            _index++;
            // Right associative, and the exponent may itself be signed: 2^-3.
            return new BinaryNode("^", node, ParseUnary());
        }

        return node;
    }

    private Node ParsePostfix()
    {
        var node = ParsePrimary();
        while (true)
        {
            if (Current.IsOperator("!"))
            {
                _index++;
                node = new PostfixNode("!", node);
                continue;
            }

            if (Current.Kind == TokenKind.Square)
            {
                _index++;
                node = new PostfixNode("²", node);
                continue;
            }

            if (Current.Kind == TokenKind.Cube)
            {
                _index++;
                node = new PostfixNode("³", node);
                continue;
            }

            // '%' is percent when nothing that can start an operand follows, modulo otherwise.
            if (Current.IsOperator("%") && !StartsOperand(Peek()))
            {
                _index++;
                node = new PostfixNode("%", node);
                continue;
            }

            break;
        }

        return node;
    }

    private Node ParsePrimary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.Number:
                _index++;
                return new NumberNode(token.Number);

            case TokenKind.LeftParen:
                {
                    _index++;
                    EnterDepth();
                    try
                    {
                        if (Current.Kind == TokenKind.RightParen)
                        {
                            throw new CalcException(Errors.UnexpectedToken(")"));
                        }

                        var inner = ParseConvert();
                        if (Current.Kind != TokenKind.RightParen)
                        {
                            throw new CalcException(Errors.UnbalancedParen);
                        }

                        _index++;
                        return inner;
                    }
                    finally
                    {
                        _depth--;
                    }
                }

            case TokenKind.Identifier:
                _index++;
                if (Current.Kind == TokenKind.LeftParen)
                {
                    return ParseCall(token.Text);
                }

                return new IdentifierNode(token.Text);

            case TokenKind.RightParen:
                throw new CalcException(Errors.UnbalancedParen);

            case TokenKind.Comma:
            case TokenKind.Assign:
            case TokenKind.Convert:
            case TokenKind.Semicolon:
                throw new CalcException(Errors.UnexpectedToken(token.Text));

            case TokenKind.End:
                throw new CalcException(Errors.UnexpectedEnd);

            default:
                throw new CalcException(Errors.UnexpectedToken(token.Text));
        }
    }

    private Node ParseCall(string name)
    {
        _index++; // '('
        EnterDepth();
        try
        {
            var arguments = new List<Node>();
            if (Current.Kind != TokenKind.RightParen)
            {
                while (true)
                {
                    arguments.Add(ParseConvert());
                    if (Current.Kind == TokenKind.Comma)
                    {
                        _index++;
                        continue;
                    }

                    break;
                }
            }

            if (Current.Kind != TokenKind.RightParen)
            {
                throw new CalcException(Errors.UnbalancedParen);
            }

            _index++;
            return new CallNode(name, arguments);
        }
        finally
        {
            _depth--;
        }
    }

    private static bool StartsOperand(Token token) => token.Kind
        is TokenKind.Number
        or TokenKind.Identifier
        or TokenKind.LeftParen
        or TokenKind.Sqrt
        or TokenKind.Cbrt;

    private void EnterDepth()
    {
        _depth++;
        if (_depth > MaxDepth)
        {
            throw new CalcException(Errors.TooDeep);
        }
    }
}
