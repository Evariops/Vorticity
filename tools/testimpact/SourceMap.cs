using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Vorticity.Tools.TestImpact;

/// <summary>The lines of a source file one method covers, and the method's key.</summary>
/// <param name="First">The first line with a sequence point.</param>
/// <param name="Last">The last line with a sequence point.</param>
/// <param name="Key">The method, as <see cref="JitSummary"/> names it.</param>
/// <param name="Virtual">Whether code that did not change can call it through a slot: an override, an interface method.</param>
internal readonly record struct MethodSpan(int First, int Last, string Key, bool Virtual);

/// <summary>
/// Which method each line of the repository's sources belongs to, read from the portable PDBs of
/// its built assemblies: a method's sequence points give the lines it covers, its metadata its
/// name. A lambda, a local function and the state machine of an async method are methods of their
/// own, with the lines of their bodies.
/// </summary>
internal sealed class SourceMap
{
    private readonly Dictionary<string, List<MethodSpan>> _byFile = new(StringComparer.Ordinal);

    /// <summary>The methods of a file, by its path from the repository root, in no order.</summary>
    internal IReadOnlyList<MethodSpan> Methods(string path) =>
        _byFile.TryGetValue(path, out List<MethodSpan>? spans) ? spans : [];

    /// <summary>Adds the methods of one assembly, when it is built and has its PDB beside it.</summary>
    /// <param name="assembly">The path of the assembly.</param>
    /// <param name="root">The repository root, which document paths are made relative to.</param>
    /// <returns>Whether the assembly was read.</returns>
    internal bool Add(string assembly, string root)
    {
        string pdbPath = Path.ChangeExtension(assembly, ".pdb");
        if (!File.Exists(assembly) || !File.Exists(pdbPath))
        {
            return false;
        }

        using FileStream image = File.OpenRead(assembly);
        using PEReader pe = new PEReader(image);
        MetadataReader metadata = pe.GetMetadataReader();
        using FileStream symbols = File.OpenRead(pdbPath);
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(symbols);
        MetadataReader pdb = provider.GetMetadataReader();

        Dictionary<DocumentHandle, string> documents = [];
        foreach (MethodDebugInformationHandle handle in pdb.MethodDebugInformation)
        {
            MethodDebugInformation information = pdb.GetMethodDebugInformation(handle);
            if (information.SequencePointsBlob.IsNil)
            {
                continue;
            }

            MethodDefinition method = metadata.GetMethodDefinition(handle.ToDefinitionHandle());
            string key = TypeName(metadata, method.GetDeclaringType()) + ":" + metadata.GetString(method.Name);
            bool isVirtual = (method.Attributes & MethodAttributes.Virtual) != 0;

            // A method's points are in one document but for rare generated code; each document gets
            // the span of the points that are in it.
            Dictionary<DocumentHandle, (int First, int Last)> spans = [];
            foreach (SequencePoint point in information.GetSequencePoints())
            {
                if (point.IsHidden)
                {
                    continue;
                }

                spans[point.Document] = spans.TryGetValue(point.Document, out (int First, int Last) span)
                    ? (Math.Min(span.First, point.StartLine), Math.Max(span.Last, point.EndLine))
                    : (point.StartLine, point.EndLine);
            }

            foreach ((DocumentHandle document, (int first, int last)) in spans)
            {
                if (!documents.TryGetValue(document, out string? path))
                {
                    path = Relative(pdb.GetString(pdb.GetDocument(document).Name), root);
                    documents[document] = path;
                }

                if (!_byFile.TryGetValue(path, out List<MethodSpan>? list))
                {
                    list = [];
                    _byFile[path] = list;
                }

                list.Add(new MethodSpan(first, last, key, isVirtual));
            }
        }

        return true;
    }

    /// <summary>The full name of a type as the JIT prints it: namespace, then nested types after a plus.</summary>
    private static string TypeName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        TypeDefinition type = metadata.GetTypeDefinition(handle);
        string name = metadata.GetString(type.Name);
        TypeDefinitionHandle declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return TypeName(metadata, declaring) + "+" + name;
        }

        string space = metadata.GetString(type.Namespace);
        return space.Length == 0 ? name : space + "." + name;
    }

    /// <summary>A document path from the repository root, with forward slashes.</summary>
    private static string Relative(string path, string root)
    {
        string normalized = path.Replace('\\', '/');
        string prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
        return normalized.StartsWith(prefix, StringComparison.Ordinal) ? normalized[prefix.Length..] : normalized;
    }
}
