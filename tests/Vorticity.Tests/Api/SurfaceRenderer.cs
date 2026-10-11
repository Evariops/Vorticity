using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Vorticity.Tests.Api;

/// <summary>
/// Renders the public surface of assemblies as text, a line a type and a line a member, in an order
/// that does not depend on reflection's: what each package's surface test compares to the file it
/// tracks. Linked into every test project that keeps a surface on record, so the rendering is one.
/// </summary>
internal static class SurfaceRenderer
{
    /// <summary>
    /// Why the renderer declares itself unfit for trimming rather than silencing the analyzer:
    /// walking every exported type of an assembly is the one thing a trimmer cannot follow.
    /// </summary>
    internal const string NotTrimmable =
        "Renders the public surface by walking every exported type, which a trimmer cannot follow.";

    /// <summary>The return type and the name of a rendered method line: <c>method ValueTask&lt;T1&gt; AggregateAsync&lt;T1&gt;(…)</c>.</summary>
    internal static bool TryMethod(string line, out string returned, out string name)
    {
        returned = name = string.Empty;
        int method = line.IndexOf(" method ", StringComparison.Ordinal);
        int paren = line.IndexOf('(', StringComparison.Ordinal);
        if (method < 0 || paren < 0)
        {
            return false;
        }

        // Past the method's own type arguments, back to the end of its name.
        int end = paren;
        if (line[end - 1] == '>')
        {
            int depth = 0;
            do
            {
                end--;
                depth += line[end] == '>' ? 1 : line[end] == '<' ? -1 : 0;
            }
            while (depth > 0);
        }

        int start = line.LastIndexOf(' ', end - 1) + 1;
        name = line[start..end];
        returned = line[(method + " method ".Length)..start].Trim();
        return name.Length > 0;
    }

    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(void)] = "void",
        [typeof(bool)] = "bool",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(nint)] = "nint",
        [typeof(nuint)] = "nuint",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(decimal)] = "decimal",
        [typeof(char)] = "char",
        [typeof(string)] = "string",
        [typeof(object)] = "object",
    };

    [RequiresUnreferencedCode(NotTrimmable)]
    internal static string Render(IEnumerable<Assembly> assemblies)
    {
        StringBuilder text = new();
        foreach (Assembly assembly in assemblies)
        {
            string name = assembly.GetName().Name ?? "?";
            foreach (Type type in assembly.GetExportedTypes()
                         .OrderBy(NameOf, StringComparer.Ordinal))
            {
                text.Append(name).Append(": ").Append(Describe(type)).Append('\n');
                foreach (string member in Members(type).Order(StringComparer.Ordinal))
                {
                    text.Append(name).Append(":     ").Append(member).Append('\n');
                }
            }
        }

        return text.ToString();
    }

    [RequiresUnreferencedCode(NotTrimmable)]
    private static IEnumerable<string> Members(Type type)
    {
        const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (MemberInfo member in type.GetMembers(Flags))
        {
            string? rendered = member switch
            {
                ConstructorInfo c when IsSurface(c.Attributes) => Signature("ctor", c, TypeName(type)),
                MethodInfo m when IsSurface(m.Attributes) && !IsAccessor(m) =>
                    Signature("method", m, TypeName(m.ReturnType) + " " + NameWithArity(m)),
                PropertyInfo p => Property(p),
                FieldInfo f when IsSurface(f.Attributes) => Field(f),
                EventInfo e when e.AddMethod is not null && IsSurface(e.AddMethod.Attributes) =>
                    "event " + TypeName(e.EventHandlerType!) + " " + e.Name,
                _ => null,
            };

            if (rendered is not null)
            {
                yield return rendered;
            }
        }
    }

    private static bool IsSurface(MethodAttributes attributes)
    {
        MethodAttributes visibility = attributes & MethodAttributes.MemberAccessMask;
        return visibility is MethodAttributes.Public or MethodAttributes.Family
            or MethodAttributes.FamORAssem;
    }

    private static bool IsSurface(FieldAttributes attributes)
    {
        FieldAttributes visibility = attributes & FieldAttributes.FieldAccessMask;
        return visibility is FieldAttributes.Public or FieldAttributes.Family
            or FieldAttributes.FamORAssem;
    }

    // Property and event accessors are rendered with their property or event; operators are not
    // accessors and stay, because an operator is as much of a promise as a method.
    private static bool IsAccessor(MethodInfo method) =>
        method.IsSpecialName &&
        (method.Name.StartsWith("get_", StringComparison.Ordinal) ||
         method.Name.StartsWith("set_", StringComparison.Ordinal) ||
         method.Name.StartsWith("add_", StringComparison.Ordinal) ||
         method.Name.StartsWith("remove_", StringComparison.Ordinal));

    private static string? Property(PropertyInfo property)
    {
        MethodInfo? getter = property.GetMethod;
        MethodInfo? setter = property.SetMethod;
        bool readable = getter is not null && IsSurface(getter.Attributes);
        bool writable = setter is not null && IsSurface(setter.Attributes);
        if (!readable && !writable)
        {
            return null;
        }

        StringBuilder text = new();
        text.Append("property ").Append(TypeName(property.PropertyType)).Append(' ')
            .Append(property.Name);
        ParameterInfo[] indexers = property.GetIndexParameters();
        if (indexers.Length > 0)
        {
            text.Append('[').AppendJoin(", ", indexers.Select(p => TypeName(p.ParameterType)))
                .Append(']');
        }

        text.Append(" {");
        if (readable)
        {
            text.Append(" get;");
        }

        if (writable)
        {
            text.Append(setter!.ReturnParameter.GetRequiredCustomModifiers()
                .Any(m => m == typeof(IsExternalInit)) ? " init;" : " set;");
        }

        return text.Append(" }").ToString();
    }

    private static string Field(FieldInfo field)
    {
        string kind = field.IsLiteral ? "const" : field.IsStatic ? "static field" : "field";
        string text = kind + " " + TypeName(field.FieldType) + " " + field.Name;
        return field.IsLiteral && field.GetRawConstantValue() is object value
            ? text + " = " + Convert.ToString(value, CultureInfo.InvariantCulture)
            : text;
    }

    private static string Signature(string kind, MethodBase method, string head)
    {
        StringBuilder text = new();
        text.Append(kind).Append(' ').Append(head).Append('(');
        text.AppendJoin(", ", method.GetParameters().Select(Parameter));
        return text.Append(')').ToString();
    }

    private static string Parameter(ParameterInfo parameter)
    {
        string type = TypeName(parameter.ParameterType);
        string prefix = parameter.ParameterType.IsByRef
            ? parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref "
            : string.Empty;
        return parameter.IsOptional ? prefix + type + " = default" : prefix + type;
    }

    private static string NameWithArity(MethodInfo method) =>
        method.IsGenericMethodDefinition
            ? method.Name + "<" + string.Join(
                ", ", method.GetGenericArguments().Select(a => a.Name)) + ">"
            : method.Name;

    [RequiresUnreferencedCode(NotTrimmable)]
    private static string Describe(Type type)
    {
        StringBuilder text = new();
        if (type.IsInterface)
        {
            text.Append("interface");
        }
        else if (type.IsEnum)
        {
            text.Append("enum");
        }
        else if (type.IsValueType)
        {
            text.Append(type.IsByRefLike ? "ref struct" : "struct");
        }
        else if (type.IsAbstract && type.IsSealed)
        {
            text.Append("static class");
        }
        else
        {
            text.Append(type.IsAbstract ? "abstract class" : type.IsSealed ? "sealed class" : "class");
        }

        text.Append(' ').Append(NameOf(type));

        string[] bases = [.. Bases(type).Order(StringComparer.Ordinal)];
        if (bases.Length > 0)
        {
            text.Append(" : ").AppendJoin(", ", bases);
        }

        return text.ToString();
    }

    // The base type and the interfaces are part of the promise: dropping one breaks a caller that
    // assigned through it. Interfaces an interface already implies are left out, so adding a base
    // interface to an interface shows up once rather than on every implementor.
    [RequiresUnreferencedCode(NotTrimmable)]
    private static IEnumerable<string> Bases(Type type)
    {
        if (!type.IsValueType && !type.IsInterface &&
            type.BaseType is Type baseType && baseType != typeof(object))
        {
            yield return TypeName(baseType);
        }

        Type[] direct = type.GetInterfaces();
        foreach (Type contract in direct)
        {
            if (!direct.Any(other => other != contract && other.GetInterfaces().Contains(contract)) &&
                (type.BaseType is null || !type.BaseType.GetInterfaces().Contains(contract)))
            {
                yield return TypeName(contract);
            }
        }
    }

    private static string NameOf(Type type) =>
        (type.Namespace is null ? string.Empty : type.Namespace + ".") + TypeName(type);

    private static string TypeName(Type type)
    {
        if (type.IsByRef)
        {
            return TypeName(type.GetElementType()!);
        }

        if (type.IsArray)
        {
            return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        if (type.IsPointer)
        {
            return TypeName(type.GetElementType()!) + "*";
        }

        // Keywords rather than CLR names: this file is read by a person deciding whether a member
        // has a reason to be public, and `ReadOnlySpan<byte>` is that decision's vocabulary.
        if (Keywords.TryGetValue(type, out string? keyword))
        {
            return keyword;
        }

        string name = type.DeclaringType is not null && !type.IsGenericParameter
            ? TypeName(type.DeclaringType) + "." + Bare(type)
            : Bare(type);

        return type.IsGenericType
            ? name + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">"
            : name;

        static string Bare(Type t)
        {
            int tick = t.Name.IndexOf('`', StringComparison.Ordinal);
            return tick < 0 ? t.Name : t.Name[..tick];
        }
    }

    internal static string Diff(string expected, string actual)
    {
        HashSet<string> before = [.. expected.Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        HashSet<string> after = [.. actual.Split('\n', StringSplitOptions.RemoveEmptyEntries)];

        string[] withdrawn = [.. before.Except(after).Order(StringComparer.Ordinal)];
        string[] added = [.. after.Except(before).Order(StringComparer.Ordinal)];

        StringBuilder text = new();
        text.Append(withdrawn.Length).Append(" withdrawn, ").Append(added.Length).Append(" added.");
        Append(text, "- ", withdrawn);
        Append(text, "+ ", added);
        return text.ToString();

        static void Append(StringBuilder text, string sign, string[] lines)
        {
            foreach (string line in lines.Take(40))
            {
                text.Append(Environment.NewLine).Append(sign).Append(line);
            }

            if (lines.Length > 40)
            {
                text.Append(Environment.NewLine).Append("  ... and ").Append(lines.Length - 40)
                    .Append(" more.");
            }
        }
    }

    // Normalised so a checkout with CRLF endings, or an editor that adds a final newline, does not
    // read as a surface change.
    internal static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
