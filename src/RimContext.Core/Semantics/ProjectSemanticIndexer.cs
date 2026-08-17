using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using RimContext.Core.Configuration;
using RimContext.Core.Discovery;
using RimContext.Core.Model;
using RimContext.Core.Output;
using RimContext.Core.Storage;

namespace RimContext.Core.Semantics;

public sealed record ProjectSemanticResult(
    IReadOnlyDictionary<string, IndexedFileRecord> FileUpdates,
    IReadOnlyList<EntityRecord> Entities,
    IReadOnlyList<RelationRecord> Relations,
    IReadOnlyList<string> RefreshedFileIds,
    bool RebuildRelations,
    IReadOnlyList<IndexDiagnostic> Diagnostics);

public static class ProjectSemanticIndexer
{
    private const string ProjectKind = "project";
    private const string DependencyKind = "dependency";
    private const string ModKind = "mod";
    private const string DependencyCode = "DEPENDENCY";
    private const string ProjectParseCode = "PROJECT_PARSE";

    public static ProjectSemanticResult Empty { get; } = new(
        new Dictionary<string, IndexedFileRecord>(StringComparer.Ordinal),
        [],
        [],
        [],
        false,
        []);

    public static bool NeedsAnalysis(
        IReadOnlyList<IndexedFileRecord> previousFiles,
        IReadOnlyList<IndexedFileRecord> addedOrChangedFiles,
        IReadOnlyList<string> removedFileIds)
    {
        if (addedOrChangedFiles.Any(file => IsStructureInput(file)))
        {
            return true;
        }

        var previousById = previousFiles.ToDictionary(file => file.Id, StringComparer.Ordinal);
        return removedFileIds
            .Select(id => previousById.TryGetValue(id, out var file) ? file : null)
            .Any(file => file is not null && IsStructureInput(file));
    }

    public static ProjectSemanticResult Analyze(
        WorkspaceConfiguration configuration,
        IReadOnlyList<IndexedFileRecord> currentFiles,
        IReadOnlyList<IndexedFileRecord> filesToAnalyze,
        IReadOnlyList<IndexedFileRecord> previousFiles,
        IReadOnlyList<EntityRecord> previousEntities,
        IReadOnlyList<EntityRecord> semanticEntities,
        IReadOnlyList<string> removedFileIds)
    {
        var currentById = currentFiles.ToDictionary(file => file.Id, StringComparer.Ordinal);
        var changedIds = filesToAnalyze
            .Select(file => file.Id)
            .Concat(removedFileIds)
            .ToHashSet(StringComparer.Ordinal);
        var activePrevious = previousEntities
            .Where(entity => entity.Kind != DependencyKind)
            .Where(entity =>
                entity.FileId is null ||
                (currentById.ContainsKey(entity.FileId) && !changedIds.Contains(entity.FileId)))
            .ToArray();
        var availableEntities = activePrevious
            .Concat(semanticEntities)
            .ToArray();
        var diagnostics = new List<IndexDiagnostic>();
        var fileUpdates = new Dictionary<string, IndexedFileRecord>(StringComparer.Ordinal);
        var projects = ParseProjects(
            configuration,
            currentFiles,
            filesToAnalyze.Select(file => file.Id).ToHashSet(StringComparer.Ordinal),
            activePrevious,
            fileUpdates,
            diagnostics);
        var mods = ParseMods(availableEntities, currentById);
        var projectEntities = projects
            .Select(project => CreateProjectEntity(configuration, project))
            .ToArray();
        var modEntities = mods
            .Select(mod => CreateModEntity(mod))
            .ToArray();
        var refreshedFileIds = currentFiles
            .Where(file =>
                IsProjectDefinition(file.Path) ||
                (file.Kind == DiscoveredFileKinds.Xml && IsAboutPath(file.Path)))
            .Select(file => file.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var entities = new List<EntityRecord>(projectEntities.Length + modEntities.Length);
        entities.AddRange(projectEntities);
        entities.AddRange(modEntities);
        var relations = new List<RelationRecord>();
        var dependencyEntities = new List<EntityRecord>();
        var knownEntityIds = currentFiles
            .Select(file => file.Id)
            .Concat(projectEntities.Select(entity => entity.Id))
            .Concat(modEntities.Select(entity => entity.Id))
            .ToHashSet(StringComparer.Ordinal);

        AddOwnershipEdges(
            configuration,
            currentFiles,
            projects,
            mods,
            projectEntities,
            modEntities,
            knownEntityIds,
            dependencyEntities,
            relations);
        AddModDependencyEdges(
            mods,
            modEntities,
            knownEntityIds,
            dependencyEntities,
            relations,
            diagnostics);
        AddProjectDependencyEdges(
            configuration,
            currentFiles,
            projects,
            projectEntities,
            knownEntityIds,
            dependencyEntities,
            relations,
            diagnostics);
        DetectProjectCycles(projects, diagnostics);

        entities.AddRange(dependencyEntities);
        return new ProjectSemanticResult(
            fileUpdates,
            entities
                .OrderBy(entity => entity.Kind, StringComparer.Ordinal)
                .ThenBy(entity => entity.Id, StringComparer.Ordinal)
                .ToArray(),
            relations
                .OrderBy(relation => relation.Kind, StringComparer.Ordinal)
                .ThenBy(relation => relation.Id, StringComparer.Ordinal)
                .ToArray(),
            refreshedFileIds,
            true,
            diagnostics
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Code, StringComparer.Ordinal)
                .ThenBy(item => item.Message, StringComparer.Ordinal)
                .ToArray());
    }

    private static IReadOnlyList<ProjectInfo> ParseProjects(
        WorkspaceConfiguration configuration,
        IReadOnlyList<IndexedFileRecord> currentFiles,
        IReadOnlySet<string> changedIds,
        IReadOnlyList<EntityRecord> activePrevious,
        IDictionary<string, IndexedFileRecord> fileUpdates,
        ICollection<IndexDiagnostic> diagnostics)
    {
        var storedByFile = activePrevious
            .Where(entity => entity.Kind == ProjectKind && entity.FileId is not null)
            .GroupBy(entity => entity.FileId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var result = new List<ProjectInfo>();
        foreach (var file in currentFiles
                     .Where(item => item.Kind == DiscoveredFileKinds.Project && IsProjectDefinition(item.Path))
                     .OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            ProjectInfo? project = null;
            if (!changedIds.Contains(file.Id) &&
                storedByFile.TryGetValue(file.Id, out var stored))
            {
                project = ParseStoredProject(stored);
            }

            if (project is null)
            {
                project = ParseProject(configuration, file, fileUpdates, diagnostics);
            }

            if (project is not null)
            {
                result.Add(project);
            }
        }

        return result
            .OrderBy(item => item.FilePath, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static ProjectInfo? ParseProject(
        WorkspaceConfiguration configuration,
        IndexedFileRecord file,
        IDictionary<string, IndexedFileRecord> fileUpdates,
        ICollection<IndexDiagnostic> diagnostics)
    {
        var absolutePath = Path.Combine(configuration.RootPath, file.Path.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (Path.GetExtension(file.Path).Equals(".sln", StringComparison.OrdinalIgnoreCase))
            {
                var solutionText = File.ReadAllText(absolutePath);
                var solutionDirectory = Path.GetDirectoryName(absolutePath) ?? configuration.RootPath;
                var solutionReferences = solutionText
                    .Split('\n')
                    .Select(line => Regex.Match(
                        line,
                        @"=\s*""[^""]+"",\s*""([^""]+\.csproj)""",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    .Where(match => match.Success)
                    .Select(match => NormalizeProjectPath(
                        configuration,
                        solutionDirectory,
                        match.Groups[1].Value.Trim()))
                    .Where(item => item is not null)
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item, StringComparer.Ordinal)
                    .ToArray();
                var solution = new ProjectInfo(
                    ProjectId(file),
                    file.Id,
                    file.Path,
                    Path.GetFileNameWithoutExtension(file.Path),
                    "solution",
                    [],
                    null,
                    [],
                    solutionReferences,
                    [],
                    [],
                    "complete",
                    LineNumber(solutionText),
                    null);
                fileUpdates[file.Id] = file with { ParseStatus = "parsed", Diagnostic = null };
                return solution with { SolutionProjectCount = solutionReferences.Length };
            }

            var document = XDocument.Load(
                absolutePath,
                LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root is null)
            {
                throw new InvalidDataException("The project file has no root element.");
            }

            var assemblyName = FirstValue(root, "AssemblyName");
            var name = string.IsNullOrWhiteSpace(assemblyName)
                ? Path.GetFileNameWithoutExtension(file.Path)
                : assemblyName;
            var targetFrameworks = FirstValue(root, "TargetFrameworks")
                ?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Concat(FirstValue(root, "TargetFramework")?
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item, StringComparer.Ordinal)
                .ToArray() ?? [];
            var projectDirectory = Path.GetDirectoryName(absolutePath) ?? configuration.RootPath;
            var compileIncludes = root.Descendants()
                .Where(item => item.Name.LocalName.Equals("Compile", StringComparison.OrdinalIgnoreCase))
                .Select(item => (string?)item.Attribute("Include") ?? (string?)item.Attribute("Update"))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => NormalizeProjectPath(configuration, projectDirectory, item!))
                .Where(item => item is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item, StringComparer.Ordinal)
                .ToArray();
            var projectReferences = root.Descendants()
                .Where(item => item.Name.LocalName.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase))
                .Select(item => (string?)item.Attribute("Include"))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => NormalizeProjectPath(configuration, projectDirectory, item!))
                .Where(item => item is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item, StringComparer.Ordinal)
                .ToArray();
            var packageReferences = root.Descendants()
                .Where(item => item.Name.LocalName.Equals("PackageReference", StringComparison.OrdinalIgnoreCase))
                .Select(item => new PackageReferenceInfo(
                    ((string?)item.Attribute("Include") ?? string.Empty).Trim(),
                    ((string?)item.Attribute("Version") ?? FirstValue(item, "Version"))?.Trim()))
                .Where(item => item.Include.Length > 0)
                .Distinct()
                .OrderBy(item => item.Include, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var assemblyReferences = root.Descendants()
                .Where(item => item.Name.LocalName.Equals("Reference", StringComparison.OrdinalIgnoreCase))
                .Select(item => ((string?)item.Attribute("Include") ?? string.Empty).Split(',')[0].Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item, StringComparer.Ordinal)
                .ToArray();
            var staticEvaluation = compileIncludes.Length == 0 ||
                                   compileIncludes.Any(item => item.Contains('*', StringComparison.Ordinal)) ||
                                   root.Descendants().Any(item => item.Name.LocalName.Equals("Import", StringComparison.OrdinalIgnoreCase))
                ? "partial"
                : "complete";
            var parsed = new ProjectInfo(
                ProjectId(file),
                file.Id,
                file.Path,
                name,
                "csproj",
                targetFrameworks,
                FirstValue(root, "RootNamespace"),
                compileIncludes,
                projectReferences,
                packageReferences,
                assemblyReferences,
                staticEvaluation,
                LineNumber(root),
                null);
            fileUpdates[file.Id] = file with { ParseStatus = "parsed", Diagnostic = null };
            return parsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            var message = $"Unable to parse project metadata: {ex.Message}";
            diagnostics.Add(new IndexDiagnostic(file.Path, message, ProjectParseCode));
            fileUpdates[file.Id] = file with { ParseStatus = "error", Diagnostic = message };
            return new ProjectInfo(
                ProjectId(file),
                file.Id,
                file.Path,
                Path.GetFileNameWithoutExtension(file.Path),
                Path.GetExtension(file.Path).Equals(".sln", StringComparison.OrdinalIgnoreCase) ? "solution" : "csproj",
                [],
                null,
                [],
                [],
                [],
                [],
                "partial",
                null,
                message);
        }
    }

    private static ProjectInfo? ParseStoredProject(EntityRecord entity)
    {
        try
        {
            using var document = JsonDocument.Parse(entity.PayloadJson);
            var root = document.RootElement;
            var filePath = GetString(root, "file") ?? string.Empty;
            var name = GetString(root, "name") ?? filePath;
            return new ProjectInfo(
                entity.Id,
                entity.FileId ?? string.Empty,
                filePath,
                name,
                GetString(root, "projectKind") ?? "csproj",
                GetStrings(root, "targetFrameworks"),
                GetString(root, "rootNamespace"),
                GetStrings(root, "compileIncludes"),
                GetStrings(root, "projectReferences"),
                GetPackageReferences(root),
                GetStrings(root, "assemblyReferences"),
                GetString(root, "staticEvaluation") ?? "partial",
                entity.Line,
                GetString(root, "diagnostic"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ModInfo> ParseMods(
        IReadOnlyList<EntityRecord> entities,
        IReadOnlyDictionary<string, IndexedFileRecord> files)
    {
        var result = new List<ModInfo>();
        foreach (var entity in entities.Where(item => item.Kind == ModKind))
        {
            try
            {
                using var document = JsonDocument.Parse(entity.PayloadJson);
                var root = document.RootElement;
                var filePath = entity.FileId is not null && files.TryGetValue(entity.FileId, out var file)
                    ? file.Path
                    : GetString(root, "aboutFile") ?? string.Empty;
                result.Add(new ModInfo(
                    entity.Id,
                    entity.FileId ?? string.Empty,
                    GetString(root, "modRoot") ?? ModRootPath(filePath),
                    GetString(root, "packageId"),
                    GetString(root, "name"),
                    GetString(root, "aboutFile") ?? filePath,
                    entity.Line,
                    GetStrings(root, "supportedVersions"),
                    GetStrings(root, "modDependencies"),
                    GetStrings(root, "loadAfter"),
                    GetStrings(root, "loadBefore"),
                    GetStrings(root, "incompatibleWith")));
            }
            catch (JsonException)
            {
            }
        }

        return result
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.PackageId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.RootPath, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static EntityRecord CreateProjectEntity(
        WorkspaceConfiguration configuration,
        ProjectInfo project)
    {
        return new EntityRecord(
            project.Id,
            ProjectKind,
            $"{configuration.WorkspaceIdentity}\0{project.FilePath}",
            project.FileId,
            project.Line,
            JsonOutput.SerializePayload(new
            {
                file = project.FilePath,
                name = project.Name,
                projectKind = project.ProjectKind,
                targetFrameworks = project.TargetFrameworks,
                rootNamespace = project.RootNamespace,
                compileIncludes = project.CompileIncludes,
                projectReferences = project.ProjectReferences,
                packageReferences = project.PackageReferences,
                assemblyReferences = project.AssemblyReferences,
                staticEvaluation = project.StaticEvaluation,
                solutionProjectCount = project.SolutionProjectCount,
                diagnostic = project.Diagnostic
            }));
    }

    private static EntityRecord CreateModEntity(ModInfo mod)
    {
        return new EntityRecord(
            mod.Id,
            ModKind,
            $"{mod.PackageId ?? mod.RootPath}\0{mod.RootPath}",
            mod.FileId,
            mod.Line,
            JsonOutput.SerializePayload(new
            {
                packageId = mod.PackageId,
                name = mod.Name,
                modRoot = mod.RootPath,
                aboutFile = mod.AboutFile,
                supportedVersions = mod.SupportedVersions,
                modDependencies = mod.ModDependencies,
                loadAfter = mod.LoadAfter,
                loadBefore = mod.LoadBefore,
                incompatibleWith = mod.IncompatibleWith
            }));
    }

    private static void AddOwnershipEdges(
        WorkspaceConfiguration configuration,
        IReadOnlyList<IndexedFileRecord> currentFiles,
        IReadOnlyList<ProjectInfo> projects,
        IReadOnlyList<ModInfo> mods,
        IReadOnlyList<EntityRecord> projectEntities,
        IReadOnlyList<EntityRecord> modEntities,
        ISet<string> knownEntityIds,
        ICollection<EntityRecord> dependencyEntities,
        ICollection<RelationRecord> relations)
    {
        var projectById = projects.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var modById = mods.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var projectEntitiesById = projectEntities.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var modEntitiesById = modEntities.ToDictionary(item => item.Id, StringComparer.Ordinal);

        foreach (var file in currentFiles.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            foreach (var mod in mods.Where(item => IsUnder(item.RootPath, file.Path)))
            {
                AddEdge(
                    mod.Id,
                    file.Id,
                    file.Path,
                    "owns",
                    mod.FileId,
                    mod.Line,
                    "exact",
                    "resolved",
                    dependencyEntities,
                    relations,
                    knownEntityIds);
            }

            if (file.Kind is not (DiscoveredFileKinds.Source or DiscoveredFileKinds.Xml or DiscoveredFileKinds.Assembly))
            {
                continue;
            }

            var project = FindOwningProject(projects, file.Path);
            if (project is not null)
            {
                AddEdge(
                    project.Id,
                    file.Id,
                    file.Path,
                    "owns",
                    project.FileId,
                    project.Line,
                    project.CompileIncludes.Any(item => GlobMatches(item, file.Path)) ? "exact" : "heuristic",
                    "resolved",
                    dependencyEntities,
                    relations,
                    knownEntityIds);
            }
        }

        foreach (var project in projects)
        {
            foreach (var mod in mods.Where(item => IsUnder(item.RootPath, project.FilePath)))
            {
                AddEdge(
                    mod.Id,
                    project.Id,
                    project.FilePath,
                    "owns",
                    mod.FileId,
                    mod.Line,
                    "exact",
                    "resolved",
                    dependencyEntities,
                    relations,
                    knownEntityIds);
            }
        }
    }

    private static void AddModDependencyEdges(
        IReadOnlyList<ModInfo> mods,
        IReadOnlyList<EntityRecord> modEntities,
        ISet<string> knownEntityIds,
        ICollection<EntityRecord> dependencyEntities,
        ICollection<RelationRecord> relations,
        ICollection<IndexDiagnostic> diagnostics)
    {
        var byPackage = mods
            .Where(item => !string.IsNullOrWhiteSpace(item.PackageId))
            .GroupBy(item => item.PackageId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var modEntityIds = modEntities.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var duplicate in byPackage.Where(item => item.Value.Length > 1))
        {
            foreach (var mod in duplicate.Value)
            {
                diagnostics.Add(new IndexDiagnostic(
                    mod.AboutFile,
                    $"Duplicate mod packageId '{duplicate.Key}'.",
                    DependencyCode));
            }
        }

        foreach (var mod in mods)
        {
            AddModDependencyKind(mod, mod.ModDependencies, "requires", byPackage, modEntityIds, knownEntityIds, dependencyEntities, relations, diagnostics, true);
            AddModDependencyKind(mod, mod.LoadAfter, "load_after", byPackage, modEntityIds, knownEntityIds, dependencyEntities, relations, diagnostics, false);
            AddModDependencyKind(mod, mod.LoadBefore, "load_before", byPackage, modEntityIds, knownEntityIds, dependencyEntities, relations, diagnostics, false);
            AddModDependencyKind(mod, mod.IncompatibleWith, "incompatible", byPackage, modEntityIds, knownEntityIds, dependencyEntities, relations, diagnostics, false);
        }
    }

    private static void AddModDependencyKind(
        ModInfo source,
        IReadOnlyList<string> targets,
        string relationKind,
        IReadOnlyDictionary<string, ModInfo[]> byPackage,
        IReadOnlySet<string> modEntityIds,
        ISet<string> knownEntityIds,
        ICollection<EntityRecord> dependencyEntities,
        ICollection<RelationRecord> relations,
        ICollection<IndexDiagnostic> diagnostics,
        bool required)
    {
        foreach (var target in targets.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ThenBy(item => item, StringComparer.Ordinal))
        {
            var matches = byPackage.TryGetValue(target, out var values) ? values : [];
            var targetId = matches.Length == 1 && modEntityIds.Contains(matches[0].Id)
                ? matches[0].Id
                : null;
            var resolution = targetId is not null
                ? "resolved"
                : matches.Length > 1
                    ? "ambiguous"
                    : "unresolved";
            AddEdge(
                source.Id,
                targetId,
                target,
                relationKind,
                source.FileId,
                source.Line,
                targetId is not null ? "exact" : "heuristic",
                resolution,
                dependencyEntities,
                relations,
                knownEntityIds);
            if (required && targetId is null)
            {
                diagnostics.Add(new IndexDiagnostic(
                    source.AboutFile,
                    $"Missing required local mod '{target}'.",
                    DependencyCode));
            }
        }
    }

    private static void AddProjectDependencyEdges(
        WorkspaceConfiguration configuration,
        IReadOnlyList<IndexedFileRecord> currentFiles,
        IReadOnlyList<ProjectInfo> projects,
        IReadOnlyList<EntityRecord> projectEntities,
        ISet<string> knownEntityIds,
        ICollection<EntityRecord> dependencyEntities,
        ICollection<RelationRecord> relations,
        ICollection<IndexDiagnostic> diagnostics)
    {
        var projectsByPath = projects
            .ToDictionary(item => item.FilePath, item => item, StringComparer.OrdinalIgnoreCase);
        var assemblies = currentFiles
            .Where(file => file.Kind == DiscoveredFileKinds.Assembly)
            .GroupBy(file => Path.GetFileNameWithoutExtension(file.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            foreach (var reference in project.ProjectReferences)
            {
                var target = projectsByPath.TryGetValue(reference, out var targetProject)
                    ? targetProject.Id
                    : null;
                AddEdge(
                    project.Id,
                    target,
                    reference,
                    "project_reference",
                    project.FileId,
                    project.Line,
                    target is not null ? "exact" : "heuristic",
                    target is not null ? "resolved" : "unresolved",
                    dependencyEntities,
                    relations,
                    knownEntityIds);
                if (target is null)
                {
                    diagnostics.Add(new IndexDiagnostic(
                        project.FilePath,
                        $"Missing project reference '{reference}'.",
                        DependencyCode));
                }
            }

            foreach (var package in project.PackageReferences)
            {
                var target = package.Version is null
                    ? package.Include
                    : $"{package.Include}@{package.Version}";
                AddEdge(
                    project.Id,
                    null,
                    target,
                    "requires",
                    project.FileId,
                    project.Line,
                    "exact",
                    "external",
                    dependencyEntities,
                    relations,
                    knownEntityIds);
            }

            foreach (var assemblyReference in project.AssemblyReferences)
            {
                var targetFiles = assemblies.TryGetValue(assemblyReference, out var values) ? values : [];
                var target = targetFiles.Length == 1 ? targetFiles[0].Id : null;
                AddEdge(
                    project.Id,
                    target,
                    assemblyReference,
                    "assembly_reference",
                    project.FileId,
                    project.Line,
                    target is not null ? "exact" : "heuristic",
                    target is not null ? "resolved" : "unresolved",
                    dependencyEntities,
                    relations,
                    knownEntityIds);
            }
        }
    }

    private static void DetectProjectCycles(
        IReadOnlyList<ProjectInfo> projects,
        ICollection<IndexDiagnostic> diagnostics)
    {
        var byPath = projects.ToDictionary(item => item.FilePath, StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            Visit(project, []);
        }

        void Visit(ProjectInfo project, IReadOnlyList<string> stack)
        {
            if (state.TryGetValue(project.FilePath, out var value))
            {
                if (value == 1 && reported.Add(project.FilePath))
                {
                    diagnostics.Add(new IndexDiagnostic(
                        project.FilePath,
                        $"Project reference cycle detected at '{project.FilePath}'.",
                        DependencyCode));
                }

                return;
            }

            state[project.FilePath] = 1;
            var nextStack = stack.Concat([project.FilePath]).ToArray();
            foreach (var reference in project.ProjectReferences)
            {
                if (byPath.TryGetValue(reference, out var target))
                {
                    Visit(target, nextStack);
                }
            }

            state[project.FilePath] = 2;
        }
    }

    private static void AddEdge(
        string fromId,
        string? toId,
        string target,
        string relationKind,
        string? fileId,
        int? line,
        string confidence,
        string resolutionState,
        ICollection<EntityRecord> dependencyEntities,
        ICollection<RelationRecord> relations,
        ISet<string> knownEntityIds)
    {
        if (!knownEntityIds.Contains(fromId))
        {
            return;
        }

        if (toId is not null && !knownEntityIds.Contains(toId))
        {
            toId = null;
            resolutionState = "unresolved";
        }

        var evidence = $"{relationKind}\0{target}\0{fileId ?? string.Empty}";
        var dependencyId = StableEntityId.Create(DependencyKind, fromId, evidence);
        if (dependencyEntities.All(entity => !entity.Id.Equals(dependencyId, StringComparison.Ordinal)))
        {
            dependencyEntities.Add(new EntityRecord(
                dependencyId,
                DependencyKind,
                $"{fromId}\0{evidence}",
                fileId,
                line,
                JsonOutput.SerializePayload(new
                {
                    relation = relationKind,
                    fromId,
                    toId,
                    target,
                    resolutionState,
                    confidence,
                    evidenceFileId = fileId,
                    line
                })));
            knownEntityIds.Add(dependencyId);
        }

        var relationId = StableEntityId.Create(
            "relation",
            fromId,
            $"{relationKind}\0{toId ?? target}\0{fileId ?? string.Empty}");
        if (relations.All(relation => !relation.Id.Equals(relationId, StringComparison.Ordinal)))
        {
            relations.Add(new RelationRecord(
                relationId,
                fromId,
                toId,
                relationKind,
                fileId,
                line,
                JsonOutput.SerializePayload(new
                {
                    relation = relationKind,
                    target,
                    targetId = toId,
                    resolutionState,
                    confidence,
                    fileId,
                    line
                })));
        }
    }

    private static ProjectInfo? FindOwningProject(
        IReadOnlyList<ProjectInfo> projects,
        string filePath)
    {
        var explicitMatches = projects
            .Where(project => project.CompileIncludes.Any(item => GlobMatches(item, filePath)))
            .OrderBy(project => project.CompileIncludes.Any(item =>
                !item.Contains('*', StringComparison.Ordinal) &&
                item.Equals(filePath, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
            .ThenByDescending(project => ProjectDirectory(project.FilePath).Length)
            .ThenBy(project => project.FilePath, StringComparer.Ordinal)
            .ToArray();
        if (explicitMatches.Length > 0)
        {
            return explicitMatches[0];
        }

        return projects
            .Where(project => IsUnder(ProjectDirectory(project.FilePath), filePath))
            .OrderByDescending(project => ProjectDirectory(project.FilePath).Length)
            .ThenBy(project => project.FilePath, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool GlobMatches(string pattern, string path)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return pattern.Equals(path, StringComparison.OrdinalIgnoreCase);
        }

        var expression = "^" +
                         Regex.Escape(pattern)
                             .Replace(@"\*\*", ".*", StringComparison.Ordinal)
                             .Replace(@"\*", "[^/]*", StringComparison.Ordinal) +
                         "$";
        return Regex.IsMatch(path, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string ProjectDirectory(string projectPath)
    {
        var separator = projectPath.LastIndexOf('/');
        return separator < 0 ? string.Empty : projectPath[..separator];
    }

    private static string? NormalizeProjectPath(
        WorkspaceConfiguration configuration,
        string projectDirectory,
        string include)
    {
        try
        {
            var absolute = Path.GetFullPath(Path.Combine(
                projectDirectory.Length == 0
                    ? configuration.RootPath
                    : Path.Combine(configuration.RootPath, projectDirectory.Replace('/', Path.DirectorySeparatorChar)),
                include.Replace('/', Path.DirectorySeparatorChar)));
            if (PathUtilities.IsWithin(configuration.RootPath, absolute))
            {
                return PathUtilities.NormalizeRelativePath(configuration.RootPath, absolute);
            }

            return absolute.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsStructureInput(IndexedFileRecord file)
    {
        return file.Kind is DiscoveredFileKinds.Source or
            DiscoveredFileKinds.Xml or
            DiscoveredFileKinds.Project or
            DiscoveredFileKinds.Assembly;
    }

    private static bool IsProjectDefinition(string path)
    {
        return path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAboutPath(string path)
    {
        return path.EndsWith("/About/About.xml", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("About/About.xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnder(string root, string path)
    {
        var normalizedRoot = root.Trim('/').Replace('\\', '/');
        var normalizedPath = path.Trim('/').Replace('\\', '/');
        return normalizedRoot.Length > 0 &&
               (normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string ModRootPath(string aboutPath)
    {
        var normalized = aboutPath.Replace('\\', '/');
        const string suffix = "/About/About.xml";
        var index = normalized.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
        return index >= 0
            ? normalized[..index]
            : normalized.Contains("/About.xml", StringComparison.OrdinalIgnoreCase)
                ? normalized[..normalized.LastIndexOf("/About.xml", StringComparison.OrdinalIgnoreCase)]
                : ProjectDirectory(normalized);
    }

    private static string? FirstValue(XElement? root, string name)
    {
        return root?
            .DescendantsAndSelf()
            .FirstOrDefault(item => item.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value
            .Trim();
    }

    private static string ProjectId(IndexedFileRecord file) =>
        StableEntityId.Create(ProjectKind, file.WorkspaceIdentity, $"model\0{file.Path}");

    private static int? LineNumber(XElement? element)
    {
        return element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : null;
    }

    private static int? LineNumber(string text)
    {
        return string.IsNullOrEmpty(text) ? null : 1;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.String
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

    private static IReadOnlyList<PackageReferenceInfo> GetPackageReferences(JsonElement element)
    {
        if (!element.TryGetProperty("packageReferences", out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<PackageReferenceInfo>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var include = GetString(item, "include");
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            result.Add(new PackageReferenceInfo(include, GetString(item, "version")));
        }

        return result
            .Distinct()
            .OrderBy(item => item.Include, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed record ProjectInfo(
        string Id,
        string FileId,
        string FilePath,
        string Name,
        string ProjectKind,
        IReadOnlyList<string> TargetFrameworks,
        string? RootNamespace,
        IReadOnlyList<string> CompileIncludes,
        IReadOnlyList<string> ProjectReferences,
        IReadOnlyList<PackageReferenceInfo> PackageReferences,
        IReadOnlyList<string> AssemblyReferences,
        string StaticEvaluation,
        int? Line,
        string? Diagnostic,
        int SolutionProjectCount = 0);

    private sealed record PackageReferenceInfo(string Include, string? Version);

    private sealed record ModInfo(
        string Id,
        string FileId,
        string RootPath,
        string? PackageId,
        string? Name,
        string AboutFile,
        int? Line,
        IReadOnlyList<string> SupportedVersions,
        IReadOnlyList<string> ModDependencies,
        IReadOnlyList<string> LoadAfter,
        IReadOnlyList<string> LoadBefore,
        IReadOnlyList<string> IncompatibleWith);
}
