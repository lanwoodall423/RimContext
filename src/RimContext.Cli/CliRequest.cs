using RimContext.Core.Model;

namespace RimContext.Cli;

public sealed record CliRequest(
    string Command,
    string? Subject,
    IReadOnlyList<string> Inputs,
    string? Root,
    string? Store,
    IReadOnlyList<string> AssemblyRoots,
    bool Force,
    bool Json,
    int Limit,
    int Depth,
    string Direction,
    string? Kind,
    string? File);

public static class CliCommands
{
    public const string Help = "help";
    public const string Version = "version";
    public const string Index = "index";
    public const string Find = "find";
    public const string Refs = "refs";
    public const string Definition = "definition";
    public const string Affected = "affected";
    public const string Harmony = "harmony";
    public const string File = "file";
    public const string Summary = "summary";

    public static readonly string[] All =
    [
        Index,
        Find,
        Refs,
        Definition,
        Affected,
        Harmony,
        File,
        Summary,
        Version
    ];

    public static bool IsQuery(string command) => command is Find or Refs or Definition or Affected or Harmony or File;
}
