using System.Text;
using RimContext.Core.Contracts;
using RimContext.Core.Discovery;
using RimContext.Core.Model;

namespace RimContext.Core.Configuration;

public sealed record WorkspaceConfiguration
{
    private WorkspaceConfiguration(
        string rootPath,
        string storePath,
        string workspaceIdentity,
        string configurationFingerprint,
        IReadOnlyList<string> assemblyRoots)
    {
        RootPath = rootPath;
        StorePath = storePath;
        WorkspaceIdentity = workspaceIdentity;
        ConfigurationFingerprint = configurationFingerprint;
        AssemblyRoots = assemblyRoots;
    }

    public string RootPath { get; }

    public string StorePath { get; }

    public string WorkspaceIdentity { get; }

    public string ConfigurationFingerprint { get; }

    public IReadOnlyList<string> AssemblyRoots { get; }

    public static WorkspaceConfiguration Resolve(
        string? rootPath = null,
        string? storePath = null,
        IEnumerable<string>? assemblyRoots = null)
    {
        var root = PathUtilities.CanonicalizeDirectory(rootPath ?? Directory.GetCurrentDirectory());
        var store = PathUtilities.CanonicalizeFilePath(
            storePath ?? Path.Combine(root, ".rimctx", "index.sqlite"),
            root);

        var rimContextRoot = Path.Combine(root, ".rimctx");
        if (PathUtilities.IsWithin(root, store) && !PathUtilities.IsWithin(rimContextRoot, store))
        {
            throw ErrorFactory.InvalidArgument("The index store must be outside the indexed tree or inside <root>/.rimctx.");
        }

        var canonicalAssemblyRoots = (assemblyRoots ?? [])
            .Select(value => PathUtilities.CanonicalizeDirectory(value, root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var workspaceIdentity = StableEntityId.Create("workspace", "root", NormalizeIdentityPath(root));
        var fingerprintInput = new StringBuilder()
            .Append("root\0")
            .Append(NormalizeIdentityPath(root))
            .Append('\0')
            .Append("assembly-roots\0")
            .AppendJoin('\0', canonicalAssemblyRoots.Select(NormalizeIdentityPath))
            .Append('\0')
            .Append("schema\0")
            .Append(IndexConstants.SchemaVersion)
            .Append('\0')
            .Append("tool\0")
            .Append(IndexConstants.ToolVersion)
            .Append('\0')
            .Append("semantic-indexer\0")
            .Append(IndexConstants.SemanticIndexerVersion)
            .Append('\0')
            .Append("ignored-directories\0")
            .AppendJoin('\0', IndexConstants.IgnoredDirectoryNames)
            .Append('\0')
            .Append("candidate-kinds\0")
            .AppendJoin('\0', DiscoveredFileKinds.All)
            .ToString();

        return new WorkspaceConfiguration(
            root,
            store,
            workspaceIdentity,
            StableEntityId.DigestBase32(fingerprintInput),
            canonicalAssemblyRoots);
    }

    public string RelativeOrExternalPath(string absolutePath)
    {
        var canonical = Path.GetFullPath(absolutePath);
        if (PathUtilities.IsWithin(RootPath, canonical))
        {
            return PathUtilities.NormalizeRelativePath(RootPath, canonical);
        }

        return canonical.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    public string StoreDisplayPath() => PathUtilities.DisplayPath(RootPath, StorePath);

    private static string NormalizeIdentityPath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').ToLowerInvariant();
}
