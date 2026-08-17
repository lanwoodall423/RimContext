using System.Text.Json;
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
        return FindResults(selector, limit, "def")
            .OfType<DefinitionMatch>()
            .ToArray();
    }

    public IReadOnlyList<object> FindResults(string selector, int limit, string? kind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var candidates = BuildCandidates(selector, kind);
        return candidates
            .OrderBy(item => item.Score)
            .ThenBy(item => KindOrder(item.Kind))
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Label, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .Select(item => item.Result)
            .ToArray();
    }

    public IReadOnlyList<object> FindDefinitionResults(string selector, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        return BuildCandidates(selector, null)
            .Where(item => item.Score == 0)
            .OrderBy(item => KindOrder(item.Kind))
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Label, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .Select(item => item.Result)
            .ToArray();
    }

    public ReferenceResult FindReferences(string selector, int limit, string direction = "both")
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
            return new ReferenceResult([], []);
        }

        var targetId = candidates[0].Id;
        var incoming = direction is "in" or "both"
            ? relations
                .Where(relation => string.Equals(relation.ToId, targetId, StringComparison.Ordinal))
                .OrderBy(relation => relation.Id, StringComparer.Ordinal)
                .Take(Math.Max(1, limit))
                .Select(relation => ToReference(relation, "incoming"))
                .ToArray()
            : [];
        var outgoing = direction is "out" or "both"
            ? relations
                .Where(relation => string.Equals(relation.FromId, targetId, StringComparison.Ordinal))
                .OrderBy(relation => relation.Id, StringComparer.Ordinal)
                .Take(Math.Max(1, limit))
                .Select(relation => ToReference(relation, "outgoing"))
                .ToArray()
            : [];
        return new ReferenceResult(incoming, outgoing);
    }

    public IReadOnlyList<FileSummaryMatch> FindFiles(string selector, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        var normalized = selector.Trim().Replace('\\', '/');
        return files
            .Where(file =>
                string.Equals(file.Id, selector.Trim(), StringComparison.Ordinal) ||
                string.Equals(file.Path, normalized, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ThenBy(file => file.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .Select(CreateFileSummary)
            .ToArray();
    }

    public IReadOnlyList<HarmonyTargetMatch> FindHarmony(
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
            .Take(Math.Max(1, limit))
            .Select(item => new HarmonyTargetMatch(
                item.Target,
                item.Patches.Select(ToHarmonyMatch).ToArray()))
            .ToArray();
        return candidates;
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
