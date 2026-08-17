using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using RimContext.Core.Configuration;
using RimContext.Core.Discovery;
using RimContext.Core.Model;
using RimContext.Core.Output;
using RimContext.Core.Storage;

namespace RimContext.Core.Semantics;

public sealed record XmlSemanticResult(
    IReadOnlyDictionary<string, IndexedFileRecord> FileUpdates,
    IReadOnlyList<EntityRecord> Entities,
    IReadOnlyList<RelationRecord> Relations,
    IReadOnlyList<string> RefreshedFileIds,
    bool RebuildRelations,
    IReadOnlyList<IndexDiagnostic> Diagnostics);

public static class XmlSemanticIndexer
{
    public static XmlSemanticResult Empty { get; } = new(
        new Dictionary<string, IndexedFileRecord>(StringComparer.Ordinal),
        [],
        [],
        [],
        false,
        []);

    public static IReadOnlyList<IndexedFileRecord> SelectFilesToAnalyze(
        IReadOnlyList<IndexedFileRecord> currentFiles,
        IReadOnlyList<IndexedFileRecord> previousFiles,
        IReadOnlyList<IndexedFileRecord> addedOrChangedFiles,
        IReadOnlyList<string> removedFileIds)
    {
        var affectedRoots = addedOrChangedFiles
            .Concat(previousFiles.Where(file => removedFileIds.Contains(file.Id, StringComparer.Ordinal)))
            .Where(file => file.Kind == DiscoveredFileKinds.Xml && IsAboutPath(file.Path))
            .Select(file => ModRootPath(file.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var changedIds = addedOrChangedFiles
            .Where(file => file.Kind == DiscoveredFileKinds.Xml)
            .Select(file => file.Id)
            .ToHashSet(StringComparer.Ordinal);

        return currentFiles
            .Where(file => file.Kind == DiscoveredFileKinds.Xml &&
                           (changedIds.Contains(file.Id) || affectedRoots.Any(root => IsWithinModRoot(file.Path, root))))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ThenBy(file => file.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public static XmlSemanticResult Analyze(
        WorkspaceConfiguration configuration,
        IReadOnlyList<IndexedFileRecord> currentFiles,
        IReadOnlyList<IndexedFileRecord> filesToAnalyze,
        IReadOnlyList<IndexedFileRecord> previousFiles,
        IReadOnlyList<EntityRecord> previousEntities,
        IReadOnlyList<string> removedFileIds)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(currentFiles);
        ArgumentNullException.ThrowIfNull(filesToAnalyze);
        ArgumentNullException.ThrowIfNull(previousFiles);
        ArgumentNullException.ThrowIfNull(previousEntities);
        ArgumentNullException.ThrowIfNull(removedFileIds);

        var currentIds = currentFiles.Select(file => file.Id).ToHashSet(StringComparer.Ordinal);
        var analyzedIds = filesToAnalyze.Select(file => file.Id).ToHashSet(StringComparer.Ordinal);
        var removedIds = removedFileIds.ToHashSet(StringComparer.Ordinal);
        var activePreviousEntities = previousEntities
            .Where(entity => entity.FileId is null ||
                             (currentIds.Contains(entity.FileId) &&
                              !analyzedIds.Contains(entity.FileId) &&
                              !removedIds.Contains(entity.FileId)))
            .ToArray();

        var parsedFiles = filesToAnalyze
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ThenBy(file => file.Id, StringComparer.Ordinal)
            .Select(file => ParseFile(configuration, file))
            .ToArray();

        var previousMods = activePreviousEntities
            .Where(entity => entity.Kind == EntityKinds.Mod)
            .Select(ParseMod)
            .Where(mod => mod is not null)
            .Cast<ModInfo>()
            .ToList();
        var parsedMods = parsedFiles
            .Where(file => file.Mod is not null)
            .Select(file => file.Mod!)
            .ToList();
        var mods = previousMods
            .Concat(parsedMods)
            .GroupBy(mod => mod.FileId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(mod => mod.RootPath, StringComparer.Ordinal)
            .ThenBy(mod => mod.Id, StringComparer.Ordinal)
            .ToArray();

        var activeDefinitions = activePreviousEntities
            .Where(entity => entity.Kind == EntityKinds.Def)
            .Select(ParseDefinition)
            .Where(definition => definition is not null)
            .Cast<DefinitionInfo>()
            .ToList();

        var rawDefinitions = parsedFiles
            .SelectMany(file => file.Definitions.Select(definition => (File: file.File, Definition: definition, Owner: FindOwner(mods, file.File.Path))))
            .ToArray();
        var definitionGroups = activeDefinitions
            .Select(definition => DefinitionGroupKey(definition.Scope, definition.Type, definition.Name))
            .Concat(rawDefinitions.Select(item => DefinitionGroupKey(
                OwnerScope(configuration, item.Owner),
                item.Definition.Type,
                item.Definition.Name)))
            .GroupBy(key => key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var replacementEntities = new List<EntityRecord>();
        var newDefinitions = new List<DefinitionInfo>();
        var rawReferenceCandidates = new List<RawReferenceCandidate>();

        foreach (var raw in rawDefinitions)
        {
            var scope = OwnerScope(configuration, raw.Owner);
            var groupKey = DefinitionGroupKey(scope, raw.Definition.Type, raw.Definition.Name);
            var semanticIdentity = raw.Definition.Name is null
                ? $@"{raw.Definition.Type}#{raw.File.Path}|{raw.Definition.XmlPath}"
                : $@"{raw.Definition.Type}/{raw.Definition.Name}" +
                  (definitionGroups[groupKey] > 1 ? $@"#{raw.File.Path}|{raw.Definition.XmlPath}" : string.Empty);
            var id = StableEntityId.Create(EntityKinds.Def, scope, semanticIdentity);
            var definition = new DefinitionInfo(
                id,
                raw.Definition.Type,
                raw.Definition.Name,
                raw.Definition.Parent,
                raw.File.Id,
                raw.File.Path,
                raw.Definition.Line,
                raw.Owner?.Id,
                raw.Owner?.PackageId,
                scope,
                raw.Definition.XmlPath);
            newDefinitions.Add(definition);
            replacementEntities.Add(new EntityRecord(
                id,
                EntityKinds.Def,
                $@"{scope}\0{raw.Definition.Type}\0{raw.Definition.Name ?? raw.Definition.XmlPath}",
                raw.File.Id,
                raw.Definition.Line,
                JsonOutput.SerializePayload(new
                {
                    defType = raw.Definition.Type,
                    defName = raw.Definition.Name,
                    parent = raw.Definition.Parent,
                    name = raw.Definition.AttributeName,
                    file = raw.File.Path,
                    line = raw.Definition.Line,
                    ownerModId = raw.Owner?.PackageId,
                    ownerModName = raw.Owner?.Name,
                    ownerScope = scope,
                    xmlPath = raw.Definition.XmlPath
                })));

            rawReferenceCandidates.AddRange(raw.Definition.References.Select(reference =>
                new RawReferenceCandidate(definition, raw.File, reference)));
        }

        var allDefinitions = activeDefinitions
            .Concat(newDefinitions)
            .GroupBy(definition => definition.Id, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(definition => definition.Id, StringComparer.Ordinal)
            .ToArray();
        var allReferenceEntities = activePreviousEntities
            .Where(entity => entity.Kind == EntityKinds.DefReference)
            .Select(entity => ParseReference(entity))
            .Where(reference => reference is not null)
            .Cast<ReferenceInfo>()
            .ToList();

        foreach (var candidate in rawReferenceCandidates)
        {
            var resolution = ResolveTarget(allDefinitions, candidate.Reference.Target);
            var id = StableEntityId.Create(
                EntityKinds.DefReference,
                candidate.Definition.Id,
                $@"{candidate.Reference.Field}|{candidate.Reference.Target}|{candidate.Reference.XmlPath}");
            var reference = new ReferenceInfo(
                id,
                candidate.Definition.Id,
                candidate.Reference.Target,
                candidate.Reference.Field,
                candidate.Reference.XmlPath,
                candidate.File.Id,
                candidate.Reference.Line,
                resolution.Target?.Id,
                resolution.Confidence,
                candidate.Reference.ReferenceKind);
            allReferenceEntities.Add(reference);
            replacementEntities.Add(new EntityRecord(
                id,
                EntityKinds.DefReference,
                $@"{candidate.Definition.Id}\0{candidate.Reference.XmlPath}",
                candidate.File.Id,
                candidate.Reference.Line,
                JsonOutput.SerializePayload(new
                {
                    ownerDefId = candidate.Definition.Id,
                    target = candidate.Reference.Target,
                    targetId = resolution.Target?.Id,
                    field = candidate.Reference.Field,
                    confidence = resolution.Confidence,
                    referenceKind = candidate.Reference.ReferenceKind,
                    xmlPath = candidate.Reference.XmlPath
                })));
        }

        foreach (var parsedFile in parsedFiles)
        {
            if (parsedFile.Mod is null)
            {
                continue;
            }

            replacementEntities.Add(new EntityRecord(
                parsedFile.Mod.Id,
                EntityKinds.Mod,
                parsedFile.Mod.PackageId ?? parsedFile.Mod.RootPath,
                parsedFile.File.Id,
                parsedFile.Mod.Line,
                JsonOutput.SerializePayload(new
                {
                    packageId = parsedFile.Mod.PackageId,
                    name = parsedFile.Mod.Name,
                    modRoot = parsedFile.Mod.RootPath,
                    aboutFile = parsedFile.File.Path,
                    supportedVersions = parsedFile.Mod.SupportedVersions,
                    modDependencies = parsedFile.Mod.ModDependencies,
                    loadAfter = parsedFile.Mod.LoadAfter,
                    loadBefore = parsedFile.Mod.LoadBefore,
                    incompatibleWith = parsedFile.Mod.IncompatibleWith
                })));
        }

        foreach (var parsedFile in parsedFiles)
        {
            foreach (var patch in parsedFile.Patches)
            {
                var owner = FindOwner(mods, parsedFile.File.Path);
                var id = StableEntityId.Create(EntityKinds.PatchOperation, parsedFile.File.Id, patch.XmlPath);
                replacementEntities.Add(new EntityRecord(
                    id,
                    EntityKinds.PatchOperation,
                    $@"{parsedFile.File.Id}\0{patch.XmlPath}",
                    parsedFile.File.Id,
                    patch.Line,
                    JsonOutput.SerializePayload(new
                    {
                        operationType = patch.OperationType,
                        xpath = patch.XPath,
                        file = parsedFile.File.Path,
                        line = patch.Line,
                        ownerModId = owner?.PackageId,
                        ownerModName = owner?.Name,
                        xmlPath = patch.XmlPath
                    })));
            }
        }

        var relations = new List<RelationRecord>();
        foreach (var definition in allDefinitions)
        {
            if (!string.IsNullOrWhiteSpace(definition.Parent))
            {
                var resolution = ResolveTarget(allDefinitions, definition.Parent);
                relations.Add(new RelationRecord(
                    StableEntityId.Create(EntityKinds.Relation, definition.Id, $@"inheritance|{definition.Parent}|{definition.XmlPath}"),
                    definition.Id,
                    resolution.Target?.Id,
                    EntityKinds.Inheritance,
                    definition.FileId,
                    definition.Line,
                    JsonOutput.SerializePayload(new
                    {
                        target = definition.Parent,
                        confidence = resolution.Confidence
                    })));
            }
        }

        foreach (var reference in allReferenceEntities)
        {
            var resolution = ResolveTarget(allDefinitions, reference.Target);
            relations.Add(new RelationRecord(
                StableEntityId.Create(EntityKinds.Relation, reference.OwnerDefId, $@"{EntityKinds.DefReference}|{reference.Id}"),
                reference.OwnerDefId,
                resolution.Target?.Id,
                EntityKinds.DefReference,
                reference.FileId,
                reference.Line,
                JsonOutput.SerializePayload(new
                {
                    target = reference.Target,
                    targetId = resolution.Target?.Id,
                    field = reference.Field,
                    confidence = resolution.Confidence,
                    referenceKind = reference.ReferenceKind,
                    xmlPath = reference.XmlPath
                })));
        }

        var updates = parsedFiles.ToDictionary(
            file => file.File.Id,
            file => file.File with
            {
                ParseStatus = file.Diagnostic is null ? "parsed" : "error",
                Diagnostic = file.Diagnostic
            },
            StringComparer.Ordinal);
        var diagnostics = parsedFiles
            .Where(file => file.Diagnostic is not null)
            .Select(file => new IndexDiagnostic(file.File.Path, file.Diagnostic!))
            .OrderBy(diagnostic => diagnostic.Path, StringComparer.Ordinal)
            .ToArray();
        var removedXml = previousFiles.Any(file =>
            file.Kind == DiscoveredFileKinds.Xml && removedFileIds.Contains(file.Id, StringComparer.Ordinal));

        return new XmlSemanticResult(
            updates,
            replacementEntities,
            relations.OrderBy(relation => relation.Id, StringComparer.Ordinal).ToArray(),
            parsedFiles.Select(file => file.File.Id).ToArray(),
            parsedFiles.Length > 0 || removedXml,
            diagnostics);
    }

    private static ParsedXmlFile ParseFile(WorkspaceConfiguration configuration, IndexedFileRecord file)
    {
        var absolutePath = Path.Combine(configuration.RootPath, file.Path.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreWhitespace = false,
                XmlResolver = null,
                MaxCharactersInDocument = 10_000_000
            };
            using var stream = File.OpenRead(absolutePath);
            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root is null)
            {
                return new ParsedXmlFile(file, [], [], null, "XML document has no root element.");
            }

            var definitions = new List<RawDefinition>();
            if (IsElement(root, "Defs"))
            {
                foreach (var element in root.Elements().Where(IsDefinitionElement))
                {
                    definitions.Add(ParseDefinition(element));
                }
            }
            else if (IsDefinitionElement(root))
            {
                definitions.Add(ParseDefinition(root));
            }

            var patches = root.DescendantsAndSelf()
                .Where(IsPatchOperation)
                .Select(ParsePatch)
                .OrderBy(patch => patch.XmlPath, StringComparer.Ordinal)
                .ToArray();
            var mod = IsAboutPath(file.Path) ? ParseMod(document, file) : null;
            return new ParsedXmlFile(file, definitions, patches, mod, null);
        }
        catch (XmlException exception)
        {
            var line = exception.LineNumber > 0 ? $@" at line {exception.LineNumber}" : string.Empty;
            return new ParsedXmlFile(file, [], [], null, $@"Malformed XML{line}.");
        }
        catch (IOException)
        {
            return new ParsedXmlFile(file, [], [], null, "The XML file could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            return new ParsedXmlFile(file, [], [], null, "The XML file could not be read.");
        }
    }

    private static RawDefinition ParseDefinition(XElement element)
    {
        var parent = FirstValue(element, "parentName") ?? AttributeValue(element, "ParentName");
        var defName = FirstValue(element, "defName");
        var attributeName = AttributeValue(element, "Name");
        var references = new List<RawReference>();
        foreach (var leaf in element.Descendants().Where(child => !child.Elements().Any()))
        {
            var field = IsReferenceField(leaf.Name.LocalName)
                ? leaf
                : leaf.Ancestors()
                    .TakeWhile(ancestor => !ReferenceEquals(ancestor, element))
                    .FirstOrDefault(ancestor => IsReferenceField(ancestor.Name.LocalName));
            if (field is null || IsElement(field, "defName") || IsElement(field, "parentName"))
            {
                continue;
            }

            var target = leaf.Value.Trim();
            if (target.Length == 0 || target.Length > 512)
            {
                continue;
            }

            references.Add(new RawReference(
                field.Name.LocalName,
                target,
                ElementPath(leaf),
                LineNumber(leaf),
                IsListReferenceField(field.Name.LocalName) ? "definition_list" : "definition"));
        }

        return new RawDefinition(
            element.Name.LocalName,
            defName,
            parent,
            attributeName,
            ElementPath(element),
            LineNumber(element),
            references
                .GroupBy(reference => reference.XmlPath, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(reference => reference.XmlPath, StringComparer.Ordinal)
                .ToArray());
    }

    private static RawPatch ParsePatch(XElement element) => new(
        AttributeValue(element, "Class") ?? element.Name.LocalName,
        FirstValue(element, "xpath"),
        ElementPath(element),
        LineNumber(element));

    private static ModInfo? ParseMod(XDocument document, IndexedFileRecord file)
    {
        var root = ModRootPath(file.Path);
        var packageId = FirstValue(document.Root, "packageId");
        var name = FirstValue(document.Root, "name");
        var identity = packageId ?? root;
        if (identity.Length == 0)
        {
            return null;
        }

        return new ModInfo(
            StableEntityId.Create(EntityKinds.Mod, file.WorkspaceIdentity, $@"{identity}\0{root}"),
            file.Id,
            root,
            packageId,
            name,
            LineNumber(document.Root),
            ChildValues(document.Root, "supportedVersions"),
            ChildValues(document.Root, "modDependencies"),
            ChildValues(document.Root, "loadAfter"),
            ChildValues(document.Root, "loadBefore"),
            ChildValues(document.Root, "incompatibleWith"));
    }

    private static ModInfo? ParseMod(EntityRecord entity)
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

            return new ModInfo(
                entity.Id,
                entity.FileId ?? string.Empty,
                modRoot,
                GetString(root, "packageId"),
                GetString(root, "name"),
                entity.Line,
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

    private static DefinitionInfo? ParseDefinition(EntityRecord entity)
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

            return new DefinitionInfo(
                entity.Id,
                type,
                GetString(root, "defName"),
                GetString(root, "parent"),
                entity.FileId,
                GetString(root, "file") ?? string.Empty,
                entity.Line,
                GetString(root, "ownerModId"),
                GetString(root, "ownerModName"),
                GetString(root, "ownerScope") ?? string.Empty,
                GetString(root, "xmlPath") ?? entity.IdentityKey);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ReferenceInfo? ParseReference(EntityRecord entity)
    {
        try
        {
            using var document = JsonDocument.Parse(entity.PayloadJson);
            var root = document.RootElement;
            var owner = GetString(root, "ownerDefId");
            var target = GetString(root, "target");
            var field = GetString(root, "field");
            if (owner is null || target is null || field is null)
            {
                return null;
            }

            return new ReferenceInfo(
                entity.Id,
                owner,
                target,
                field,
                GetString(root, "xmlPath") ?? entity.IdentityKey,
                entity.FileId,
                entity.Line,
                GetString(root, "targetId"),
                GetString(root, "confidence") ?? "unresolved",
                GetString(root, "referenceKind") ?? "definition");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (DefinitionInfo? Target, string Confidence) ResolveTarget(
        IReadOnlyList<DefinitionInfo> definitions,
        string target)
    {
        var normalized = target.Trim();
        var separator = normalized.IndexOf('/');
        IEnumerable<DefinitionInfo> candidates;
        if (separator > 0 && separator < normalized.Length - 1)
        {
            var type = normalized[..separator];
            var name = normalized[(separator + 1)..];
            candidates = definitions.Where(definition =>
                string.Equals(definition.Type, type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            candidates = definitions.Where(definition =>
                string.Equals(definition.Name, normalized, StringComparison.OrdinalIgnoreCase));
        }

        var matches = candidates.OrderBy(definition => definition.Id, StringComparer.Ordinal).ToArray();
        return matches.Length switch
        {
            1 => (matches[0], "exact"),
            > 1 => (null, "ambiguous"),
            _ => (null, "unresolved")
        };
    }

    private static ModInfo? FindOwner(IReadOnlyList<ModInfo> mods, string path) => mods
        .Where(mod => IsWithinModRoot(path, mod.RootPath))
        .OrderByDescending(mod => mod.RootPath.Length)
        .ThenBy(mod => mod.Id, StringComparer.Ordinal)
        .FirstOrDefault();

    private static string OwnerScope(WorkspaceConfiguration configuration, ModInfo? owner) =>
        owner?.PackageId ?? configuration.WorkspaceIdentity;

    private static string DefinitionGroupKey(string scope, string type, string? name) =>
        $@"{scope}\0{type}\0{name ?? string.Empty}";

    private static bool IsDefinitionElement(XElement element) =>
        (!IsElement(element, "Defs") && !IsPatchOperation(element) &&
         element.Elements().Any(child => IsElement(child, "defName"))) ||
        (element.Name.LocalName.EndsWith("Def", StringComparison.Ordinal) && !IsPatchOperation(element));

    private static bool IsPatchOperation(XElement element) =>
        element.Name.LocalName.StartsWith("PatchOperation", StringComparison.OrdinalIgnoreCase) ||
        (AttributeValue(element, "Class")?.StartsWith("PatchOperation", StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool IsReferenceField(string name) =>
        !name.Equals("defName", StringComparison.OrdinalIgnoreCase) &&
        !name.Equals("parentName", StringComparison.OrdinalIgnoreCase) &&
        (name.EndsWith("Def", StringComparison.OrdinalIgnoreCase) ||
         name.EndsWith("Defs", StringComparison.OrdinalIgnoreCase));

    private static bool IsListReferenceField(string name) =>
        name.EndsWith("Defs", StringComparison.OrdinalIgnoreCase);

    private static bool IsAboutPath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        return normalized.EndsWith("/About/About.xml", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("About/About.xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string ModRootPath(string aboutPath)
    {
        var normalized = aboutPath.Replace('\\', '/').Trim('/');
        const string suffix = "/About/About.xml";
        return normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? normalized[..^suffix.Length]
            : string.Empty;
    }

    private static bool IsWithinModRoot(string path, string root)
    {
        var normalizedPath = path.Replace('\\', '/').Trim('/');
        var normalizedRoot = root.Replace('\\', '/').Trim('/');
        return normalizedRoot.Length == 0 ||
               normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstValue(XElement? element, string name)
    {
        var value = element?.Descendants()
            .FirstOrDefault(child => child.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?
            .Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? AttributeValue(XElement element, string name)
    {
        var value = element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?
            .Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsElement(XElement? element, string name) =>
        element is not null && element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static int? LineNumber(XObject? value)
    {
        var lineInfo = value as IXmlLineInfo;
        return lineInfo?.HasLineInfo() == true ? lineInfo.LineNumber : null;
    }

    private static string ElementPath(XElement element)
    {
        var segments = element.AncestorsAndSelf()
            .Reverse()
            .Select(item =>
            {
                var index = item.Parent is null
                    ? 1
                    : item.Parent.Elements()
                        .Where(sibling => sibling.Name.LocalName.Equals(item.Name.LocalName, StringComparison.Ordinal))
                        .TakeWhile(sibling => !ReferenceEquals(sibling, item))
                        .Count() + 1;
                return $@"{item.Name.LocalName}[{index}]";
            });
        return string.Join('/', segments);
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static IReadOnlyList<string> GetStrings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> ChildValues(XElement? root, string name)
    {
        var container = root?.Descendants()
            .FirstOrDefault(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (container is null)
        {
            return [];
        }

        return container.Elements()
            .Select(item =>
                FirstValue(item, "packageId") ??
                (item.Elements().Any() ? null : item.Value.Trim()))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item, StringComparer.Ordinal)
            .ToArray();
    }

    private static class EntityKinds
    {
        public const string Def = "def";
        public const string DefReference = "def_reference";
        public const string Mod = "mod";
        public const string PatchOperation = "patch_operation";
        public const string Relation = "relation";
        public const string Inheritance = "inheritance";
    }

    private sealed record ParsedXmlFile(
        IndexedFileRecord File,
        IReadOnlyList<RawDefinition> Definitions,
        IReadOnlyList<RawPatch> Patches,
        ModInfo? Mod,
        string? Diagnostic);

    private sealed record RawDefinition(
        string Type,
        string? Name,
        string? Parent,
        string? AttributeName,
        string XmlPath,
        int? Line,
        IReadOnlyList<RawReference> References);

    private sealed record RawReference(
        string Field,
        string Target,
        string XmlPath,
        int? Line,
        string ReferenceKind);

    private sealed record RawPatch(string OperationType, string? XPath, string XmlPath, int? Line);

    private sealed record RawReferenceCandidate(DefinitionInfo Definition, IndexedFileRecord File, RawReference Reference);

    private sealed record ModInfo(
        string Id,
        string FileId,
        string RootPath,
        string? PackageId,
        string? Name,
        int? Line,
        IReadOnlyList<string> SupportedVersions,
        IReadOnlyList<string> ModDependencies,
        IReadOnlyList<string> LoadAfter,
        IReadOnlyList<string> LoadBefore,
        IReadOnlyList<string> IncompatibleWith);

    private sealed record DefinitionInfo(
        string Id,
        string Type,
        string? Name,
        string? Parent,
        string? FileId,
        string FilePath,
        int? Line,
        string? OwnerModId,
        string? OwnerModName,
        string Scope,
        string XmlPath);

    private sealed record ReferenceInfo(
        string Id,
        string OwnerDefId,
        string Target,
        string Field,
        string XmlPath,
        string? FileId,
        int? Line,
        string? TargetId,
        string Confidence,
        string ReferenceKind);
}
