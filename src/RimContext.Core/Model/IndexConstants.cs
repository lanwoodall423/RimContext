namespace RimContext.Core.Model;

public static class IndexConstants
{
    public const int SchemaVersion = 1;
    public const string SchemaVersionText = "rimctx/v1";
    public const string ToolVersion = "0.1.0";
    public const string SemanticIndexerVersion = "xml-csharp-harmony-v1";
    public const int DefaultLimit = 20;
    public const int MaximumLimit = 100;
    public const int MaximumOutputBytes = 1_048_576;
    public const int DefaultAffectedDepth = 1;

    public static readonly string[] IgnoredDirectoryNames =
    [
        ".git",
        ".vs",
        "bin",
        "obj",
        "artifacts",
        ".rimctx"
    ];
}
