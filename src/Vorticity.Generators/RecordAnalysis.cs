using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vorticity.Generators;

/// <summary>Reads a <c>[VortexRecord]</c> type into the model the emitter writes, or into the diagnostics that stop it.</summary>
internal static class RecordAnalysis
{
    public static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly SymbolDisplayFormat NamespaceFormat = new SymbolDisplayFormat(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static readonly string[] UtcZones = ["UTC", "Etc/UTC", "Z", "+00:00", "Etc/GMT", "GMT"];

    private static readonly string[] TimeUnits = ["Nanoseconds", "Microseconds", "Milliseconds", "Seconds", "Days"];

    public static RecordResult Analyze(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        INamedTypeSymbol type = (INamedTypeSymbol)context.TargetSymbol;
        TypeDeclarationSyntax declaration = (TypeDeclarationSyntax)context.TargetNode;
        Compilation compilation = context.SemanticModel.Compilation;
        KnownSymbols known = new KnownSymbols(compilation);
        LocationModel? typeLocation = LocationModel.From(declaration.Identifier.GetLocation());
        string display = type.ToDisplayString();

        // An attribute repeated on two parts is the compiler's error to report; one part is enough for ours.
        if (!OwnsAttribute(type, declaration, known, cancellationToken))
        {
            return new RecordResult(null, EquatableArray<DiagnosticModel>.Empty);
        }

        if (!IsPartial(declaration) || !ContainersArePartial(type, cancellationToken))
        {
            return Stop(Diagnostic(Descriptors.RecordNotPartial, typeLocation, display));
        }

        if (ShapeProblem(type) is string shape)
        {
            return Stop(Diagnostic(Descriptors.RecordShapeUnsupported, typeLocation, display, shape));
        }

        List<DiagnosticModel> diagnostics = [];
        IMethodSymbol? primary = PrimaryConstructor(type, cancellationToken);
        List<Candidate> candidates = Candidates(type, primary, compilation);
        Helpers helpers = new Helpers();
        List<Included> included = Include(type, candidates, known, typeLocation, helpers, diagnostics, cancellationToken);
        if (diagnostics.Count > 0)
        {
            return new RecordResult(null, new EquatableArray<DiagnosticModel>([.. diagnostics]));
        }

        if (!ChooseConstructor(type, primary, included, compilation, out IMethodSymbol? constructor, out int[] arguments))
        {
            return Stop(Diagnostic(Descriptors.RecordShapeUnsupported, typeLocation, display,
                "no constructor takes only members as its parameters, and none takes no parameter"));
        }

        List<MemberModel> members = Fills(type, candidates, included, constructor, arguments, compilation, known, typeLocation, diagnostics);
        if (diagnostics.Count > 0)
        {
            return new RecordResult(null, new EquatableArray<DiagnosticModel>([.. diagnostics]));
        }

        return new RecordResult(Assemble(type, included, members, constructor, arguments, helpers, known, compilation), EquatableArray<DiagnosticModel>.Empty);
    }

    private static bool OwnsAttribute(INamedTypeSymbol type, TypeDeclarationSyntax declaration, KnownSymbols known, CancellationToken cancellationToken)
    {
        AttributeData? first = type.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, known.RecordAttribute));
        return first?.ApplicationSyntaxReference?.GetSyntax(cancellationToken).FirstAncestorOrSelf<TypeDeclarationSyntax>() is not TypeDeclarationSyntax owner
            || owner == declaration;
    }

    /// <summary>The candidates that are columns, each with its type's model; a diagnostic for each that has no mapping.</summary>
    private static List<Included> Include(
        INamedTypeSymbol type, List<Candidate> candidates, KnownSymbols known, LocationModel? typeLocation, Helpers helpers,
        List<DiagnosticModel> diagnostics, CancellationToken cancellationToken)
    {
        List<Included> included = [];
        foreach (Candidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<AttributeData> attributes = Attributes(candidate);
            if (IsIgnored(attributes, known))
            {
                continue;
            }

            LocationModel? location = LocationModel.From(candidate.Parameter?.Locations.FirstOrDefault() ?? candidate.Member.Locations.FirstOrDefault()) ?? typeLocation;
            ColumnOptions options = ColumnOptions.From(attributes, known.ColumnAttribute);
            ITypeSymbol memberType = TypeOf(candidate.Member);
            MappedType mapped = MappedType.Map(memberType, known);
            string? error = mapped.Error;
            if (error is null && mapped.Kind == ValueKind.Record && Reaches(mapped.Core, type, known, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
            {
                error = "a record cannot hold itself, directly or through another record";
            }

            ValueModel? value = error is null ? Build(mapped, memberType, options, helpers, out error) : null;
            if (value is null)
            {
                diagnostics.Add(Diagnostic(Descriptors.MemberHasNoDType, location, candidate.Member.Name, type.ToDisplayString(), memberType.ToDisplayString(), error ?? string.Empty));
                continue;
            }

            included.Add(new Included(candidate.Member, candidate.Parameter, options.Name ?? candidate.Member.Name, value, location));
        }

        return included;
    }

    private static ImmutableArray<AttributeData> Attributes(Candidate candidate) =>
        candidate.Parameter is null ? candidate.Member.GetAttributes() : candidate.Member.GetAttributes().AddRange(candidate.Parameter.GetAttributes());

    private static bool IsIgnored(ImmutableArray<AttributeData> attributes, KnownSymbols known) =>
        known.IgnoreAttribute is not null && attributes.Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, known.IgnoreAttribute));

    /// <summary>How reading fills each column member; a diagnostic for each it cannot fill, and for a required member left out.</summary>
    private static List<MemberModel> Fills(
        INamedTypeSymbol type, List<Candidate> candidates, List<Included> included, IMethodSymbol? constructor, int[] arguments,
        Compilation compilation, KnownSymbols known, LocationModel? typeLocation, List<DiagnosticModel> diagnostics)
    {
        string display = type.ToDisplayString();
        bool setsRequired = constructor is not null && constructor.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");
        List<MemberModel> members = [];
        for (int i = 0; i < included.Count; i++)
        {
            Included member = included[i];
            if (Fill(member.Member, Array.IndexOf(arguments, i) >= 0, setsRequired, type, compilation, out string? problem) is not MemberFill fill)
            {
                diagnostics.Add(Diagnostic(Descriptors.MemberNotReadable, member.Location, member.Member.Name, display, problem ?? string.Empty));
                continue;
            }

            members.Add(new MemberModel(member.Member.Name, member.ColumnName, fill, member.Value, member.Member.ContainingType.ToDisplayString(TypeFormat)));
        }

        if (!setsRequired)
        {
            foreach (Candidate candidate in candidates)
            {
                if (IsRequired(candidate.Member) && IsIgnored(Attributes(candidate), known))
                {
                    diagnostics.Add(Diagnostic(Descriptors.MemberNotReadable,
                        LocationModel.From(candidate.Member.Locations.FirstOrDefault()) ?? typeLocation, candidate.Member.Name, display,
                        "it is required, and a member marked [VortexIgnore] has no value to set it to"));
                }
            }
        }

        return members;
    }

    private static RecordModel Assemble(
        INamedTypeSymbol type, List<Included> included, List<MemberModel> members, IMethodSymbol? constructor, int[] arguments,
        Helpers helpers, KnownSymbols known, Compilation compilation)
    {
        List<ContainerModel> containers = [];
        for (INamedTypeSymbol? container = type.ContainingType; container is not null; container = container.ContainingType)
        {
            containers.Insert(0, new ContainerModel(Keyword(container), Escape(container.Name)));
        }

        string? ns = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(NamespaceFormat);
        Accessibility access = Accessible(type);
        foreach (Included member in included)
        {
            access = Min(access, Accessible(TypeOf(member.Member)));
        }

        // The extension class sits in the namespace, so it can name the record only when a namespace can.
        // Its containers are joined to the name by underscores: run together, A.B.Rec and AB.Rec would
        // name one class twice.
        string containerNames = string.Concat(containers.Select(c => c.Name.TrimStart('@') + "_"));
        string? extensions = members.Count == 0 || access == Accessibility.NotApplicable ? null : containerNames + type.Name + "VortexExtensions";
        string hint = (ns is null ? string.Empty : ns.Replace("@", string.Empty) + ".")
            + string.Concat(containers.Select(c => c.Name.TrimStart('@') + ".")) + type.Name + ".g.cs";

        return new RecordModel(
            ns,
            new EquatableArray<ContainerModel>([.. containers]),
            Keyword(type),
            Escape(type.Name),
            type.ToDisplayString(TypeFormat),
            new EquatableArray<MemberModel>([.. members]),
            constructor is not null && constructor.Parameters.Length > 0,
            new EquatableArray<int>(arguments),
            helpers.Schema,
            helpers.Extension,
            Names(type, members, known, compilation),
            extensions,
            access == Accessibility.Public ? "public" : "internal",
            hint);
    }

    private static RecordResult Stop(DiagnosticModel diagnostic) => new RecordResult(null, new EquatableArray<DiagnosticModel>([diagnostic]));

    private static DiagnosticModel Diagnostic(DiagnosticDescriptor descriptor, LocationModel? location, params string[] arguments) =>
        new DiagnosticModel(descriptor.Id, location, new EquatableArray<string>(arguments));

    private static bool IsPartial(TypeDeclarationSyntax declaration) => declaration.Modifiers.Any(SyntaxKind.PartialKeyword);

    private static bool ContainersArePartial(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        for (INamedTypeSymbol? container = type.ContainingType; container is not null; container = container.ContainingType)
        {
            foreach (SyntaxReference reference in container.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(cancellationToken) is not TypeDeclarationSyntax syntax || !IsPartial(syntax))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string? ShapeProblem(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.TypeParameters.Length > 0)
            {
                return current.Equals(type, SymbolEqualityComparer.Default)
                    ? "a generic type has no schema known when the program is compiled"
                    : $"it is nested in the generic type {current.ToDisplayString()}, so its schema is not known when the program is compiled";
            }
        }

        if (type.IsRefLikeType)
        {
            return "a ref struct cannot be held in the span of rows that reading fills";
        }

        if (type.IsStatic)
        {
            return "a static class has no rows";
        }

        if (type.IsAbstract)
        {
            return "an abstract type cannot be constructed when rows are read";
        }

        if (type.IsFileLocal)
        {
            return "a file-local type cannot be completed from the file the generator adds";
        }

        return null;
    }

    private static IMethodSymbol? PrimaryConstructor(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        foreach (IMethodSymbol constructor in type.InstanceConstructors)
        {
            foreach (SyntaxReference reference in constructor.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax)
                {
                    return constructor;
                }
            }
        }

        return null;
    }

    /// <summary>The public instance properties and fields, base types first: a positional record's parameters, then each level in declaration order.</summary>
    private static List<Candidate> Candidates(INamedTypeSymbol type, IMethodSymbol? primary, Compilation compilation)
    {
        List<Candidate> candidates = [];
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        if (type.IsRecord && primary is not null)
        {
            foreach (IParameterSymbol parameter in primary.Parameters)
            {
                seen.Add(parameter.Name);
                if (FindMember(type, parameter.Name) is ISymbol member)
                {
                    candidates.Add(new Candidate(member, parameter));
                }
            }
        }

        List<INamedTypeSymbol> levels = [];
        for (INamedTypeSymbol? level = type; level is not null && level.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); level = level.BaseType)
        {
            levels.Insert(0, level);
        }

        Dictionary<SyntaxTree, int> trees = new Dictionary<SyntaxTree, int>();
        if (levels.Any(level => level.DeclaringSyntaxReferences.Length > 1))
        {
            int index = 0;
            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                trees[tree] = index++;
            }
        }

        foreach (INamedTypeSymbol level in levels)
        {
            ImmutableArray<ISymbol> declared = level.GetMembers();
            IEnumerable<ISymbol> ordered = declared
                .Select((member, position) => (member, position))
                .Where(pair => IsCandidate(pair.member))
                .OrderBy(pair => SourceOrder(pair.member, trees, pair.position))
                .Select(pair => pair.member);
            foreach (ISymbol member in ordered)
            {
                if (seen.Add(member.Name))
                {
                    candidates.Add(new Candidate(FindMember(type, member.Name) ?? member, null));
                }
            }
        }

        return candidates;
    }

    /// <summary>A member's place in source: file order, which matters only for a type split over files, then position.</summary>
    private static (int Tree, int Position) SourceOrder(ISymbol member, Dictionary<SyntaxTree, int> trees, int position)
    {
        Location? location = member.Locations.FirstOrDefault(l => l.IsInSource);
        return location is null
            ? (int.MaxValue, position)
            : (trees.TryGetValue(location.SourceTree!, out int tree) ? tree : 0, location.SourceSpan.Start);
    }

    private static bool IsCandidate(ISymbol member)
    {
        if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public)
        {
            return false;
        }

        return member switch
        {
            IPropertySymbol property => !property.IsIndexer && property.GetMethod is not null,
            IFieldSymbol field => !field.IsConst && !field.IsImplicitlyDeclared && !field.IsFixedSizeBuffer,
            _ => false,
        };
    }

    private static ISymbol? FindMember(INamedTypeSymbol type, string name)
    {
        for (INamedTypeSymbol? level = type; level is not null; level = level.BaseType)
        {
            foreach (ISymbol member in level.GetMembers(name))
            {
                if (IsCandidate(member))
                {
                    return member;
                }
            }
        }

        return null;
    }

    private static ITypeSymbol TypeOf(ISymbol member) => member is IPropertySymbol property ? property.Type : ((IFieldSymbol)member).Type;

    private static bool IsRequired(ISymbol member) => member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true };

    /// <summary>Whether the nested record <paramref name="from"/> holds <paramref name="target"/>, which would make its schema infinite.</summary>
    private static bool Reaches(ITypeSymbol from, INamedTypeSymbol target, KnownSymbols known, HashSet<ITypeSymbol> visited)
    {
        if (SymbolEqualityComparer.Default.Equals(from, target))
        {
            return true;
        }

        if (!visited.Add(from))
        {
            return false;
        }

        foreach (ISymbol member in from.GetMembers())
        {
            if (!IsCandidate(member) || (known.IgnoreAttribute is not null && KnownSymbols.HasAttribute(member, known.IgnoreAttribute)))
            {
                continue;
            }

            ITypeSymbol memberType = TypeOf(member);
            ITypeSymbol core = memberType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } wrapped
                ? wrapped.TypeArguments[0]
                : memberType.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            if (core.TypeKind is TypeKind.Class or TypeKind.Struct && known.IsRecord(core) && Reaches(core, target, known, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static ValueModel? Build(MappedType mapped, ITypeSymbol declared, ColumnOptions options, Helpers helpers, out string? error)
    {
        error = null;
        string core = mapped.Core.ToDisplayString(TypeFormat);
        string underlying = mapped.Kind == ValueKind.Enum
            ? ((INamedTypeSymbol)mapped.Core).EnumUnderlyingType!.ToDisplayString(TypeFormat)
            : core;
        ValueModel? element = null;
        string? schema;
        switch (mapped.Kind)
        {
            case ValueKind.List:
                element = Build(mapped.Element!, ((INamedTypeSymbol)mapped.Core).TypeArguments[0], options, helpers, out error);
                schema = element is null ? null : "global::Vorticity.VortexType.List(" + element.Schema + ")";
                break;
            case ValueKind.Record:
                helpers.Schema = true;
                schema = "global::Vorticity.VortexType.Struct([.. VortexSchemaOf<" + core + ">()])";
                break;
            case ValueKind.Extension:
                helpers.Extension = true;
                schema = "VortexExtensionTypeOf<" + core + ">()";
                break;
            default:
                schema = ScalarSchema(mapped.Scalar, options, out error);
                break;
        }

        if (schema is null)
        {
            return null;
        }

        return new ValueModel(
            mapped.Kind,
            mapped.Scalar,
            mapped.IsNullable,
            declared.ToDisplayString(TypeFormat),
            core,
            underlying,
            mapped.IsNullable ? schema + ".Nullable" : schema,
            element);
    }

    private static string? ScalarSchema(ScalarKind scalar, ColumnOptions options, out string? error)
    {
        const string Type = "global::Vorticity.VortexType.";
        error = null;
        switch (scalar)
        {
            case ScalarKind.Decimal:
            case ScalarKind.WideDecimal:
                error = DecimalProblem(options.Precision, options.Scale, scalar == ScalarKind.Decimal);
                return error is null ? Type + $"Decimal({options.Precision}, {options.Scale})" : null;
            case ScalarKind.Time:
                if (options.Unit is < 0 or >= 4)
                {
                    error = "a time is in seconds, milliseconds, microseconds or nanoseconds";
                    return null;
                }

                return Type + $"Time(global::Vorticity.TimeUnit.{TimeUnits[options.Unit]})";
            case ScalarKind.Timestamp:
            case ScalarKind.ZonedTimestamp:
                if (options.Unit is < 0 or >= 5)
                {
                    error = "the unit is not a TimeUnit";
                    return null;
                }

                string unit = "global::Vorticity.TimeUnit." + TimeUnits[options.Unit];
                if (scalar == ScalarKind.Timestamp)
                {
                    if (options.TimeZone is not null && Array.IndexOf(UtcZones, options.TimeZone) < 0)
                    {
                        error = $"a DateTime reads a naive or UTC timestamp, not one in '{options.TimeZone}'; declare the member DateTimeOffset";
                        return null;
                    }

                    return options.TimeZone is null
                        ? Type + $"Timestamp({unit})"
                        : Type + $"Timestamp({unit}, {SymbolDisplay.FormatLiteral(options.TimeZone, quote: true)})";
                }

                return Type + $"Timestamp({unit}, {SymbolDisplay.FormatLiteral(options.TimeZone ?? "UTC", quote: true)})";
            case ScalarKind.Date:
                return Type + "Date";
            case ScalarKind.Uuid:
                return Type + "Uuid";
            default:
                return Type + scalar.ToString();
        }
    }

    private static string? DecimalProblem(int precision, int scale, bool clr)
    {
        if (precision is < 1 or > 76)
        {
            return $"a decimal column has 1 to 76 digits, not {precision}";
        }

        if (scale is < -128 or > 76 || (scale > 0 && scale > precision))
        {
            return $"a decimal scale is at most 76, and at most the precision when positive; {scale} is neither";
        }

        if (clr && (precision > 28 || scale is < 0 or > 28))
        {
            return "a decimal member reads a column of at most 28 digits and a scale of 0 to 28; declare it VortexDecimal for a wider one";
        }

        return null;
    }

    /// <summary>
    /// The constructor reading goes through, and for each of its parameters the index of the member
    /// it takes, or -1 for a positional parameter that is not a column. A null constructor is the
    /// parameterless one.
    /// </summary>
    private static bool ChooseConstructor(INamedTypeSymbol type, IMethodSymbol? primary, List<Included> included, Compilation compilation, out IMethodSymbol? constructor, out int[] arguments)
    {
        constructor = null;
        arguments = [];
        if (type.IsRecord && primary is not null)
        {
            constructor = primary;
            arguments = new int[primary.Parameters.Length];
            for (int p = 0; p < arguments.Length; p++)
            {
                IParameterSymbol parameter = primary.Parameters[p];
                arguments[p] = included.FindIndex(m => m.Parameter is not null && SymbolEqualityComparer.Default.Equals(m.Parameter, parameter));
            }

            return true;
        }

        if (primary is not null && Bind(primary, included, compilation) is int[] primaryArguments)
        {
            constructor = primary;
            arguments = primaryArguments;
            return true;
        }

        if (type.IsValueType || type.InstanceConstructors.Any(c => c.Parameters.Length == 0))
        {
            return true;
        }

        foreach (IMethodSymbol candidate in type.InstanceConstructors.OrderByDescending(c => c.Parameters.Length))
        {
            if (Bind(candidate, included, compilation) is int[] bound)
            {
                constructor = candidate;
                arguments = bound;
                return true;
            }
        }

        return false;
    }

    private static int[]? Bind(IMethodSymbol constructor, List<Included> included, Compilation compilation)
    {
        int[] arguments = new int[constructor.Parameters.Length];
        for (int p = 0; p < arguments.Length; p++)
        {
            IParameterSymbol parameter = constructor.Parameters[p];
            int found = -1;
            for (int m = 0; m < included.Count; m++)
            {
                if (string.Equals(included[m].Member.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (found >= 0)
                    {
                        return null;
                    }

                    found = m;
                }
            }

            if (found < 0 || Array.IndexOf(arguments, found, 0, p) >= 0
                || !compilation.ClassifyCommonConversion(TypeOf(included[found].Member), parameter.Type).IsImplicit)
            {
                return null;
            }

            arguments[p] = found;
        }

        return arguments;
    }

    private static MemberFill? Fill(ISymbol member, bool bound, bool setsRequired, INamedTypeSymbol type, Compilation compilation, out string? problem)
    {
        problem = null;
        if (IsRequired(member) && !setsRequired)
        {
            return MemberFill.Initializer;
        }

        if (bound)
        {
            return MemberFill.Constructor;
        }

        if (member is IFieldSymbol field)
        {
            if (field.IsReadOnly)
            {
                problem = "it is a readonly field and no constructor parameter takes it";
                return null;
            }

            return compilation.IsSymbolAccessibleWithin(field, type) ? MemberFill.Assignment : Inaccessible(out problem);
        }

        IPropertySymbol property = (IPropertySymbol)member;
        if (property.SetMethod is not IMethodSymbol setter)
        {
            problem = "it has no setter and no constructor parameter takes it";
            return null;
        }

        if (!compilation.IsSymbolAccessibleWithin(setter, type))
        {
            return Inaccessible(out problem);
        }

        return setter.IsInitOnly ? MemberFill.Initializer : MemberFill.Assignment;
    }

    private static MemberFill? Inaccessible(out string problem)
    {
        problem = "its setter is not accessible from the record";
        return null;
    }

    private static GeneratedNames Names(INamedTypeSymbol type, List<MemberModel> members, KnownSymbols known, Compilation compilation)
    {
        HashSet<string> memberNames = new HashSet<string>(members.Select(m => m.Name), StringComparer.Ordinal);
        string probe = Unused(["r", "probe"], memberNames);
        string columns = Unused(["c", "columns"], memberNames);
        string builder = Unused(["b", "builder"], memberNames);
        HashSet<string> parameters = new HashSet<string>(StringComparer.Ordinal) { columns };
        List<string> deconstruct = [];
        foreach (MemberModel member in members)
        {
            string name = char.ToLowerInvariant(member.Name[0]) + member.Name.Substring(1);
            while (!parameters.Add(name))
            {
                name += "_";
            }

            deconstruct.Add(Escape(name));
        }

        return new GeneratedNames(
            ExplicitSchema: Taken(type, "Schema", memberNames, compilation),
            ExplicitReadRows: Taken(type, "ReadRows", memberNames, compilation),
            ExplicitWriteRows: Taken(type, "WriteRows", memberNames, compilation),
            EmitColumnNames: !Taken(type, "ColumnNames", memberNames, compilation),
            HidesSchema: Hides(type, "Schema", methodsHide: true, known, compilation),
            HidesReadRows: Hides(type, "ReadRows", methodsHide: false, known, compilation),
            HidesWriteRows: Hides(type, "WriteRows", methodsHide: false, known, compilation),
            HidesColumnNames: Hides(type, "ColumnNames", methodsHide: true, known, compilation),
            ProbeName: probe,
            ColumnsName: columns,
            BuilderName: builder,
            DeconstructNames: new EquatableArray<string>([.. deconstruct]));
    }

    /// <summary>
    /// Whether a generated member named <paramref name="name"/> would shadow something the code still
    /// has to reach: a member of the type itself, a column, or an instance member of a base type. The
    /// interface member is then implemented explicitly, or <c>ColumnNames</c> left out.
    /// </summary>
    private static bool Taken(INamedTypeSymbol type, string name, HashSet<string> columns, Compilation compilation)
    {
        if (!type.GetMembers(name).IsEmpty || columns.Contains(name))
        {
            return true;
        }

        for (INamedTypeSymbol? level = type.BaseType; level is not null && level.SpecialType != SpecialType.System_Object; level = level.BaseType)
        {
            if (level.GetMembers(name).Any(member => !member.IsStatic && compilation.IsSymbolAccessibleWithin(member, type)))
            {
                return true;
            }
        }

        return false;
    }

    private static string Unused(string[] names, HashSet<string> taken)
    {
        foreach (string name in names)
        {
            if (!taken.Contains(name))
            {
                return name;
            }
        }

        string fallback = names[names.Length - 1];
        while (taken.Contains(fallback))
        {
            fallback += "_";
        }

        return fallback;
    }

    /// <summary>
    /// Whether a generated member named <paramref name="name"/> hides one of a base type, and so needs
    /// <c>new</c>: a property or a nested type hides every member of its name, a method only another
    /// method of its signature, which the generated ones never share with anything but a non-method.
    /// A base record whose generated members this compilation does not see yet counts as having them.
    /// </summary>
    private static bool Hides(INamedTypeSymbol type, string name, bool methodsHide, KnownSymbols known, Compilation compilation)
    {
        for (INamedTypeSymbol? level = type.BaseType; level is not null && level.SpecialType != SpecialType.System_Object; level = level.BaseType)
        {
            foreach (ISymbol member in level.GetMembers(name))
            {
                if (compilation.IsSymbolAccessibleWithin(member, type) && (methodsHide || member.Kind != SymbolKind.Method))
                {
                    return true;
                }
            }

            if (methodsHide && known.RecordAttribute is not null && KnownSymbols.HasAttribute(level, known.RecordAttribute)
                && level.Locations.Any(l => l.IsInSource))
            {
                return true;
            }
        }

        return false;
    }

    private static string Keyword(INamedTypeSymbol type) => type switch
    {
        { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
        { IsRecord: true } => "record",
        { TypeKind: TypeKind.Struct } => "struct",
        { TypeKind: TypeKind.Interface } => "interface",
        _ => "class",
    };

    public static string Escape(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    /// <summary>Public, internal, or not applicable when a namespace-level type cannot name <paramref name="type"/>.</summary>
    private static Accessibility Accessible(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return Accessible(array.ElementType);
            case INamedTypeSymbol named:
                Accessibility access = Accessibility.Public;
                for (INamedTypeSymbol? current = named; current is not null; current = current.ContainingType)
                {
                    access = Min(access, current.DeclaredAccessibility switch
                    {
                        Accessibility.Public => Accessibility.Public,
                        Accessibility.Internal or Accessibility.ProtectedOrInternal => Accessibility.Internal,
                        _ => Accessibility.NotApplicable,
                    });
                }

                foreach (ITypeSymbol argument in named.TypeArguments)
                {
                    access = Min(access, Accessible(argument));
                }

                return access;
            default:
                return Accessibility.Public;
        }
    }

    private static Accessibility Min(Accessibility left, Accessibility right) =>
        left == Accessibility.NotApplicable || right == Accessibility.NotApplicable ? Accessibility.NotApplicable
        : left == Accessibility.Internal || right == Accessibility.Internal ? Accessibility.Internal
        : Accessibility.Public;

    private sealed record Candidate(ISymbol Member, IParameterSymbol? Parameter);

    /// <summary>
    /// The private generic helpers the record needs: a nested record's schema and an extension's type
    /// are read through a type parameter, which reaches an explicit implementation as well as an
    /// implicit one.
    /// </summary>
    private sealed class Helpers
    {
        public bool Schema { get; set; }

        public bool Extension { get; set; }
    }

    private sealed record Included(ISymbol Member, IParameterSymbol? Parameter, string ColumnName, ValueModel Value, LocationModel? Location);

    /// <summary>What a <c>[VortexColumn]</c> says, with the attribute's defaults where it is silent.</summary>
    private readonly struct ColumnOptions
    {
        private ColumnOptions(string? name, int precision, int scale, int unit, string? timeZone)
        {
            Name = name;
            Precision = precision;
            Scale = scale;
            Unit = unit;
            TimeZone = timeZone;
        }

        public string? Name { get; }

        public int Precision { get; }

        public int Scale { get; }

        public int Unit { get; }

        public string? TimeZone { get; }

        public static ColumnOptions From(ImmutableArray<AttributeData> attributes, INamedTypeSymbol? columnAttribute)
        {
            string? name = null;
            int precision = 28;
            int scale = 10;
            int unit = 1;
            string? timeZone = null;
            foreach (AttributeData attribute in attributes)
            {
                if (columnAttribute is null || !SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, columnAttribute))
                {
                    continue;
                }

                if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string named)
                {
                    name = named;
                }

                foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
                {
                    switch (argument.Key)
                    {
                        case "Precision" when argument.Value.Value is int value:
                            precision = value;
                            break;
                        case "Scale" when argument.Value.Value is int value:
                            scale = value;
                            break;
                        case "Unit" when argument.Value.Value is not null:
                            unit = Convert.ToInt32(argument.Value.Value, System.Globalization.CultureInfo.InvariantCulture);
                            break;
                        case "TimeZone":
                            timeZone = argument.Value.Value as string;
                            break;
                    }
                }
            }

            return new ColumnOptions(name, precision, scale, unit, timeZone);
        }
    }
}
