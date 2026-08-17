using RimContext.Core.Configuration;
using RimContext.Core.Contracts;
using RimContext.Core.Model;

namespace RimContext.Core.Discovery;

public static class WorkspaceDiscovery
{
    private static readonly HashSet<string> IgnoredDirectories =
        new(IndexConstants.IgnoredDirectoryNames, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<DiscoveredFile> Discover(WorkspaceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var files = new Dictionary<string, DiscoveredFile>(StringComparer.OrdinalIgnoreCase);
        DiscoverRoot(configuration, configuration.RootPath, files, isAssemblyOnly: false, externalRoot: null);

        foreach (var assemblyRoot in configuration.AssemblyRoots)
        {
            DiscoverRoot(configuration, assemblyRoot, files, isAssemblyOnly: true, externalRoot: assemblyRoot);
        }

        return files.Values
            .OrderBy(file => file.DisplayPath, StringComparer.Ordinal)
            .ThenBy(file => file.Kind, StringComparer.Ordinal)
            .ToArray();
    }

    private static void DiscoverRoot(
        WorkspaceConfiguration configuration,
        string root,
        IDictionary<string, DiscoveredFile> files,
        bool isAssemblyOnly,
        string? externalRoot)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<DirectoryInfo> directories;
            IEnumerable<FileInfo> currentFiles;

            try
            {
                var directory = new DirectoryInfo(current);
                directories = directory.EnumerateDirectories()
                    .Where(child => !IgnoredDirectories.Contains(child.Name))
                    .Where(child => !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    .OrderBy(child => child.FullName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                currentFiles = directory.EnumerateFiles().OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw ErrorFactory.InputReadFailed(null, "Workspace discovery could not read a directory.");
            }

            foreach (var child in directories.Reverse())
            {
                pending.Push(child.FullName);
            }

            foreach (var file in currentFiles)
            {
                if (!TryGetKind(file.Name, file.Extension, isAssemblyOnly, out var kind))
                {
                    continue;
                }

                var absolutePath = Path.GetFullPath(file.FullName);
                if (files.ContainsKey(absolutePath))
                {
                    continue;
                }

                var isExternal = externalRoot is not null &&
                                  !PathUtilities.IsWithin(configuration.RootPath, absolutePath);
                var relative = PathUtilities.NormalizeRelativePath(
                    externalRoot ?? configuration.RootPath,
                    absolutePath);
                string displayPath;
                string identityPath;
                string scope;

                if (isExternal)
                {
                    var rootKey = StableEntityId.DigestBase32(
                        externalRoot!.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').ToLowerInvariant())
                        [..16];
                    displayPath = $"external/{rootKey}/{relative}";
                    identityPath = displayPath;
                    scope = $"external:{rootKey}";
                }
                else
                {
                    displayPath = PathUtilities.NormalizeRelativePath(configuration.RootPath, absolutePath);
                    identityPath = displayPath;
                    scope = configuration.WorkspaceIdentity;
                }

                files[absolutePath] = new DiscoveredFile(
                    absolutePath,
                    displayPath,
                    identityPath,
                    scope,
                    kind);
            }
        }
    }

    private static bool TryGetKind(string fileName, string extension, bool isAssemblyOnly, out string kind)
    {
        if (isAssemblyOnly)
        {
            kind = extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? DiscoveredFileKinds.Assembly
                : string.Empty;
            return kind.Length > 0;
        }

        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            kind = DiscoveredFileKinds.Source;
            return true;
        }

        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            kind = DiscoveredFileKinds.Xml;
            return true;
        }

        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase))
        {
            kind = DiscoveredFileKinds.Project;
            return true;
        }

        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            kind = DiscoveredFileKinds.Assembly;
            return true;
        }

        kind = string.Empty;
        return false;
    }
}
