using RimContext.Core.Contracts;
using RimContext.Core.Model;

namespace RimContext.Core.Configuration;

public static class PathUtilities
{
    public static string CanonicalizeDirectory(string path, string? relativeTo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var candidate = MakeAbsolute(path, relativeTo);
        if (!Directory.Exists(candidate))
        {
            throw ErrorFactory.PathNotFound();
        }

        return TrimDirectorySeparator(Path.GetFullPath(candidate));
    }

    public static string CanonicalizeFilePath(string path, string? relativeTo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var candidate = MakeAbsolute(path, relativeTo);
        return TrimDirectorySeparator(Path.GetFullPath(candidate));
    }

    public static string NormalizeRelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".")
        {
            return string.Empty;
        }

        try
        {
            return StableEntityId.NormalizePath(relative);
        }
        catch (ArgumentException ex)
        {
            throw ErrorFactory.InvalidArgument(ex.Message);
        }
    }

    public static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ||
               (!relative.Equals("..", StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !relative.StartsWith("../", StringComparison.Ordinal));
    }

    public static string DisplayPath(string root, string path)
    {
        if (IsWithin(root, path))
        {
            return NormalizeRelativePath(root, path);
        }

        return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string MakeAbsolute(string path, string? relativeTo)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.Combine(relativeTo ?? Directory.GetCurrentDirectory(), path);
    }

    private static string TrimDirectorySeparator(string path)
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root) && path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
