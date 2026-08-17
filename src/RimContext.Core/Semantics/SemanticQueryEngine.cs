using System.Text.Json;
using System.Text.Json.Serialization;
using RimContext.Core.Configuration;
using RimContext.Core.Contracts;
using RimContext.Core.Storage;

namespace RimContext.Core.Semantics;

public sealed record DefinitionMatch(
    string Kind,
    string Id,
    string DefType,
    string? DefName,
    string File,
    int? Line,
    string? Parent,
    string? Mod);

public sealed record CSharpTypeMatch(
    string Kind,
    string Id,
    string Name,
    string QualifiedName,
    string TypeKind,
    string Namespace,
    string File,
    int? Line,
    int Members,
    string Accessibility,
    bool IsStatic,
    bool IsPartial,
    string? BaseType,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<string> Attributes);

public sealed record CSharpMemberMatch(
    string Kind,
    string Id,
    string Name,
    string QualifiedName,
    string MemberKind,
    string Signature,
    string ContainingType,
    string File,
    int? Line,
    string Accessibility,
    bool IsStatic,
    IReadOnlyList<string> Attributes);

public sealed record ReferenceMatch(
    string Id,
    string Kind,
    string Direction,
    string FromId,
    string? ToId,
    string? Target,
    string? Field,
    string? Confidence,
    string? File,
    int? Line);

public sealed record ReferenceResult(
    IReadOnlyList<ReferenceMatch> Incoming,
    IReadOnlyList<ReferenceMatch> Outgoing);

public sealed record QueryPage<T>(
    IReadOnlyList<T> Items,
    int Count,
    bool Truncated);

public sealed record ReferenceQueryPage(
    ReferenceResult Result,
    int Count,
    bool Truncated,
    bool Found);

public sealed record FileEntitySummary(
    string Kind,
    string Id,
    string Name,
    int? Line);

public sealed record FileSummaryMatch(
    string Kind,
    string Id,
    string Path,
    string Hash,
    string ParseStatus,
    int EntityCount,
    IReadOnlyList<FileEntitySummary> Entities);

public sealed record HarmonyPatchMatch(
    string Id,
    string Kind,
    string Method,
    string File,
    int? Line,
    bool Resolved,
    string PatchClass,
    string? TargetMember,
    IReadOnlyList<string> TargetSignature,
    string ResolutionState,
    string Confidence);

public sealed record HarmonyTargetMatch(
    string Target,
    IReadOnlyList<HarmonyPatchMatch> Patches);

public sealed record AffectedMatch(
    string Kind,
    string Id,
    string? Name,
    string? File,
    int? Line,
    string? Reason,
    string? Confidence);

public sealed record AffectedResult(
    IReadOnlyList<string> Changed,
    IReadOnlyList<AffectedMatch> Direct,
    IReadOnlyList<AffectedMatch> Dependent,
    [property: JsonPropertyName("runtime_risk")] IReadOnlyList<AffectedMatch> RuntimeRisk,
    bool Truncated);

public sealed record ModMatch(
    string Kind,
    string Id,
    string? PackageId,
    string? Name,
    string File,
    string ModRoot,
    IReadOnlyList<string> SupportedVersions,
    IReadOnlyList<string> ModDependencies,
    IReadOnlyList<string> LoadAfter,
    IReadOnlyList<string> LoadBefore,
    IReadOnlyList<string> IncompatibleWith);

public sealed record ProjectMatch(
    string Kind,
    string Id,
    string Name,
    string File,
    string ProjectKind,
    IReadOnlyList<string> TargetFrameworks,
    string? RootNamespace,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<string> AssemblyReferences,
    string StaticEvaluation);

public sealed class SemanticQueryEngine
{
    private readonly IReadOnlyList<IndexedFileRecord> files;
    private readonly IReadOnlyDictionary<string, string> filePaths;
    private readonly IReadOnlyList<EntityRecord> entities;
    private readonly IReadOnlyList<DefinitionModel> definitions;
    private readonly IReadOnlyList<CSharpTypeModel> types;
    private readonly IReadOnlyList<CSharpMemberModel> members;
    private readonly IReadOnlyList<HarmonyPatchModel> harmonyPatches;
    private readonly IReadOnlyList<ModModel> mods;
    private readonly IReadOnlyList<ProjectModel> projects;
    private readonly IReadOnlyList<RelationRecord> relations;

    public SemanticQueryEngine(IndexStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        files = store.GetFiles();
        filePaths = files.ToDictionary(file => file.Id, file => file.Path, StringComparer.Ordinal);
        entities = store.GetEntities();
        definitions = entities
            .Where(entity => entity.Kind == "def")
            .Select(ParseDefinition)
            .Where(item => item is not null)
            .Cast<DefinitionModel>()
            .OrderBy(item => item.DefType, StringComparer.Ordinal)
            .ThenBy(item => item.DefName, StringComparer.Ordinal)
            .ThenBy(item => item.File, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        types = entities
            .Where(entity => entity.Kind == "csharp_type")
            .Select(ParseType)
            .Where(item => item is not null)
            .Cast<CSharpTypeModel>()
            .OrderBy(item => item.QualifiedName, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        members = entities
            .Where(entity => entity.Kind == "csharp_member")
            .Select(ParseMember)
            .Where(item => item is not null)
            .Cast<CSharpMemberModel>()
            .OrderBy(item => item.QualifiedName, StringComparer.Ordinal)
            .ThenBy(item => item.Signature, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        harmonyPatches = entities
            .Where(entity => entity.Kind == "harmony_patch")
            .Select(ParseHarmonyPatch)
            .Where(item => item is not null)
            .Cast<HarmonyPatchModel>()
            .OrderBy(item => item.Target, StringComparer.Ordinal)
            .ThenBy(item => item.File, StringComparer.Ordinal)
            .ThenBy(item => item.Line ?? int.MaxValue)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        mods = entities
            .Where(entity => entity.Kind == "mod")
            .Select(ParseMod)
            .Where(item => item is not null)
            .Cast<ModModel>()
            .OrderBy(item => item.PackageId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ModRoot, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        projects = entities
            .Where(entity => entity.Kind == "project")
            .Select(ParseProject)
            .Where(item => item is not null)
            .Cast<ProjectModel>()
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.File, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        relations = store.GetRelations();
    }

    public IReadOnlyList<DefinitionMatch> FindDefinitions(string selector, int limit)
    {
        return FindResultsPage(selector, limit, "def")
            .Items
            .OfType<DefinitionMatch>()
            .ToArray();
    }

    public IReadOnlyList<object> FindResults(string selector, int limit, string? kind = null)
    {
        return FindResultsPage(selector, limit, kind).Items;
    }

    public QueryPage<object> FindResultsPage(string selector, int limit, string? kind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var candidates = BuildCandidates(selector, kind);
        var ordered = candidates
            .OrderBy(item => item.Score)
            .ThenBy(item => KindOrder(item.Kind))
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Label, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Result)
            .ToArray();
        return Page(ordered, limit);
    }

    public IReadOnlyList<object> FindDefinitionResults(string selector, int limit)
    {
        return FindDefinitionResultsPage(selector, limit).Items;
    }

    public QueryPage<object> FindDefinitionResultsPage(string selector, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var ordered = BuildCandidates(selector, null)
            .Where(item => item.Score == 0)
            .OrderBy(item => KindOrder(item.Kind))
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Label, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Select(item => item.Result)
            .ToArray();
        return Page(ordered, limit);
    }

    public ReferenceResult FindReferences(string selector, int limit, string direction = "both")
    {
        return FindReferencesPage(selector, limit, direction).Result;
    }

    public ReferenceQueryPage FindReferencesPage(string selector, int limit, string direction = "both")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var candidates = BuildCandidates(selector, null)
            .Where(item => item.Score == 0)
            .ToArray();
        if (candidates.Length > 1)
        {
            throw ErrorFactory.AmbiguousEntity(
                "The selector matches multiple entities; qualify it.",
                new { selector, matches = candidates.Length });
        }

        if (candidates.Length == 0)
        {
            return new ReferenceQueryPage(new ReferenceResult([], []), 0, false, false);
        }

        var targetId = candidates[0].Id;
        var requestedLimit = Math.Max(1, limit);
        var incoming = direction is "in" or "both"
            ? DistinctReferenceRelations(relations
                .Where(relation => string.Equals(relation.ToId, targetId, StringComparison.Ordinal)))
                .Select(relation => ToReference(relation, "incoming"))
                .ToArray()
            : [];
        var outgoing = direction is "out" or "both"
            ? DistinctReferenceRelations(relations
                .Where(relation => string.Equals(relation.FromId, targetId, StringComparison.Ordinal)))
                .Select(relation => ToReference(relation, "outgoing"))
                .ToArray()
            : [];
        var incomingPage = incoming.Take(requestedLimit).ToArray();
        var outgoingPage = outgoing.Take(requestedLimit).ToArray();
        return new ReferenceQueryPage(
            new ReferenceResult(incomingPage, outgoingPage),
            incomingPage.Length + outgoingPage.Length,
            incoming.Length > incomingPage.Length || outgoing.Length > outgoingPage.Length,
            true);
    }

    public IReadOnlyList<FileSummaryMatch> FindFiles(string selector, int limit)
    {
        return FindFilesPage(selector, limit).Items;
    }

    public QueryPage<FileSummaryMatch> FindFilesPage(string selector, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var normalized = selector.Trim().Replace('\\', '/');
        var matches = files
            .Where(file =>
                string.Equals(file.Id, selector.Trim(), StringComparison.Ordinal) ||
                string.Equals(file.Path, normalized, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ThenBy(file => file.Id, StringComparer.Ordinal)
            .Select(CreateFileSummary)
            .ToArray();
        return Page(matches, limit);
    }

    public IReadOnlyList<HarmonyTargetMatch> FindHarmony(
        string? selector,
        string? filePath,
        int limit)
    {
        return FindHarmonyPage(selector, filePath, limit).Items;
    }

    public QueryPage<HarmonyTargetMatch> FindHarmonyPage(
        string? selector,
        string? filePath,
        int limit)
    {
        var normalizedSelector = string.IsNullOrWhiteSpace(selector)
            ? null
            : NormalizeSelector(selector);
        var normalizedFile = string.IsNullOrWhiteSpace(filePath)
            ? null
            : filePath.Trim().Replace('\\', '/');
        var candidates = harmonyPatches
            .Where(patch => normalizedFile is null ||
                            string.Equals(patch.File, normalizedFile, StringComparison.OrdinalIgnoreCase))
            .Select(patch => new HarmonyCandidate(
                patch,
                normalizedSelector is null
                    ? 0
                    : Score(
                        normalizedSelector,
                        patch.Target,
                        patch.TargetType ?? string.Empty,
                        patch.TargetMember ?? string.Empty)))
            .Where(item => item.Score >= 0)
            .GroupBy(item => item.Patch.Target, StringComparer.Ordinal)
            .Select(group => new HarmonyTargetCandidate(
                group.Key,
                group.Min(item => item.Score),
                group.Select(item => item.Patch)
                    .OrderBy(item => item.PatchKind, StringComparer.Ordinal)
                    .ThenBy(item => item.Method, StringComparer.Ordinal)
                    .ThenBy(item => item.File, StringComparer.Ordinal)
                    .ThenBy(item => item.Line ?? int.MaxValue)
                    .ThenBy(item => item.Id, StringComparer.Ordinal)
                    .ToArray()))
            .OrderBy(item => item.Score)
            .ThenBy(item => item.Target, StringComparer.Ordinal)
            .Select(item => new HarmonyTargetMatch(
                item.Target,
                item.Patches.Select(ToHarmonyMatch).ToArray()))
            .ToArray();
        return Page(candidates, limit);
    }

    public AffectedResult FindAffected(
        IReadOnlyList<string> changedPaths,
        string rootPath,
        int depth,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (changedPaths.Count == 0)
        {
            throw ErrorFactory.InvalidArgument("The affected command requires at least one path.");
        }

        var normalizedRoot = Path.GetFullPath(rootPath);
        var changed = NormalizeAffectedPaths(changedPaths, normalizedRoot);
        var changedFiles = files
            .Where(file => changed.Contains(file.Path, StringComparer.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ThenBy(file => file.Id, StringComparer.Ordinal)
            .ToArray();

        var directEntities = entities
            .Where(IsDirectAffectedEntity)
            .Where(entity => changedFiles.Any(file => EntityBelongsToFile(entity, file)))
            .OrderBy(entity => AffectedKindOrder(entity.Kind))
            .ThenBy(entity => AffectedDisplayName(entity), StringComparer.Ordinal)
            .ThenBy(entity => FilePathFromEntity(entity), StringComparer.Ordinal)
            .ThenBy(entity => entity.Line ?? int.MaxValue)
            .ThenBy(entity => entity.Id, StringComparer.Ordinal)
            .ToArray();

        var directIds = directEntities
            .Select(entity => entity.Id)
            .ToHashSet(StringComparer.Ordinal);
        var dependentCandidates = new Dictionary<string, AffectedCandidate>(StringComparer.Ordinal);
        var runtimeCandidates = new Dictionary<string, AffectedCandidate>(StringComparer.Ordinal);
        var entityById = entities.ToDictionary(entity => entity.Id, StringComparer.Ordinal);
        var incoming = relations
            .Where(relation => relation.ToId is not null)
            .GroupBy(relation => relation.ToId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(relation => relation.Id, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);
        var visited = new HashSet<string>(directIds, StringComparer.Ordinal);
        var queue = new Queue<(string Id, int Distance)>();
        var traversalTruncated = false;
        var maximumTraversalEntities = Math.Min(4096, Math.Max(256, Math.Max(1, limit) * 32));
        if (directEntities.Length > maximumTraversalEntities)
        {
            traversalTruncated = true;
        }
        else
        {
            foreach (var entity in directEntities)
            {
                queue.Enqueue((entity.Id, 0));
            }
        }
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!incoming.TryGetValue(current.Id, out var currentRelations))
            {
                continue;
            }

            foreach (var relation in currentRelations)
            {
                if (!entityById.TryGetValue(relation.FromId, out var source))
                {
                    continue;
                }

                if (relation.Kind is "harmony_target" or "harmony_target_member")
                {
                    if (!directIds.Contains(source.Id) && source.Kind == "harmony_patch")
                    {
                        AddAffectedCandidate(
                            runtimeCandidates,
                            source,
                            current.Distance,
                            relation,
                            "heuristic");
                    }

                    continue;
                }

                if (relation.Kind == "owns" || current.Distance >= depth)
                {
                    continue;
                }

                if (!IsDependentAffectedEntity(source) || !visited.Add(source.Id))
                {
                    continue;
                }

                if (visited.Count > maximumTraversalEntities)
                {
                    traversalTruncated = true;
                    break;
                }

                AddAffectedCandidate(
                    dependentCandidates,
                    source,
                    current.Distance + 1,
                    relation,
                    null);
                queue.Enqueue((source.Id, current.Distance + 1));
            }

            if (traversalTruncated)
            {
                break;
            }
        }

        foreach (var directId in directIds)
        {
            dependentCandidates.Remove(directId);
            runtimeCandidates.Remove(directId);
        }

        foreach (var runtimeId in runtimeCandidates.Keys)
        {
            dependentCandidates.Remove(runtimeId);
        }

        var directMatches = directEntities
            .Select(entity => ToAffectedMatch(entity, "changed_file", null))
            .ToArray();
        var dependentMatches = dependentCandidates.Values
            .OrderBy(item => item.Distance)
            .ThenBy(item => AffectedKindOrder(item.Entity.Kind))
            .ThenBy(item => AffectedDisplayName(item.Entity), StringComparer.Ordinal)
            .ThenBy(item => FilePathFromEntity(item.Entity), StringComparer.Ordinal)
            .ThenBy(item => item.Entity.Line ?? int.MaxValue)
            .ThenBy(item => item.Entity.Id, StringComparer.Ordinal)
            .Select(item => ToAffectedMatch(item.Entity, item.Reason, item.Confidence))
            .ToArray();
        var runtimeMatches = runtimeCandidates.Values
            .OrderBy(item => AffectedDisplayName(item.Entity), StringComparer.Ordinal)
            .ThenBy(item => FilePathFromEntity(item.Entity), StringComparer.Ordinal)
            .ThenBy(item => item.Entity.Line ?? int.MaxValue)
            .ThenBy(item => item.Entity.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Reason, StringComparer.Ordinal)
            .Select(item => ToAffectedMatch(item.Entity, item.Reason, item.Confidence))
            .ToArray();

        var remaining = Math.Max(1, limit);
        var returnedDirect = TakeAffectedTier(directMatches, ref remaining, out var directTruncated);
        var returnedDependent = TakeAffectedTier(dependentMatches, ref remaining, out var dependentTruncated);
        var returnedRuntime = TakeAffectedTier(runtimeMatches, ref remaining, out var runtimeTruncated);
        return new AffectedResult(
            changed,
            returnedDirect,
            returnedDependent,
            returnedRuntime,
            traversalTruncated || directTruncated || dependentTruncated || runtimeTruncated);
    }

    private IReadOnlyList<string> NormalizeAffectedPaths(
        IReadOnlyList<string> changedPaths,
        string rootPath)
    {
        var indexedPaths = files
            .Select(file => file.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in changedPaths)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            string displayPath;
            try
            {
                var candidate = Path.IsPathRooted(input)
                    ? input
                    : Path.Combine(rootPath, input);
                displayPath = PathUtilities.DisplayPath(rootPath, Path.GetFullPath(candidate));
            }
            catch (ArgumentException ex)
            {
                throw ErrorFactory.InvalidArgument($"Invalid affected path '{input}': {ex.Message}");
            }

            var indexedPath = indexedPaths.FirstOrDefault(
                path => string.Equals(path, displayPath, StringComparison.OrdinalIgnoreCase));
            var canonicalPath = indexedPath ?? displayPath;
            if (!normalized.TryGetValue(canonicalPath, out var existing) ||
                string.CompareOrdinal(canonicalPath, existing) < 0)
            {
                normalized[canonicalPath] = canonicalPath;
            }
        }

        if (normalized.Count == 0)
        {
            throw ErrorFactory.InvalidArgument("The affected command requires at least one non-empty path.");
        }

        return normalized
            .Values
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsDirectAffectedEntity(EntityRecord entity) => entity.Kind is
        "def" or
        "patch_operation" or
        "csharp_type" or
        "csharp_member" or
        "harmony_patch" or
        "mod" or
        "project" or
        "assembly";

    private static bool IsDependentAffectedEntity(EntityRecord entity) =>
        IsDirectAffectedEntity(entity);

    private static void AddAffectedCandidate(
        IDictionary<string, AffectedCandidate> candidates,
        EntityRecord entity,
        int distance,
        RelationRecord relation,
        string? confidenceFallback)
    {
        if (candidates.ContainsKey(entity.Id))
        {
            return;
        }

        var payload = ParsePayload(relation.PayloadJson);
        var confidence = payload.TryGetValue("confidence", out var value) &&
                         !string.IsNullOrWhiteSpace(value)
            ? value
            : confidenceFallback;
        candidates.Add(
            entity.Id,
            new AffectedCandidate(entity, distance, relation.Kind, confidence));
    }

    private AffectedMatch ToAffectedMatch(
        EntityRecord entity,
        string? reason,
        string? confidence)
    {
        string? name = null;
        string? file = null;
        int? line = entity.Line;
        try
        {
            using var document = JsonDocument.Parse(entity.PayloadJson);
            var root = document.RootElement;
            name = AffectedDisplayName(entity.Kind, root);
            file = FilePath(entity, root);
            line ??= GetInt(root, "line");
        }
        catch (JsonException)
        {
            file = FilePathFromEntity(entity);
        }

        return new AffectedMatch(
            entity.Kind,
            entity.Id,
            string.IsNullOrWhiteSpace(name) ? null : name,
            string.IsNullOrWhiteSpace(file) ? null : file,
            line,
            reason,
            confidence);
    }

    private string AffectedDisplayName(EntityRecord entity)
    {
        try
        {
            using var document = JsonDocument.Parse(entity.PayloadJson);
            return AffectedDisplayName(entity.Kind, document.RootElement) ?? entity.Kind;
        }
        catch (JsonException)
        {
            return entity.Kind;
        }
    }

    private static string? AffectedDisplayName(string kind, JsonElement root) => kind switch
    {
        "def" => CombineDefName(root),
        "csharp_type" => GetString(root, "qualifiedName") ?? GetString(root, "name"),
        "csharp_member" => GetString(root, "qualifiedName") ?? GetString(root, "name"),
        "harmony_patch" => CombinePatchName(root),
        "patch_operation" => GetString(root, "operation") ??
                              GetString(root, "operationType") ??
                              GetString(root, "class"),
        "mod" => GetString(root, "packageId") ?? GetString(root, "name"),
        "project" => GetString(root, "name") ?? GetString(root, "file"),
        "assembly" => GetString(root, "name") ?? GetString(root, "file") ?? GetString(root, "path"),
        _ => GetString(root, "name") ?? GetString(root, "qualifiedName") ?? GetString(root, "target")
    };

    private static string? CombineDefName(JsonElement root)
    {
        var type = GetString(root, "defType");
        var name = GetString(root, "defName");
        return type is null
            ? name
            : name is null ? type : type + "/" + name;
    }

    private static string? CombinePatchName(JsonElement root)
    {
        var patchClass = GetString(root, "patchClass");
        var patchMethod = GetString(root, "patchMethod");
        if (patchClass is null)
        {
            return patchMethod;
        }

        if (patchMethod is null)
        {
            return patchClass;
        }

        return patchMethod.Contains('.', StringComparison.Ordinal)
            ? patchMethod
            : patchClass + "." + patchMethod;
    }

    private string FilePathFromEntity(EntityRecord entity)
    {
        try
        {
            using var document = JsonDocument.Parse(entity.PayloadJson);
            return FilePath(entity, document.RootElement);
        }
        catch (JsonException)
        {
            return entity.FileId is not null && filePaths.TryGetValue(entity.FileId, out var path)
                ? path
                : string.Empty;
        }
    }

    private static AffectedMatch[] TakeAffectedTier(
        IReadOnlyList<AffectedMatch> candidates,
        ref int remaining,
        out bool truncated)
    {
        var count = Math.Min(remaining, candidates.Count);
        truncated = candidates.Count > count;
        var result = candidates.Take(count).ToArray();
        remaining -= count;
        return result;
    }

    private IReadOnlyList<SearchCandidate> BuildCandidates(string selector, string? kind)
    {
        var normalized = NormalizeSelector(selector);
        var candidates = new List<SearchCandidate>();
        if (kind is null or "def")
        {
            foreach (var definition in definitions)
            {
                var result = ToMatch(definition);
                var score = Score(
                    normalized,
                    definition.DefType + "/" + (definition.DefName ?? string.Empty),
                    definition.DefType,
                    definition.DefName ?? string.Empty);
                if (score >= 0)
                {
                    candidates.Add(new SearchCandidate(
                        result,
                        result.Id,
                        result.Kind,
                        score,
                        definition.DefType + "/" + (definition.DefName ?? string.Empty)));
                }
            }
        }

        if (kind is null or "csharp_type")
        {
            foreach (var type in types)
            {
                var result = ToMatch(type, members.Count(item => item.ContainingTypeId == type.Id));
                var score = Score(normalized, type.QualifiedName, type.Name, type.TypeKind);
                if (score >= 0)
                {
                    candidates.Add(new SearchCandidate(
                        result,
                        result.Id,
                        result.Kind,
                        score,
                        type.QualifiedName));
                }
            }
        }

        if (kind is null or "csharp_member")
        {
            foreach (var member in members)
            {
                var result = ToMatch(member);
                var score = Score(
                    normalized,
                    member.QualifiedName,
                    member.Name,
                    member.Signature,
                    member.MemberKind);
                if (score >= 0)
                {
                    candidates.Add(new SearchCandidate(
                        result,
                        result.Id,
                        result.Kind,
                        score,
                        member.QualifiedName));
                }
            }
        }

        if (kind is null or "mod")
        {
            foreach (var mod in mods)
            {
                var result = ToMatch(mod);
                var score = Score(
                    normalized,
                    mod.PackageId ?? string.Empty,
                    mod.Name ?? string.Empty,
                    mod.ModRoot);
                if (score >= 0)
                {
                    candidates.Add(new SearchCandidate(
                        result,
                        result.Id,
                        result.Kind,
                        score,
                        mod.PackageId ?? mod.Name ?? mod.ModRoot));
                }
            }
        }

        if (kind is null or "project")
        {
            foreach (var project in projects)
            {
                var result = ToMatch(project);
                var score = Score(
                    normalized,
                    project.Name,
                    project.File,
                    project.ProjectKind);
                if (score >= 0)
                {
                    candidates.Add(new SearchCandidate(
                        result,
                        result.Id,
                        result.Kind,
                        score,
                        project.Name));
                }
            }
        }

        return candidates;
    }

    private ReferenceMatch ToReference(RelationRecord relation, string direction)
    {
        var payload = ParsePayload(relation.PayloadJson);
        payload.TryGetValue("target", out var target);
        payload.TryGetValue("field", out var field);
        payload.TryGetValue("confidence", out var confidence);
        var file = relation.FileId is not null && filePaths.TryGetValue(relation.FileId, out var path)
            ? path
            : null;
        return new ReferenceMatch(
            relation.Id,
            relation.Kind,
            direction,
            relation.FromId,
            relation.ToId,
            target,
            field,
            confidence,
            file,
            relation.Line);
    }

    private FileSummaryMatch CreateFileSummary(IndexedFileRecord file)
    {
        var summaries = entities
            .Where(entity => EntityBelongsToFile(entity, file))
            .Select(CreateEntitySummary)
            .OrderBy(item => item.Line ?? int.MaxValue)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        return new FileSummaryMatch(
            file.Kind,
            file.Id,
            file.Path,
            file.ContentHash,
            file.ParseStatus,
            summaries.Length,
            summaries);
    }

    private FileEntitySummary CreateEntitySummary(EntityRecord entity)
    {
        var name = entity.Kind;
        try
        {
            using var document = JsonDocument.Parse(entity.PayloadJson);
            var root = document.RootElement;
            name = GetString(root, "defName") ??
                   GetString(root, "name") ??
                   GetString(root, "qualifiedName") ??
                   GetString(root, "operation") ??
                   entity.Kind;
        }
        catch (JsonException)
        {
        }

        return new FileEntitySummary(entity.Kind, entity.Id, name, entity.Line);
    }

    private static bool EntityBelongsToFile(EntityRecord entity, IndexedFileRecord file)
    {
        if (string.Equals(entity.FileId, file.Id, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(entity.PayloadJson);
            var root = document.RootElement;
            if (MatchesFile(root, file))
            {
                return true;
            }

            if (root.TryGetProperty("declarations", out var declarations) &&
                declarations.ValueKind == JsonValueKind.Array)
            {
                return declarations.EnumerateArray().Any(item => MatchesFile(item, file));
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static bool MatchesFile(JsonElement value, IndexedFileRecord file)
    {
        var fileId = GetString(value, "fileId");
        var path = GetString(value, "file");
        return string.Equals(fileId, file.Id, StringComparison.Ordinal) ||
               string.Equals(path?.Replace('\\', '/'), file.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static int Score(string selector, params string[] representations)
    {
        var terms = selector.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            return -1;
        }

        if (representations.Any(item =>
                string.Equals(item, selector, StringComparison.OrdinalIgnoreCase)) ||
            terms.All(term => representations.Any(item =>
                string.Equals(item, term, StringComparison.OrdinalIgnoreCase))))
        {
            return 0;
        }

        if (representations.Any(item =>
                item.StartsWith(selector, StringComparison.OrdinalIgnoreCase)) ||
            terms.All(term => representations.Any(item =>
                item.StartsWith(term, StringComparison.OrdinalIgnoreCase))))
        {
            return 1;
        }

        if (representations.Any(item =>
                item.Contains(selector, StringComparison.OrdinalIgnoreCase)) ||
            terms.All(term => representations.Any(item =>
                item.Contains(term, StringComparison.OrdinalIgnoreCase))))
        {
            return 2;
        }

        return -1;
    }

    private static DefinitionMatch ToMatch(DefinitionModel definition) => new(
        "def",
        definition.Id,
        definition.DefType,
        definition.DefName,
        definition.File,
        definition.Line,
        definition.Parent,
        definition.Mod);

    private static CSharpTypeMatch ToMatch(CSharpTypeModel type, int memberCount) => new(
        "csharp_type",
        type.Id,
        type.QualifiedName,
        type.QualifiedName,
        type.TypeKind,
        type.Namespace,
        type.File,
        type.Line,
        memberCount,
        type.Accessibility,
        type.IsStatic,
        type.IsPartial,
        type.BaseType,
        type.Interfaces,
        type.Attributes);

    private static CSharpMemberMatch ToMatch(CSharpMemberModel member) => new(
        "csharp_member",
        member.Id,
        member.Name,
        member.QualifiedName,
        member.MemberKind,
        member.Signature,
        member.ContainingType,
        member.File,
        member.Line,
        member.Accessibility,
        member.IsStatic,
        member.Attributes);

    private static ModMatch ToMatch(ModModel mod) => new(
        "mod",
        mod.Id,
        mod.PackageId,
        mod.Name,
        mod.File,
        mod.ModRoot,
        mod.SupportedVersions,
        mod.ModDependencies,
        mod.LoadAfter,
        mod.LoadBefore,
        mod.IncompatibleWith);

    private static ProjectMatch ToMatch(ProjectModel project) => new(
        "project",
        project.Id,
        project.Name,
        project.File,
        project.ProjectKind,
        project.TargetFrameworks,
        project.RootNamespace,
        project.ProjectReferences,
        project.PackageReferences,
        project.AssemblyReferences,
        project.StaticEvaluation);

    private DefinitionModel? ParseDefinition(EntityRecord entity)
    {
        try
        {
            using var document = JsonDocument.Parse(entity.PayloadJson);
            var root = document.RootElement;
            var type = GetString(root, "defType");
            if (type is null)
            {
                return null;
            }

            return new DefinitionModel(
                entity.Id,
                type,
                GetString(root, "defName"),
                FilePath(entity, root),
                entity.Line ?? GetInt(root, "line"),
                GetString(root, "parent"),
                GetString(root, "ownerModId"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private HarmonyPatchModel? ParseHarmonyPatch(EntityRecord entity)
    {
        try
        {
            using var document = JsonDocument.Parse(entity.PayloadJson);
            var root = document.RootElement;
            var patchClass = GetString(root, "patchClass");
            var patchMethod = GetString(root, "patchMethod");
            if (patchClass is null || patchMethod is null)
            {
                return null;
            }

            var targetType = GetString(root, "targetType");
            var targetMember = GetString(root, "targetMember");
            var target = GetString(root, "target") ??
                         (targetType is null
                             ? targetMember
                             : targetMember is null
                                 ? targetType
                                 : targetType + "." + targetMember) ??
                         GetString(root, "rawTarget") ??
                         "(unresolved)";
            return new HarmonyPatchModel(
                entity.Id,
                GetString(root, "patchKind") ?? "patch",
                patchClass + "." + patchMethod[(patchMethod.LastIndexOf('.') + 1)..],
                patchClass,
                target,
                targetType,
                targetMember,
                GetStrings(root, "targetSignature"),
                FilePath(entity, root),
                entity.Line ?? GetInt(root, "line"),
                string.Equals(GetString(root, "resolutionState"), "resolved", StringComparison.Ordinal),
                GetString(root, "resolutionState") ?? "unresolved",
                GetString(root, "confidence") ?? "heuristic");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HarmonyPatchMatch ToHarmonyMatch(HarmonyPatchModel patch) => new(
        patch.Id,
        patch.PatchKind,
        patch.Method,
        patch.File,
        patch.Line,
        patch.Resolved,
        patch.PatchClass,
        patch.TargetMember,
        patch.TargetSignature,
        patch.ResolutionState,
        patch.Confidence);

    private ModModel? ParseMod(EntityRecord entity)
    {
        try
        {
            using var document = JsonDocument.Parse(entity.PayloadJson);
            var root = document.RootElement;
            var modRoot = GetString(root, "modRoot");
            if (modRoot is null)
            {
                return null;
            }

            return new ModModel(
                entity.Id,
                GetString(root, "packageId"),
                GetString(root, "name"),
                FilePath(entity, root),
                modRoot,
                GetStrings(root, "supportedVersions"),
                GetStrings(root, "modDependencies"),
                GetStrings(root, "loadAfter"),
                GetStrings(root, "loadBefore"),
                GetStrings(root, "incompatibleWith"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ProjectModel? ParseProject(EntityRecord entity)
    {
        try
        {
            using var document = JsonDocument.Parse(entity.PayloadJson);
            var root = document.RootElement;
            var name = GetString(root, "name");
            var file = FilePath(entity, root);
            if (name is null || file.Length == 0)
            {
                return null;
            }

            return new ProjectModel(
                entity.Id,
                name,
                file,
                GetString(root, "projectKind") ?? "project",
                GetStrings(root, "targetFrameworks"),
                GetString(root, "rootNamespace"),
                GetStrings(root, "projectReferences"),
                GetPackageReferenceNames(root),
                GetStrings(root, "assemblyReferences"),
                GetString(root, "staticEvaluation") ?? "static");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private CSharpTypeModel? ParseType(EntityRecord entity)
    {
        try
        {
            using var document = JsonDocument.Parse(entity.PayloadJson);
            var root = document.RootElement;
            var qualified = GetString(root, "qualifiedName") ?? GetString(root, "name");
            if (qualified is null)
            {
                return null;
            }

            return new CSharpTypeModel(
                entity.Id,
                GetString(root, "name") ?? qualified,
                qualified,
                GetString(root, "typeKind") ?? "type",
                GetString(root, "namespace") ?? string.Empty,
                FilePath(entity, root),
                entity.Line ?? GetInt(root, "line"),
                GetString(root, "accessibility") ?? "internal",
                GetBool(root, "isStatic"),
                GetBool(root, "isPartial"),
                GetString(root, "baseType"),
                GetStrings(root, "interfaces"),
                GetStrings(root, "attributes"),
                DeclarationFileIds(root, entity.FileId));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private CSharpMemberModel? ParseMember(EntityRecord entity)
    {
        try
        {
            using var document = JsonDocument.Parse(entity.PayloadJson);
            var root = document.RootElement;
            var containingIdentity = GetString(root, "containingTypeIdentity") ??
                                     GetString(root, "containingType");
            var name = GetString(root, "name");
            if (containingIdentity is null || name is null)
            {
                return null;
            }

            var containing = DisplayTypeName(containingIdentity);
            return new CSharpMemberModel(
                entity.Id,
                name,
                containing + "." + name,
                GetString(root, "memberKind") ?? "member",
                GetString(root, "signature") ?? name,
                containing,
                GetString(root, "containingTypeId"),
                FilePath(entity, root),
                entity.Line ?? GetInt(root, "line"),
                GetString(root, "accessibility") ?? "private",
                GetBool(root, "isStatic"),
                GetStrings(root, "attributes"),
                DeclarationFileIds(root, entity.FileId));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private IReadOnlyList<string> DeclarationFileIds(JsonElement root, string? fallback)
    {
        var values = new List<string>();
        if (root.TryGetProperty("declarations", out var declarations) &&
            declarations.ValueKind == JsonValueKind.Array)
        {
            values.AddRange(declarations.EnumerateArray()
                .Select(item => GetString(item, "fileId"))
                .Where(item => item is not null)
                .Cast<string>());
        }

        if (fallback is not null)
        {
            values.Add(fallback);
        }

        return values.Distinct(StringComparer.Ordinal).ToArray();
    }

    private string FilePath(EntityRecord entity, JsonElement root)
    {
        if (entity.FileId is not null && filePaths.TryGetValue(entity.FileId, out var path))
        {
            return path;
        }

        return GetString(root, "file") ?? string.Empty;
    }

    private static Dictionary<string, string?> ParsePayload(string payload)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(payload);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    result[property.Name] = property.Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }

    private static string NormalizeSelector(string selector)
    {
        var value = selector.Trim();
        return value.StartsWith("def:", StringComparison.OrdinalIgnoreCase)
            ? value[4..]
            : value;
    }

    private static int KindOrder(string kind) => kind switch
    {
        "def" => 0,
        "mod" => 1,
        "project" => 2,
        "csharp_type" => 3,
        "csharp_member" => 4,
        _ => 5
    };

    private static int AffectedKindOrder(string kind) => kind switch
    {
        "def" => 0,
        "patch_operation" => 1,
        "csharp_type" => 2,
        "csharp_member" => 3,
        "harmony_patch" => 4,
        "mod" => 5,
        "project" => 6,
        "assembly" => 7,
        _ => 8
    };

    private static QueryPage<T> Page<T>(IReadOnlyList<T> items, int limit)
    {
        var requestedLimit = Math.Max(1, limit);
        var page = items.Take(requestedLimit).ToArray();
        return new QueryPage<T>(page, page.Length, page.Length < items.Count);
    }

    private IEnumerable<RelationRecord> DistinctReferenceRelations(
        IEnumerable<RelationRecord> candidates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relation in candidates.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var payload = ParsePayload(relation.PayloadJson);
            payload.TryGetValue("target", out var target);
            payload.TryGetValue("field", out var field);
            var key = string.Join(
                '\0',
                relation.Kind,
                relation.FromId,
                relation.ToId ?? string.Empty,
                target ?? string.Empty,
                field ?? string.Empty);
            if (seen.Add(key))
            {
                yield return relation;
            }
        }
    }

    private static string DisplayTypeName(string identity)
    {
        var first = identity.IndexOf('\0');
        if (first < 0)
        {
            return identity;
        }

        var second = identity.IndexOf('\0', first + 1);
        return second > first
            ? identity[(first + 1)..second]
            : identity[(first + 1)..];
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var result)
            ? result
            : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static IReadOnlyList<string> GetStrings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private static IReadOnlyList<PackageReferenceModel> GetPackageReferences(JsonElement element)
    {
        if (!element.TryGetProperty("packageReferences", out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<PackageReferenceModel>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var include = item.GetString();
                if (!string.IsNullOrWhiteSpace(include))
                {
                    result.Add(new PackageReferenceModel(include!, null));
                }
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var include = GetString(item, "include");
                if (!string.IsNullOrWhiteSpace(include))
                {
                    result.Add(new PackageReferenceModel(include!, GetString(item, "version")));
                }
            }
        }

        return result
            .Distinct()
            .OrderBy(item => item.Include, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> GetPackageReferenceNames(JsonElement element) =>
        GetPackageReferences(element)
            .Select(item => item.Version is null ? item.Include : item.Include + "@" + item.Version)
            .ToArray();

    private sealed record SearchCandidate(
        object Result,
        string Id,
        string Kind,
        int Score,
        string Label);

    private sealed record HarmonyCandidate(
        HarmonyPatchModel Patch,
        int Score);

    private sealed record HarmonyTargetCandidate(
        string Target,
        int Score,
        IReadOnlyList<HarmonyPatchModel> Patches);

    private sealed record AffectedCandidate(
        EntityRecord Entity,
        int Distance,
        string Reason,
        string? Confidence);

    private sealed record DefinitionModel(
        string Id,
        string DefType,
        string? DefName,
        string File,
        int? Line,
        string? Parent,
        string? Mod);

    private sealed record CSharpTypeModel(
        string Id,
        string Name,
        string QualifiedName,
        string TypeKind,
        string Namespace,
        string File,
        int? Line,
        string Accessibility,
        bool IsStatic,
        bool IsPartial,
        string? BaseType,
        IReadOnlyList<string> Interfaces,
        IReadOnlyList<string> Attributes,
        IReadOnlyList<string> FileIds);

    private sealed record CSharpMemberModel(
        string Id,
        string Name,
        string QualifiedName,
        string MemberKind,
        string Signature,
        string ContainingType,
        string? ContainingTypeId,
        string File,
        int? Line,
        string Accessibility,
        bool IsStatic,
        IReadOnlyList<string> Attributes,
        IReadOnlyList<string> FileIds);

    private sealed record HarmonyPatchModel(
        string Id,
        string PatchKind,
        string Method,
        string PatchClass,
        string Target,
        string? TargetType,
        string? TargetMember,
        IReadOnlyList<string> TargetSignature,
        string File,
        int? Line,
        bool Resolved,
        string ResolutionState,
        string Confidence);

    private sealed record ModModel(
        string Id,
        string? PackageId,
        string? Name,
        string File,
        string ModRoot,
        IReadOnlyList<string> SupportedVersions,
        IReadOnlyList<string> ModDependencies,
        IReadOnlyList<string> LoadAfter,
        IReadOnlyList<string> LoadBefore,
        IReadOnlyList<string> IncompatibleWith);

    private sealed record PackageReferenceModel(string Include, string? Version);

    private sealed record ProjectModel(
        string Id,
        string Name,
        string File,
        string ProjectKind,
        IReadOnlyList<string> TargetFrameworks,
        string? RootNamespace,
        IReadOnlyList<string> ProjectReferences,
        IReadOnlyList<string> PackageReferences,
        IReadOnlyList<string> AssemblyReferences,
        string StaticEvaluation);
}
