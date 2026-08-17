using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RimContext.Core.Configuration;
using RimContext.Core.Contracts;
using RimContext.Core.Discovery;
using RimContext.Core.Logging;
using RimContext.Core.Model;
using RimContext.Core.Output;
using RimContext.Core.Semantics;

namespace RimContext.Core.Storage;

public sealed class WorkspaceIndexer
{
    public IndexBuildResult Build(
        WorkspaceConfiguration configuration,
        ILogger? logger = null,
        DateTimeOffset? indexedAtUtc = null,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        logger ??= new NullLogger();

        var started = Stopwatch.GetTimestamp();
        var temporaryPath = configuration.StorePath + ".tmp";

        try
        {
            var storeParent = Path.GetDirectoryName(configuration.StorePath);
            if (!string.IsNullOrEmpty(storeParent))
            {
                Directory.CreateDirectory(storeParent);
            }

            using var storeLock = StoreLock.Acquire(configuration.StorePath + ".lock");
            TryDeleteTemporary(temporaryPath);

            var discovered = WorkspaceDiscovery.Discover(configuration);
            var currentFiles = discovered
                .Select(file => Fingerprint(configuration, file))
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .ThenBy(file => file.Id, StringComparer.Ordinal)
                .ToArray();
            var metadata = CreateMetadata(configuration, indexedAtUtc);
            var previous = TryReadCompatibleSnapshot(configuration, out var resetRequired);

            IndexStatistics statistics;
            IndexCounts counts;
            IReadOnlyList<IndexDiagnostic> diagnostics;
            if (previous is not null && !resetRequired)
            {
                var comparison = Compare(previous.Files, currentFiles, force);
                var filesToAnalyze = XmlSemanticIndexer.SelectFilesToAnalyze(
                    currentFiles,
                    previous.Files,
                    comparison.AddedOrChanged,
                    comparison.RemovedFileIds);
                var removedXml = previous.Files.Any(file =>
                    file.Kind == DiscoveredFileKinds.Xml &&
                    comparison.RemovedFileIds.Contains(file.Id, StringComparer.Ordinal));
                var xmlSemantic = filesToAnalyze.Count == 0 && !removedXml
                    ? XmlSemanticIndexer.Empty
                    : XmlSemanticIndexer.Analyze(
                        configuration,
                        currentFiles,
                        filesToAnalyze,
                        previous.Files,
                        previous.Entities,
                        comparison.RemovedFileIds);
                var csharpFilesToAnalyze = CSharpSemanticIndexer.SelectFilesToAnalyze(
                    currentFiles,
                    previous.Files,
                    comparison.AddedOrChanged,
                    comparison.RemovedFileIds,
                    comparison.AddedOrChanged.Any(file => file.Kind == DiscoveredFileKinds.Assembly) ||
                    previous.Files.Any(file =>
                        file.Kind == DiscoveredFileKinds.Assembly &&
                        comparison.RemovedFileIds.Contains(file.Id, StringComparer.Ordinal)));
                var removedCsharp = previous.Files.Any(file =>
                    file.Kind == DiscoveredFileKinds.Source &&
                    comparison.RemovedFileIds.Contains(file.Id, StringComparer.Ordinal));
                var csharpSemantic = csharpFilesToAnalyze.Count == 0 && !removedCsharp
                    ? CSharpSemanticIndexer.Empty
                    : CSharpSemanticIndexer.Analyze(
                        configuration,
                        currentFiles,
                        csharpFilesToAnalyze,
                        previous.Files,
                        previous.Entities,
                        comparison.RemovedFileIds);
                var structureFilesToAnalyze = comparison.AddedOrChanged
                    .Concat(previous.Files.Where(file =>
                        comparison.RemovedFileIds.Contains(file.Id, StringComparer.Ordinal)))
                    .GroupBy(file => file.Id, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToArray();
                var structureChanged = ProjectSemanticIndexer.NeedsAnalysis(
                    previous.Files,
                    comparison.AddedOrChanged,
                    comparison.RemovedFileIds);
                var projectSemantic = !structureChanged
                    ? ProjectSemanticIndexer.Empty
                    : ProjectSemanticIndexer.Analyze(
                        configuration,
                        currentFiles,
                        structureFilesToAnalyze,
                        previous.Files,
                        previous.Entities,
                        xmlSemantic.Entities.Concat(csharpSemantic.Entities).ToArray(),
                        comparison.RemovedFileIds);
                var semantic = CombineSemanticResults(
                    currentFiles,
                    previous.Relations,
                    xmlSemantic,
                    csharpSemantic,
                    projectSemantic);
                var changedIds = comparison.AddedOrChanged
                    .Select(file => file.Id)
                    .ToHashSet(StringComparer.Ordinal);
                var changedFiles = comparison.AddedOrChanged
                    .Select(file => semantic.FileUpdates.TryGetValue(file.Id, out var update) ? update : file)
                    .ToArray();
                var metadataUpdates = comparison.MetadataUpdates
                    .Concat(semantic.FileUpdates.Values.Where(file => !changedIds.Contains(file.Id)))
                    .GroupBy(file => file.Id, StringComparer.Ordinal)
                    .Select(group => group.Last())
                    .OrderBy(file => file.Path, StringComparer.Ordinal)
                    .ToArray();
                var replacementEntities = changedFiles
                    .Concat(semantic.RefreshedFileIds
                        .Select(id => currentFiles.FirstOrDefault(file => file.Id == id))
                        .Where(file => file is not null)
                        .Cast<IndexedFileRecord>())
                    .GroupBy(file => file.Id, StringComparer.Ordinal)
                    .Select(group => CreateMetadataEntity(group.Last()))
                    .Concat(semantic.Entities)
                    .GroupBy(entity => entity.Id, StringComparer.Ordinal)
                    .Select(group => group.Last())
                    .ToArray();

                using var store = IndexStore.OpenWritable(configuration);
                store.ApplyIncremental(
                    changedFiles,
                    metadataUpdates,
                    comparison.RemovedFileIds,
                    replacementEntities,
                    semantic.Relations,
                    metadata,
                    semantic.RefreshedFileIds,
                    semantic.RebuildRelations);
                counts = store.GetCounts();
                statistics = comparison.Statistics;
                diagnostics = semantic.Diagnostics;
            }
            else
            {
                var xmlSemantic = XmlSemanticIndexer.Analyze(
                    configuration,
                    currentFiles,
                    currentFiles.Where(file => file.Kind == DiscoveredFileKinds.Xml).ToArray(),
                    [],
                    [],
                    []);
                var csharpSemantic = CSharpSemanticIndexer.Analyze(
                    configuration,
                    currentFiles,
                    currentFiles.Where(file => file.Kind == DiscoveredFileKinds.Source).ToArray(),
                    [],
                    [],
                    []);
                var projectSemantic = ProjectSemanticIndexer.Analyze(
                    configuration,
                    currentFiles,
                    currentFiles,
                    [],
                    [],
                    xmlSemantic.Entities.Concat(csharpSemantic.Entities).ToArray(),
                    []);
                var semantic = CombineSemanticResults(
                    currentFiles,
                    [],
                    xmlSemantic,
                    csharpSemantic,
                    projectSemantic);
                var indexedFiles = currentFiles
                    .Select(file => semantic.FileUpdates.TryGetValue(file.Id, out var update) ? update : file)
                    .ToArray();
                var entities = indexedFiles
                    .Select(CreateMetadataEntity)
                    .Concat(semantic.Entities)
                    .GroupBy(entity => entity.Id, StringComparer.Ordinal)
                    .Select(group => group.Last())
                    .ToArray();
                using (var store = IndexStore.CreateNew(temporaryPath, metadata))
                {
                    store.WriteBatch(indexedFiles, entities, semantic.Relations);
                }

                ReplaceStore(temporaryPath, configuration.StorePath);
                counts = new IndexCounts(indexedFiles.Length, entities.Length, semantic.Relations.Count);
                statistics = new IndexStatistics(
                    indexedFiles.Length,
                    indexedFiles.Length,
                    0,
                    0,
                    0);
                diagnostics = semantic.Diagnostics;
            }

            var durationMilliseconds = Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            logger.Info(
                $"Indexed {statistics.Scanned} file(s): {statistics.Added} added, " +
                $"{statistics.Changed} changed, {statistics.Removed} removed, " +
                $"{statistics.Unchanged} unchanged.");
            return new IndexBuildResult(
                metadata,
                counts,
                statistics.Scanned,
                statistics,
                durationMilliseconds,
                diagnostics);
        }
        catch (RimContextException)
        {
            TryDeleteTemporary(temporaryPath);
            throw;
        }
        catch (SqliteException ex)
        {
            TryDeleteTemporary(temporaryPath);
            throw ErrorFactory.IndexFailed("The index could not be written.", new { error = ex.SqliteErrorCode });
        }
        catch (IOException ex)
        {
            TryDeleteTemporary(temporaryPath);
            throw ErrorFactory.IndexFailed("The index could not be written.", new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDeleteTemporary(temporaryPath);
            throw ErrorFactory.IndexFailed("The index could not be written.", new { error = ex.Message });
        }
    }

    private static CombinedSemanticResult CombineSemanticResults(
        IReadOnlyList<IndexedFileRecord> currentFiles,
        IReadOnlyList<RelationRecord> previousRelations,
        XmlSemanticResult xml,
        CSharpSemanticResult csharp,
        ProjectSemanticResult project)
    {
        var fileUpdates = xml.FileUpdates
            .Concat(csharp.FileUpdates)
            .Concat(project.FileUpdates)
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        var refreshed = xml.RefreshedFileIds
            .Concat(csharp.RefreshedFileIds)
            .Concat(project.RefreshedFileIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var rebuildRelations = xml.RebuildRelations || csharp.RebuildRelations || project.RebuildRelations;
        var relations = rebuildRelations
            ? MergeRelations(previousRelations, xml, csharp, project)
            : [];
        var diagnostics = xml.Diagnostics
            .Concat(csharp.Diagnostics)
            .Concat(project.Diagnostics)
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Message, StringComparer.Ordinal)
            .ToArray();
        var entities = xml.Entities
            .Concat(csharp.Entities)
            .Concat(project.Entities)
            .Concat(CreateDiagnosticEntities(diagnostics, currentFiles))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        return new CombinedSemanticResult(
            fileUpdates,
            entities,
            relations,
            refreshed,
            rebuildRelations,
            diagnostics);
    }

    private static IReadOnlyList<EntityRecord> CreateDiagnosticEntities(
        IReadOnlyList<IndexDiagnostic> diagnostics,
        IReadOnlyList<IndexedFileRecord> currentFiles)
    {
        return diagnostics
            .Select(diagnostic =>
            {
                var file = currentFiles.FirstOrDefault(item =>
                    item.Path.Equals(diagnostic.Path, StringComparison.OrdinalIgnoreCase));
                var identity = diagnostic.Code + "\0" + diagnostic.Path + "\0" + diagnostic.Message;
                return new EntityRecord(
                    StableEntityId.Create("diagnostic", diagnostic.Path, identity),
                    "diagnostic",
                    identity,
                    file?.Id,
                    null,
                    JsonOutput.SerializePayload(new
                    {
                        code = diagnostic.Code,
                        severity = "error",
                        path = diagnostic.Path,
                        message = diagnostic.Message
                    }));
            })
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<RelationRecord> MergeRelations(
        IReadOnlyList<RelationRecord> previousRelations,
        XmlSemanticResult xml,
        CSharpSemanticResult csharp,
        ProjectSemanticResult project)
    {
        var relations = new List<RelationRecord>();
        if (xml.RebuildRelations)
        {
            relations.AddRange(xml.Relations);
        }
        else
        {
            relations.AddRange(previousRelations.Where(IsXmlRelation));
        }

        if (csharp.RebuildRelations)
        {
            relations.AddRange(csharp.Relations);
        }
        else
        {
            relations.AddRange(previousRelations.Where(IsCSharpRelation));
        }

        if (project.RebuildRelations)
        {
            relations.AddRange(project.Relations);
        }
        else
        {
            relations.AddRange(previousRelations.Where(IsProjectRelation));
        }

        relations.AddRange(previousRelations.Where(relation =>
            !IsXmlRelation(relation) &&
            !IsCSharpRelation(relation) &&
            !IsProjectRelation(relation)));

        return relations
            .GroupBy(relation => relation.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(relation => relation.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsXmlRelation(RelationRecord relation) =>
        relation.Kind is "inheritance" or "def_reference";

    private static bool IsCSharpRelation(RelationRecord relation) =>
        relation.Kind.StartsWith("csharp_", StringComparison.Ordinal) ||
        relation.Kind.StartsWith("harmony_", StringComparison.Ordinal);

    private static bool IsProjectRelation(RelationRecord relation) =>
        relation.Kind is "requires" or
            "load_after" or
            "load_before" or
            "incompatible" or
            "project_reference" or
            "assembly_reference" or
            "owns";

    private static PreviousIndexSnapshot? TryReadCompatibleSnapshot(
        WorkspaceConfiguration configuration,
        out bool resetRequired)
    {
        resetRequired = false;
        if (!File.Exists(configuration.StorePath))
        {
            return null;
        }

        try
        {
            using var store = IndexStore.OpenReadOnly(configuration);
            return new PreviousIndexSnapshot(
                store.GetFiles(),
                store.GetEntities(),
                store.GetRelations());
        }
        catch (RimContextException ex) when (ex.Error.Code == ErrorCodes.RootMismatch)
        {
            resetRequired = true;
            return null;
        }
    }

    private static IndexComparison Compare(
        IReadOnlyList<IndexedFileRecord> previousFiles,
        IReadOnlyList<IndexedFileRecord> currentFiles,
        bool force)
    {
        var previousByPath = previousFiles.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var currentByPath = currentFiles.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var addedOrChanged = new List<IndexedFileRecord>();
        var metadataUpdates = new List<IndexedFileRecord>();
        var removedFileIds = new List<string>();
        var unchanged = 0;

        foreach (var current in currentFiles)
        {
            if (!previousByPath.TryGetValue(current.Path, out var previous))
            {
                addedOrChanged.Add(current);
                continue;
            }

            var contentChanged = force ||
                                 !previous.Id.Equals(current.Id, StringComparison.Ordinal) ||
                                 !previous.Kind.Equals(current.Kind, StringComparison.Ordinal) ||
                                 !previous.ContentHash.Equals(current.ContentHash, StringComparison.Ordinal);
            if (contentChanged)
            {
                addedOrChanged.Add(current);
                if (previous.Id != current.Id || previous.Kind != current.Kind)
                {
                    removedFileIds.Add(previous.Id);
                }

                continue;
            }

            unchanged++;
            if (previous.SizeBytes != current.SizeBytes ||
                previous.ModifiedUtcTicks != current.ModifiedUtcTicks)
            {
                metadataUpdates.Add(current with
                {
                    ParseStatus = previous.ParseStatus,
                    Diagnostic = previous.Diagnostic
                });
            }
        }

        foreach (var previous in previousFiles)
        {
            if (!currentByPath.ContainsKey(previous.Path))
            {
                removedFileIds.Add(previous.Id);
            }
        }

        return new IndexComparison(
            addedOrChanged,
            metadataUpdates,
            removedFileIds,
            new IndexStatistics(
                currentFiles.Count,
                addedOrChanged.Count(file => !previousByPath.ContainsKey(file.Path)),
                addedOrChanged.Count(file => previousByPath.ContainsKey(file.Path)),
                removedFileIds.Count,
                unchanged));
    }

    private static StoreMetadata CreateMetadata(
        WorkspaceConfiguration configuration,
        DateTimeOffset? indexedAtUtc)
    {
        return new StoreMetadata(
            IndexConstants.SchemaVersion,
            IndexConstants.ToolVersion,
            configuration.WorkspaceIdentity,
            configuration.RootPath,
            configuration.ConfigurationFingerprint,
            (indexedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O"));
    }

    private static EntityRecord CreateMetadataEntity(IndexedFileRecord record)
    {
        var entityKind = record.Kind == DiscoveredFileKinds.Project
            ? "project_file"
            : record.Kind;
        return new EntityRecord(
            record.Id,
            entityKind,
            $"{record.WorkspaceIdentity}\0{record.Path}",
            record.Id,
            null,
            JsonOutput.SerializePayload(new
            {
                path = record.Path,
                hash = record.ContentHash,
                sizeBytes = record.SizeBytes,
                metadataOnly = true
            }));
    }

    private static IndexedFileRecord Fingerprint(WorkspaceConfiguration configuration, DiscoveredFile file)
    {
        try
        {
            var info = new FileInfo(file.AbsolutePath);
            using var stream = info.OpenRead();
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var id = StableEntityId.Create(file.Kind, file.Scope, file.IdentityPath);
            return new IndexedFileRecord(
                id,
                file.Kind,
                file.DisplayPath,
                hash,
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                configuration.WorkspaceIdentity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw ErrorFactory.InputReadFailed(file.DisplayPath, "The discovered file could not be fingerprinted.");
        }
    }

    private static void ReplaceStore(string temporaryPath, string storePath)
    {
        if (File.Exists(storePath))
        {
            try
            {
                File.Replace(temporaryPath, storePath, null);
                return;
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (IOException)
            {
            }
        }

        File.Move(temporaryPath, storePath, overwrite: true);
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record IndexComparison(
        IReadOnlyList<IndexedFileRecord> AddedOrChanged,
        IReadOnlyList<IndexedFileRecord> MetadataUpdates,
        IReadOnlyList<string> RemovedFileIds,
        IndexStatistics Statistics);

    private sealed record PreviousIndexSnapshot(
        IReadOnlyList<IndexedFileRecord> Files,
        IReadOnlyList<EntityRecord> Entities,
        IReadOnlyList<RelationRecord> Relations);

    private sealed record CombinedSemanticResult(
        IReadOnlyDictionary<string, IndexedFileRecord> FileUpdates,
        IReadOnlyList<EntityRecord> Entities,
        IReadOnlyList<RelationRecord> Relations,
        IReadOnlyList<string> RefreshedFileIds,
        bool RebuildRelations,
        IReadOnlyList<IndexDiagnostic> Diagnostics);
}
