using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Unicode;
using Vorticity.Compute;

namespace Vorticity.Expressions;

/// <summary>
/// The filter grammar, printed and parsed:
/// <code>
/// expr      := and ( "or" and )*
/// and       := unary ( "and" unary )*
/// unary     := "not" unary | "(" expr ")" | predicate
/// predicate := field op ( literal | field )
///            | field ["not"] "in" "(" literal ( "," literal )* ")"
///            | field "is" ["not"] "null"
///            | field "like" text [ "escape" text ]
///            | field "starts" "with" text
///            | field "contains" literal
/// op        := "=" | "!=" | "&lt;&gt;" | "&lt;" | "&lt;=" | "&gt;" | "&gt;="
/// literal   := 'text' | number | true | false | null
/// field     := name ( "." name )*   where a name is an identifier or a "double-quoted" string
/// </code>
/// Keywords are case-insensitive; a quote inside a quoted text or name is doubled.
/// </summary>
internal static class ExprText
{
    internal static string Format(VortexExpr expr)
    {
        StringBuilder text = new StringBuilder();
        Append(text, expr, parentIsAnd: null);
        return text.ToString();
    }

    internal static VortexExpr Parse(ReadOnlySpan<char> text) => Parse(text, null);

    /// <summary>Parses <paramref name="text"/>, where <c>?N</c> stands for the literal <paramref name="parameters"/>[N].</summary>
    internal static VortexExpr Parse(ReadOnlySpan<char> text, IReadOnlyList<FilterLiteral>? parameters)
    {
        List<Token> tokens = Tokenize(text, parameters);
        int at = 0;
        VortexExpr expr = ParseOr(tokens, ref at, 0);
        if (at != tokens.Count)
        {
            throw new FormatException($"Unexpected '{tokens[at].Text}' at position {tokens[at].Position} of the filter.");
        }

        return expr;
    }

    private static void Append(StringBuilder text, VortexExpr expr, bool? parentIsAnd)
    {
        switch (expr)
        {
            case LogicalExpr logical:
                bool wrap = parentIsAnd is { } parent && parent != logical.IsAnd;
                if (wrap)
                {
                    text.Append('(');
                }

                Append(text, logical.Left, logical.IsAnd);
                text.Append(logical.IsAnd ? " and " : " or ");
                Append(text, logical.Right, logical.IsAnd);
                if (wrap)
                {
                    text.Append(')');
                }

                return;
            case NotExpr negation:
                text.Append("not (");
                Append(text, negation.Operand, parentIsAnd: null);
                text.Append(')');
                return;
            case ComparisonExpr comparison:
                AppendField(text, comparison.Field);
                text.Append(' ').Append(Operator(comparison.Op)).Append(' ');
                AppendLiteral(text, comparison.Value);
                return;
            case ColumnComparisonExpr columns:
                AppendField(text, columns.Left);
                text.Append(' ').Append(Operator(columns.Op)).Append(' ');
                AppendField(text, columns.Right);
                return;
            case NullCheckExpr check:
                AppendField(text, check.Field);
                text.Append(check.IsNull ? " is null" : " is not null");
                return;
            case InExpr membership:
                AppendField(text, membership.Field);
                text.Append(" in (");
                for (int i = 0; i < membership.Literals.Length; i++)
                {
                    if (i != 0)
                    {
                        text.Append(", ");
                    }

                    AppendLiteral(text, membership.Literals[i]);
                }

                text.Append(')');
                return;
            case StringMatchExpr match:
                AppendField(text, match.Field);
                text.Append(match.Op switch
                {
                    StringMatchOp.StartsWith => " starts with ",
                    StringMatchOp.Contains => " contains ",
                    _ => " like ",
                });
                AppendLiteral(text, match.Pattern);
                if (match.Op == StringMatchOp.Like && match.Escape != (byte)'\\')
                {
                    text.Append(" escape '").Append((char)match.Escape).Append('\'');
                }

                return;
            case ListContainsExpr contains:
                AppendField(text, contains.Field);
                text.Append(" contains ");
                AppendLiteral(text, contains.Value);
                return;
            case FieldExpr field:
                AppendField(text, field);
                return;
            case LiteralExpr literal:
                AppendLiteral(text, literal.Value);
                return;
            default:
                text.Append(expr.Kind);
                return;
        }
    }

    private static string Operator(ComparisonOp op) => op switch
    {
        ComparisonOp.Equal => "=",
        ComparisonOp.NotEqual => "!=",
        ComparisonOp.Less => "<",
        ComparisonOp.LessOrEqual => "<=",
        ComparisonOp.Greater => ">",
        _ => ">=",
    };

    private static void AppendField(StringBuilder text, FieldExpr field)
    {
        string[] names = field.Segments ?? field.Path.Split('.');
        for (int i = 0; i < names.Length; i++)
        {
            if (i != 0)
            {
                text.Append('.');
            }

            string name = names[i];
            if (IsIdentifier(name) && !IsKeyword(name))
            {
                text.Append(name);
            }
            else
            {
                text.Append('"').Append(name.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
        }
    }

    private static void AppendLiteral(StringBuilder text, FilterLiteral literal)
    {
        switch (literal.Kind)
        {
            case FilterLiteralKind.Null:
                text.Append("null");
                return;
            case FilterLiteralKind.Bool:
                text.Append(literal.BoolValue ? "true" : "false");
                return;
            case FilterLiteralKind.Signed:
                text.Append(literal.SignedValue.ToString(CultureInfo.InvariantCulture));
                return;
            case FilterLiteralKind.Unsigned:
                text.Append(literal.UnsignedValue.ToString(CultureInfo.InvariantCulture));
                return;
            case FilterLiteralKind.Float:
                string number = literal.FloatValue.ToString("R", CultureInfo.InvariantCulture);
                text.Append(number);
                if (double.IsFinite(literal.FloatValue) && number.AsSpan().IndexOfAny('.', 'E', 'e') < 0)
                {
                    text.Append(".0");
                }

                return;
            default:
                ReadOnlySpan<byte> bytes = literal.BytesValue;
                if (Utf8.IsValid(bytes))
                {
                    text.Append('\'').Append(Encoding.UTF8.GetString(bytes).Replace("'", "''", StringComparison.Ordinal)).Append('\'');
                }
                else
                {
                    text.Append("x'").Append(Convert.ToHexString(bytes)).Append('\'');
                }

                return;
        }
    }

    private static VortexExpr ParseOr(List<Token> tokens, ref int at, int nesting)
    {
        VortexExpr left = ParseAnd(tokens, ref at, nesting);
        while (IsWord(tokens, at, "or"))
        {
            Token or = tokens[at++];
            VortexExpr right = ParseAnd(tokens, ref at, nesting);
            if (ExprDepth.TooDeep(left, right))
            {
                return ParseRun(isAnd: false, left, or.Position, right, tokens, ref at, nesting);
            }

            left = Expr.Logical(false, left, right);
        }

        return left;
    }

    private static VortexExpr ParseAnd(List<Token> tokens, ref int at, int nesting)
    {
        VortexExpr left = ParseUnary(tokens, ref at, nesting);
        while (IsWord(tokens, at, "and"))
        {
            Token and = tokens[at++];
            VortexExpr right = ParseUnary(tokens, ref at, nesting);
            if (ExprDepth.TooDeep(left, right))
            {
                return ParseRun(isAnd: true, left, and.Position, right, tokens, ref at, nesting);
            }

            left = Expr.Logical(true, left, right);
        }

        return left;
    }

    /// <summary>
    /// The rest of a run of one operator whose next node, at <paramref name="position"/>, would nest
    /// past the evaluator's depth as the run leans: the run so far taken apart into its operands,
    /// then <paramref name="right"/> and every operand after it, read whole and joined as a balanced
    /// tree of one node between two operands. When a deep operand would put that tree past the depth
    /// too, the run is joined one operand at a time as a join past the depth goes, and refused at
    /// the operator where even that goes too deep.
    /// </summary>
    private static VortexExpr ParseRun(
        bool isAnd, VortexExpr left, int position, VortexExpr right, List<Token> tokens, ref int at, int nesting)
    {
        string word = isAnd ? "and" : "or";
        VortexExpr[] operands = ArrayPool<VortexExpr>.Shared.Rent(128);
        int[] operators = ArrayPool<int>.Shared.Rent(16);
        int count = 0;
        int read = 0;
        try
        {
            Expr.Flatten(isAnd, left, ref operands, ref count);
            int taken = count;
            Expr.Append(ref operators, ref read, position);
            Expr.Append(ref operands, ref count, right);
            while (IsWord(tokens, at, word))
            {
                Expr.Append(ref operators, ref read, tokens[at++].Position);
                Expr.Append(ref operands, ref count, isAnd ? ParseUnary(tokens, ref at, nesting) : ParseAnd(tokens, ref at, nesting));
            }

            ReadOnlySpan<VortexExpr> run = operands.AsSpan(0, count);
            if (Expr.Balance(isAnd, run) is { } balanced)
            {
                return balanced;
            }

            // The operands taken apart were joined within the depth as the run leaned: one refused
            // among them is reported at the operator that brought the run here.
            VortexExpr joined = run[0];
            for (int i = 1; i < count; i++)
            {
                joined = ExprDepth.TooDeep(joined, run[i])
                    ? throw TooDeep(operators[Math.Max(i - taken, 0)])
                    : Expr.Joined(isAnd, joined, run[i]);
            }

            return joined;
        }
        finally
        {
            Array.Clear(operands, 0, count);
            ArrayPool<VortexExpr>.Shared.Return(operands);
            ArrayPool<int>.Shared.Return(operators);
        }
    }

    /// <remarks>
    /// Each <c>not</c> and each parenthesis recurses, so their nesting is bounded before the
    /// recursion: a filter nested deeper than the evaluator takes would otherwise exhaust the stack
    /// here, which ends the process.
    /// </remarks>
    private static VortexExpr ParseUnary(List<Token> tokens, ref int at, int nesting)
    {
        bool not = IsWord(tokens, at, "not");
        if (!not && !IsSymbol(tokens, at, "("))
        {
            return ParsePredicate(tokens, ref at);
        }

        Token opening = tokens[at++];
        if (nesting == FilterEvaluator.MaxDepth)
        {
            throw TooDeep(opening.Position);
        }

        if (not)
        {
            VortexExpr operand = ParseUnary(tokens, ref at, nesting + 1);
            return ExprDepth.TooDeep(operand, operand) ? throw TooDeep(opening.Position) : Expr.Not(operand);
        }

        VortexExpr inner = ParseOr(tokens, ref at, nesting + 1);
        Expect(tokens, ref at, ")");
        return inner;
    }

    private static FormatException TooDeep(int position) =>
        new FormatException($"The filter nests deeper than {FilterEvaluator.MaxDepth} levels at position {position}.");

    private static VortexExpr ParsePredicate(List<Token> tokens, ref int at)
    {
        FieldExpr field = ParseField(tokens, ref at);
        Token next = Next(tokens, at, "an operator after the field");
        if (next.Kind == TokenKind.Symbol && next.Text is "=" or "==" or "!=" or "<>" or "<" or "<=" or ">" or ">=")
        {
            at++;
            ComparisonOp op = next.Text switch
            {
                "=" or "==" => ComparisonOp.Equal,
                "!=" or "<>" => ComparisonOp.NotEqual,
                "<" => ComparisonOp.Less,
                "<=" => ComparisonOp.LessOrEqual,
                ">" => ComparisonOp.Greater,
                _ => ComparisonOp.GreaterOrEqual,
            };

            Token operand = Next(tokens, at, "a literal or a column after the operator");
            if (operand.Kind is TokenKind.Name or TokenKind.QuotedName && !IsLiteralWord(operand))
            {
                return new ColumnComparisonExpr(field, op, ParseField(tokens, ref at));
            }

            return new ComparisonExpr(field, op, ParseLiteral(tokens, ref at));
        }

        if (IsWord(tokens, at, "is"))
        {
            at++;
            bool not = IsWord(tokens, at, "not");
            if (not)
            {
                at++;
            }

            ExpectWord(tokens, ref at, "null");
            return new NullCheckExpr(field, isNull: !not);
        }

        bool negated = false;
        if (IsWord(tokens, at, "not") && IsWord(tokens, at + 1, "in"))
        {
            negated = true;
            at++;
        }

        if (IsWord(tokens, at, "in"))
        {
            at++;
            Expect(tokens, ref at, "(");
            List<FilterLiteral> values = [ParseLiteral(tokens, ref at)];
            while (IsSymbol(tokens, at, ","))
            {
                at++;
                values.Add(ParseLiteral(tokens, ref at));
            }

            Expect(tokens, ref at, ")");
            VortexExpr membership = Expr.In(field, [.. values]);
            return negated ? Expr.Not(membership) : membership;
        }

        if (IsWord(tokens, at, "like"))
        {
            at++;
            FilterLiteral pattern = ParseLiteral(tokens, ref at);
            byte escape = (byte)'\\';
            if (IsWord(tokens, at, "escape"))
            {
                at++;
                FilterLiteral escapeText = ParseLiteral(tokens, ref at);
                if (escapeText.Kind != FilterLiteralKind.Bytes || escapeText.BytesValue.Length != 1)
                {
                    throw new FormatException("An escape is one ASCII character in quotes.");
                }

                escape = escapeText.BytesValue[0];
            }

            return Expr.Like(field, pattern, escape);
        }

        if (IsWord(tokens, at, "starts") && IsWord(tokens, at + 1, "with"))
        {
            at += 2;
            return Expr.StartsWith(field, ParseLiteral(tokens, ref at));
        }

        if (IsWord(tokens, at, "contains"))
        {
            at++;
            FilterLiteral value = ParseLiteral(tokens, ref at);
            return value.Kind == FilterLiteralKind.Bytes ? Expr.Contains(field, value) : Expr.ListContains(field, value);
        }

        throw new FormatException($"Expected an operator, 'is', 'in', 'like', 'starts with' or 'contains' at position {next.Position}, not '{next.Text}'.");
    }

    private static FieldExpr ParseField(List<Token> tokens, ref int at)
    {
        List<string> names = [];
        while (true)
        {
            Token name = Next(tokens, at, "a column name");
            if (name.Kind is not (TokenKind.Name or TokenKind.QuotedName))
            {
                throw new FormatException($"Expected a column name at position {name.Position}, not '{name.Text}'.");
            }

            names.Add(name.Text);
            at++;
            if (!IsSymbol(tokens, at, "."))
            {
                return new FieldExpr([.. names]);
            }

            at++;
        }
    }

    private static FilterLiteral ParseLiteral(List<Token> tokens, ref int at)
    {
        Token token = Next(tokens, at, "a literal");
        at++;
        switch (token.Kind)
        {
            case TokenKind.Parameter:
                return token.Value;
            case TokenKind.Text:
                return FilterLiteral.From(token.Text);
            case TokenKind.Hex:
                return FilterLiteral.From(Convert.FromHexString(token.Text));
            case TokenKind.Number:
                if (token.Text.AsSpan().IndexOfAny('.', 'e', 'E') < 0)
                {
                    if (long.TryParse(token.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long signed))
                    {
                        return FilterLiteral.From(signed);
                    }

                    if (ulong.TryParse(token.Text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong unsigned))
                    {
                        return FilterLiteral.From(unsigned);
                    }
                }

                return FilterLiteral.From(double.Parse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture));
            case TokenKind.Name when token.Text.Equals("true", StringComparison.OrdinalIgnoreCase):
                return FilterLiteral.From(true);
            case TokenKind.Name when token.Text.Equals("false", StringComparison.OrdinalIgnoreCase):
                return FilterLiteral.From(false);
            case TokenKind.Name when token.Text.Equals("null", StringComparison.OrdinalIgnoreCase):
                return FilterLiteral.Null;
            default:
                throw new FormatException($"Expected a literal at position {token.Position}, not '{token.Text}'.");
        }
    }

    private static bool IsLiteralWord(Token token) =>
        token.Kind == TokenKind.Name && (token.Text.Equals("true", StringComparison.OrdinalIgnoreCase)
            || token.Text.Equals("false", StringComparison.OrdinalIgnoreCase)
            || token.Text.Equals("null", StringComparison.OrdinalIgnoreCase));

    private static bool IsWord(List<Token> tokens, int at, string word) =>
        at < tokens.Count && tokens[at].Kind == TokenKind.Name && tokens[at].Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    private static bool IsSymbol(List<Token> tokens, int at, string symbol) =>
        at < tokens.Count && tokens[at].Kind == TokenKind.Symbol && tokens[at].Text == symbol;

    private static void Expect(List<Token> tokens, ref int at, string symbol)
    {
        if (!IsSymbol(tokens, at, symbol))
        {
            Token found = Next(tokens, at, $"'{symbol}'");
            throw new FormatException($"Expected '{symbol}' at position {found.Position}, not '{found.Text}'.");
        }

        at++;
    }

    private static void ExpectWord(List<Token> tokens, ref int at, string word)
    {
        if (!IsWord(tokens, at, word))
        {
            Token found = Next(tokens, at, $"'{word}'");
            throw new FormatException($"Expected '{word}' at position {found.Position}, not '{found.Text}'.");
        }

        at++;
    }

    private static Token Next(List<Token> tokens, int at, string wanted) =>
        at < tokens.Count ? tokens[at] : throw new FormatException($"The filter ends where {wanted} was expected.");

    private static List<Token> Tokenize(ReadOnlySpan<char> text, IReadOnlyList<FilterLiteral>? parameters)
    {
        List<Token> tokens = [];
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            int start = i;
            if (c == '?' && parameters is not null && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                i++;
                while (i < text.Length && char.IsDigit(text[i]))
                {
                    i++;
                }

                int index = int.Parse(text[(start + 1)..i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
                tokens.Add(new Token(TokenKind.Parameter, text[start..i].ToString(), start, index < parameters.Count ? parameters[index] : default));
                continue;
            }
            if (c is '\'' or '"')
            {
                (string value, int end) = Quoted(text, i, c);
                tokens.Add(new Token(c == '\'' ? TokenKind.Text : TokenKind.QuotedName, value, start));
                i = end;
                continue;
            }

            if ((c is 'x' or 'X') && i + 1 < text.Length && text[i + 1] == '\'')
            {
                (string value, int end) = Quoted(text, i + 1, '\'');
                tokens.Add(new Token(TokenKind.Hex, value, start));
                i = end;
                continue;
            }

            if (char.IsDigit(c) || (c is '-' or '+' && i + 1 < text.Length && (char.IsDigit(text[i + 1]) || text[i + 1] == '.')))
            {
                i++;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] is '.' or 'e' or 'E' || (text[i] is '-' or '+' && text[i - 1] is 'e' or 'E')))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Number, text[start..i].ToString(), start));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Name, text[start..i].ToString(), start));
                continue;
            }

            string symbol = i + 1 < text.Length && text.Slice(i, 2) is "<=" or ">=" or "!=" or "<>" or "=="
                ? text.Slice(i, 2).ToString()
                : c.ToString();
            if (symbol.Length == 1 && symbol is not ("=" or "<" or ">" or "(" or ")" or "," or "."))
            {
                throw new FormatException($"Unexpected '{c}' at position {i} of the filter.");
            }

            tokens.Add(new Token(TokenKind.Symbol, symbol, start));
            i += symbol.Length;
        }

        return tokens;
    }

    private static (string Value, int End) Quoted(ReadOnlySpan<char> text, int open, char quote)
    {
        StringBuilder value = new StringBuilder();
        int i = open + 1;
        while (i < text.Length)
        {
            if (text[i] == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    value.Append(quote);
                    i += 2;
                    continue;
                }

                return (value.ToString(), i + 1);
            }

            value.Append(text[i]);
            i++;
        }

        throw new FormatException($"The quote opened at position {open} is not closed.");
    }

    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsKeyword(string name) =>
        name.ToLowerInvariant() is "and" or "or" or "not" or "in" or "is" or "null" or "like" or "escape" or "starts" or "with" or "contains" or "true" or "false";

    private enum TokenKind : byte
    {
        Name,
        QuotedName,
        Text,
        Hex,
        Number,
        Symbol,
        Parameter,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Position, FilterLiteral Value = default);

    /// <summary>The column path the last predicate of <paramref name="text"/> names, for the hole that follows it.</summary>
    internal static string[]? LastField(string text)
    {
        List<Token> tokens;
        try
        {
            tokens = Tokenize(text, []);
        }
        catch (FormatException)
        {
            return null;
        }

        // Back over the values already given to an `in` list, to the operator, then to the field.
        int at = tokens.Count - 1;
        while (at >= 0 && (tokens[at].Kind is TokenKind.Parameter or TokenKind.Text or TokenKind.Number or TokenKind.Hex
            || (tokens[at].Kind == TokenKind.Symbol && tokens[at].Text is "(" or ",")))
        {
            at--;
        }

        while (at >= 0 && (tokens[at].Kind == TokenKind.Symbol && tokens[at].Text is "=" or "==" or "!=" or "<>" or "<" or "<=" or ">" or ">="
            || (tokens[at].Kind == TokenKind.Name && IsKeyword(tokens[at].Text))))
        {
            at--;
        }

        if (at < 0 || tokens[at].Kind is not (TokenKind.Name or TokenKind.QuotedName))
        {
            return null;
        }

        List<string> names = [tokens[at].Text];
        while (at >= 2 && tokens[at - 1].Kind == TokenKind.Symbol && tokens[at - 1].Text == "." && tokens[at - 2].Kind is TokenKind.Name or TokenKind.QuotedName)
        {
            names.Insert(0, tokens[at - 2].Text);
            at -= 2;
        }

        return [.. names];
    }
}
