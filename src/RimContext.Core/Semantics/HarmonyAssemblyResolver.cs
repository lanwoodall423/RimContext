using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using RimContext.Core.Configuration;
using RimContext.Core.Discovery;
using RimContext.Core.Storage;

namespace RimContext.Core.Semantics;

internal sealed class HarmonyAssemblyResolver
{
    private readonly IReadOnlyList<AssemblyTypeInfo> types;

    private HarmonyAssemblyResolver(IReadOnlyList<AssemblyTypeInfo> types)
    {
        this.types = types;
    }

    public static HarmonyAssemblyResolver Create(
        WorkspaceConfiguration configuration,
        IReadOnlyList<IndexedFileRecord> files)
    {
        var paths = files
            .Where(file => file.Kind == DiscoveredFileKinds.Assembly)
            .Select(file => Path.Combine(
                configuration.RootPath,
                file.Path.Replace('/', Path.DirectorySeparatorChar)))
            .Where(File.Exists)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var root in configuration.AssemblyRoots)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                             .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                                            path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                {
                    paths.Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        var types = paths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .SelectMany(ReadTypes)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();
        return new HarmonyAssemblyResolver(types);
    }

    public bool TryResolve(
        string targetType,
        string? targetMember,
        IReadOnlyList<string> targetSignature)
    {
        var normalizedType = NormalizeTypeName(targetType);
        var candidates = types
            .Where(type => string.Equals(type.Name, normalizedType, StringComparison.Ordinal) ||
                           type.Name.EndsWith("." + normalizedType, StringComparison.Ordinal))
            .ToArray();
        if (candidates.Length != 1)
        {
            return false;
        }

        if (targetMember is null)
        {
            return true;
        }

        var members = candidates[0].Members
            .Where(member => member.Name == targetMember)
            .Where(member => targetSignature.Count == 0 ||
                             member.ParameterCount == targetSignature.Count)
            .ToArray();
        return members.Length == 1;
    }

    private static IReadOnlyList<AssemblyTypeInfo> ReadTypes(string path)
    {
        var result = new List<AssemblyTypeInfo>();
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            if (!reader.HasMetadata)
            {
                return result;
            }

            var metadata = reader.GetMetadataReader();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var definition = metadata.GetTypeDefinition(handle);
                var name = FullTypeName(metadata, handle);
                if (name.Length == 0)
                {
                    continue;
                }

                var members = definition.GetMethods()
                    .Select(methodHandle => metadata.GetMethodDefinition(methodHandle))
                    .Select(method => new AssemblyMemberInfo(
                        metadata.GetString(method.Name),
                        method.GetParameters()
                            .Select(metadata.GetParameter)
                            .Count(parameter => parameter.SequenceNumber > 0)))
                    .ToArray();
                result.Add(new AssemblyTypeInfo(name, members));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   BadImageFormatException or InvalidDataException)
        {
        }

        return result;
    }

    private static string FullTypeName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = StripArity(reader.GetString(definition.Name));
        var declaring = definition.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return FullTypeName(reader, declaring) + "." + name;
        }

        var @namespace = reader.GetString(definition.Namespace);
        return @namespace.Length == 0 ? name : @namespace + "." + name;
    }

    private static string NormalizeTypeName(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("global::", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(8);
        }

        var generic = normalized.IndexOf('<');
        if (generic >= 0)
        {
            normalized = normalized.Substring(0, generic);
        }

        while (normalized.EndsWith("[]", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(0, normalized.Length - 2);
        }

        return normalized.Replace('/', '.');
    }

    private static string StripArity(string value)
    {
        var tick = value.IndexOf((char)96);
        return tick < 0 ? value : value.Substring(0, tick);
    }

    private sealed record AssemblyTypeInfo(
        string Name,
        IReadOnlyList<AssemblyMemberInfo> Members);

    private sealed record AssemblyMemberInfo(
        string Name,
        int ParameterCount);
}
