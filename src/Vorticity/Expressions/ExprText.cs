using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Unicode;

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

    internal static VortexExpr Parse(ReadOnlySpan<char> text)
    {
        List<Token> tokens = Tokenize(text);
        int at = 0;
        VortexExpr expr = ParseOr(tokens, ref at);
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

    private static VortexExpr ParseOr(List<Token> tokens, ref int at)
    {
        VortexExpr left = ParseAnd(tokens, ref at);
        while (IsWord(tokens, at, "or"))
        {
            at++;
            left = Expr.Or(left, ParseAnd(tokens, ref at));
        }

        return left;
    }

    private static VortexExpr ParseAnd(List<Token> tokens, ref int at)
    {
        VortexExpr left = ParseUnary(tokens, ref at);
        while (IsWord(tokens, at, "and"))
        {
            at++;
            left = Expr.And(left, ParseUnary(tokens, ref at));
        }

        return left;
    }

    private static VortexExpr ParseUnary(List<Token> tokens, ref int at)
    {
        if (IsWord(tokens, at, "not"))
        {
            at++;
            return Expr.Not(ParseUnary(tokens, ref at));
        }

        if (IsSymbol(tokens, at, "("))
        {
            at++;
            VortexExpr inner = ParseOr(tokens, ref at);
            Expect(tokens, ref at, ")");
            return inner;
        }

        return ParsePredicate(tokens, ref at);
    }

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

    private static List<Token> Tokenize(ReadOnlySpan<char> text)
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
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Position);
}
