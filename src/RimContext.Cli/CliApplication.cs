using System.Text.Json;
using System.Text.Json.Serialization;
using RimContext.Core.Configuration;
using RimContext.Core.Contracts;
using RimContext.Core.Logging;
using RimContext.Core.Model;
using RimContext.Core.Output;
using RimContext.Core.Semantics;
using RimContext.Core.Storage;

namespace RimContext.Cli;

public static class CliApplication
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var logger = new TextWriterLogger(stderr);
        var command = GetCommandForError(args);
        try
        {
            var request = CliParser.Parse(args);
            command = request.Command;
            var envelope = Execute(request, logger);
            JsonOutput.Write(stdout, envelope);
            return 0;
        }
        catch (RimContextException ex)
        {
            JsonOutput.Write(stdout, JsonOutput.Error(command, ex.Error));
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            logger.Error($"{ex.GetType().Name}: {ex.Message}");
            var error = ErrorFactory.Internal("An unexpected internal error occurred.").Error;
            JsonOutput.Write(stdout, JsonOutput.Error(command, error));
            return 10;
        }
    }

    private static JsonEnvelope Execute(CliRequest request, ILogger logger) => request.Command switch
    {
        CliCommands.Help => JsonOutput.Success(CliCommands.Help, new
        {
            commands = CliCommands.All,
            usage = "rimctx <command> [options] --json"
        }),
        CliCommands.Version => JsonOutput.Success(CliCommands.Version, new VersionResponse(
            IndexConstants.ToolVersion,
            IndexConstants.SchemaVersionText)),
        CliCommands.Index => ExecuteIndex(request, logger),
        CliCommands.Summary => ExecuteSummary(request),
        CliCommands.Find => ExecuteFind(request),
        CliCommands.Definition => ExecuteDefinition(request),
        CliCommands.Refs => ExecuteRefs(request),
        CliCommands.Harmony => ExecuteHarmony(request),
        CliCommands.File => ExecuteFile(request),
        CliCommands.Affected => ExecuteAffected(request),
        _ when CliCommands.IsQuery(request.Command) => throw ErrorFactory.NotImplemented(request.Command),
        _ => throw ErrorFactory.InvalidArgument($"Unknown command '{request.Command}'.")
    };

    private static JsonEnvelope ExecuteIndex(CliRequest request, ILogger logger)
    {
        var configuration = WorkspaceConfiguration.Resolve(request.Root, request.Store, request.AssemblyRoots);
        var result = new WorkspaceIndexer().Build(configuration, logger, force: request.Force);
        var data = new
        {
            files = new
            {
                scanned = result.Statistics.Scanned,
                added = result.Statistics.Added,
                changed = result.Statistics.Changed,
                removed = result.Statistics.Removed,
                unchanged = result.Statistics.Unchanged
            },
            duration_ms = result.DurationMilliseconds
        };
        var diagnostics = result.Diagnostics ?? [];
        if (diagnostics.Count == 0)
        {
            return JsonOutput.Success(CliCommands.Index, data);
        }

        return JsonOutput.Partial(
            CliCommands.Index,
            data,
            warnings: diagnostics
                .Select(diagnostic => new JsonWarning(
                    diagnostic.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        ? "CSHARP_PARSE"
                        : diagnostic.Code == "INDEX"
                            ? "XML_PARSE"
                            : diagnostic.Code,
                    diagnostic.Message,
                    diagnostic.Path))
                .ToArray());
    }

    private static JsonEnvelope ExecuteSummary(CliRequest request)
    {
        var configuration = WorkspaceConfiguration.Resolve(request.Root, request.Store, request.AssemblyRoots);
        using var store = IndexStore.OpenReadOnly(configuration);
        var counts = store.GetCounts();
        var files = store.GetFiles();
        var entities = store.GetEntities();
        var diagnosticCounts = CountDiagnostics(files, entities);
        return JsonOutput.Success(CliCommands.Summary, new SummaryResponse(
            store.Metadata.SchemaVersion,
            store.Metadata.ToolVersion,
            store.Metadata.WorkspaceIdentity,
            store.Metadata.IndexedAtUtc,
            configuration.StoreDisplayPath(),
            counts.FileCount,
            counts.EntityCount,
            counts.RelationCount,
            entities.LongCount(item => item.Kind == "mod"),
            entities.LongCount(item => item.Kind == "project"),
            files.LongCount(item => item.Kind == "source_file"),
            files.LongCount(item => item.Kind == "xml_file"),
            entities.LongCount(item => item.Kind == "def"),
            entities.LongCount(item => item.Kind == "harmony_patch"),
            diagnosticCounts));
    }

    private static DiagnosticCounts CountDiagnostics(
        IReadOnlyList<IndexedFileRecord> files,
        IReadOnlyList<EntityRecord> entities)
    {
        var diagnostics = entities
            .Where(item => item.Kind == "diagnostic")
            .Select(item => ParseDiagnostic(item.PayloadJson))
            .Where(item => item is not null)
            .Cast<DiagnosticEntry>()
            .ToArray();
        if (diagnostics.Length == 0)
        {
            return new DiagnosticCounts(
                files.LongCount(item => item.ParseStatus == "error"),
                0);
        }

        return new DiagnosticCounts(
            diagnostics.LongCount(item => item.Severity == "error"),
            diagnostics.LongCount(item => item.Severity == "warning"));
    }

    private static DiagnosticEntry? ParseDiagnostic(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var severity = root.TryGetProperty("severity", out var value)
                ? value.GetString()
                : null;
            return severity is null ? null : new DiagnosticEntry(severity);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonEnvelope ExecuteFind(CliRequest request)
    {
        var configuration = WorkspaceConfiguration.Resolve(request.Root, request.Store, request.AssemblyRoots);
        using var store = IndexStore.OpenReadOnly(configuration);
        var engine = new SemanticQueryEngine(store);
        return JsonOutput.Success(
            CliCommands.Find,
            results: engine.FindResults(request.Subject!, request.Limit, request.Kind));
    }

    private static JsonEnvelope ExecuteDefinition(CliRequest request)
    {
        var configuration = WorkspaceConfiguration.Resolve(request.Root, request.Store, request.AssemblyRoots);
        using var store = IndexStore.OpenReadOnly(configuration);
        var engine = new SemanticQueryEngine(store);
        return JsonOutput.Success(
            CliCommands.Definition,
            results: engine.FindDefinitionResults(request.Subject!, request.Limit));
    }

    private static JsonEnvelope ExecuteRefs(CliRequest request)
    {
        var configuration = WorkspaceConfiguration.Resolve(request.Root, request.Store, request.AssemblyRoots);
        using var store = IndexStore.OpenReadOnly(configuration);
        var engine = new SemanticQueryEngine(store);
        var references = engine.FindReferences(request.Subject!, request.Limit, request.Direction);
        return JsonOutput.Success(
            CliCommands.Refs,
            data: new
            {
                incoming = references.Incoming,
                outgoing = references.Outgoing
            });
    }

    private static JsonEnvelope ExecuteFile(CliRequest request)
    {
        var configuration = WorkspaceConfiguration.Resolve(request.Root, request.Store, request.AssemblyRoots);
        using var store = IndexStore.OpenReadOnly(configuration);
        var engine = new SemanticQueryEngine(store);
        return JsonOutput.Success(
            CliCommands.File,
            results: engine.FindFiles(request.Subject!, request.Limit));
    }

    private static JsonEnvelope ExecuteHarmony(CliRequest request)
    {
        var configuration = WorkspaceConfiguration.Resolve(request.Root, request.Store, request.AssemblyRoots);
        using var store = IndexStore.OpenReadOnly(configuration);
        var engine = new SemanticQueryEngine(store);
        return JsonOutput.Success(
            CliCommands.Harmony,
            results: engine.FindHarmony(request.Subject, request.File, request.Limit));
    }

    private static JsonEnvelope ExecuteAffected(CliRequest request)
    {
        var configuration = WorkspaceConfiguration.Resolve(request.Root, request.Store, request.AssemblyRoots);
        using var store = IndexStore.OpenReadOnly(configuration);
        var engine = new SemanticQueryEngine(store);
        return JsonOutput.Success(
            CliCommands.Affected,
            data: engine.FindAffected(request.Inputs, configuration.RootPath, request.Depth, request.Limit));
    }

    private static string GetCommandForError(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return CliCommands.Help;
        }

        var candidate = args[0].Trim().ToLowerInvariant();
        return candidate.StartsWith("--", StringComparison.Ordinal) ? "unknown" : candidate;
    }

    private sealed record VersionResponse(string ToolVersion, string SchemaVersion);

    private sealed record SummaryResponse(
        int SchemaVersion,
        string ToolVersion,
        string WorkspaceId,
        string IndexedAtUtc,
        string Store,
        long FileCount,
        long EntityCount,
        long RelationCount,
        long Mods,
        long Projects,
        [property: JsonPropertyName("source_files")]
        long SourceFiles,
        [property: JsonPropertyName("xml_files")]
        long XmlFiles,
        long Defs,
        [property: JsonPropertyName("harmony_patches")]
        long HarmonyPatches,
        DiagnosticCounts Diagnostics);

    private sealed record DiagnosticCounts(long Error, long Warning);

    private sealed record DiagnosticEntry(string Severity);
}
