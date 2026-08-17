namespace RimContext.Core.Discovery;

public static class DiscoveredFileKinds
{
    public const string Source = "source_file";
    public const string Xml = "xml_file";
    public const string Project = "project";
    public const string Assembly = "assembly";

    public static IReadOnlyList<string> All { get; } = [Source, Xml, Project, Assembly];
}

public sealed record DiscoveredFile(
    string AbsolutePath,
    string DisplayPath,
    string IdentityPath,
    string Scope,
    string Kind);
