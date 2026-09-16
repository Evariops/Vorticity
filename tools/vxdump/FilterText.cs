// `--explain`'s expression syntax: enough to name the predicates a scan prunes with, and no more.
//
//   expr    := and ( "or" and )*
//   and     := term ( "and" term )*
//   term    := field op literal | field "starts" literal | field "is" ["not"] "null"
//   op      := = | != | < | <= | > | >=
//   literal := 'text' | integer | decimal | true | false
//
// Words are separated by spaces; a quoted literal may hold spaces. A field is a column path as the
// scan spells it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Vorticity.Expressions;

namespace Vorticity.Tools.VxDump;

internal static class FilterText
{
    internal static VortexExpr Parse(string text)
    {
        List<string> words = Words(text);
        int at = 0;
        VortexExpr expr = Or(words, ref at);
        if (at != words.Count)
        {
            throw new FormatException($"Unexpected '{words[at]}' in the expression.");
        }

        return expr;
    }

    private static VortexExpr Or(List<string> words, ref int at)
    {
        VortexExpr left = And(words, ref at);
        while (at < words.Count && words[at].Equals("or", StringComparison.OrdinalIgnoreCase))
        {
            at++;
            left = Expr.Or(left, And(words, ref at));
        }

        return left;
    }

    private static VortexExpr And(List<string> words, ref int at)
    {
        VortexExpr left = Term(words, ref at);
        while (at < words.Count && words[at].Equals("and", StringComparison.OrdinalIgnoreCase))
        {
            at++;
            left = Expr.And(left, Term(words, ref at));
        }

        return left;
    }

    private static VortexExpr Term(List<string> words, ref int at)
    {
        if (at + 2 > words.Count)
        {
            throw new FormatException("An expression term is a field, an operator and a literal.");
        }

        FieldExpr field = Expr.Field(words[at++]);
        string op = words[at++].ToLowerInvariant();
        if (op == "is")
        {
            bool not = at < words.Count && words[at].Equals("not", StringComparison.OrdinalIgnoreCase);
            if (not)
            {
                at++;
            }

            if (at >= words.Count || !words[at].Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("Expected 'null' after 'is'.");
            }

            at++;
            return not ? Expr.IsNotNull(field) : Expr.IsNull(field);
        }

        if (at >= words.Count)
        {
            throw new FormatException($"'{op}' needs a literal.");
        }

        FilterLiteral literal = Literal(words[at++]);
        LiteralExpr value = Expr.Literal(literal);
        return op switch
        {
            "=" or "==" => Expr.Eq(field, value),
            "!=" or "<>" => Expr.Ne(field, value),
            "<" => Expr.Lt(field, value),
            "<=" => Expr.Le(field, value),
            ">" => Expr.Gt(field, value),
            ">=" => Expr.Ge(field, value),
            "starts" => Expr.StartsWith(field, literal),
            _ => throw new FormatException($"Unknown operator '{op}'."),
        };
    }

    private static FilterLiteral Literal(string word)
    {
        if (word.Length >= 2 && word[0] == '\'' && word[^1] == '\'')
        {
            return FilterLiteral.From(word[1..^1]);
        }

        if (word.Equals("true", StringComparison.OrdinalIgnoreCase) || word.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return FilterLiteral.From(word.Equals("true", StringComparison.OrdinalIgnoreCase));
        }

        if (long.TryParse(word, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
        {
            return FilterLiteral.From(integer);
        }

        if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
        {
            return FilterLiteral.From(real);
        }

        return FilterLiteral.From(word);
    }

    private static List<string> Words(string text)
    {
        List<string> words = [];
        StringBuilder word = new StringBuilder();
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '\'')
            {
                quoted = !quoted;
                word.Append(c);
                continue;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (word.Length > 0)
                {
                    words.Add(word.ToString());
                    word.Clear();
                }

                continue;
            }

            word.Append(c);
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return words;
    }
}
