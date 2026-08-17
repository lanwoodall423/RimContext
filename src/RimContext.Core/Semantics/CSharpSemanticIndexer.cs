using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RimContext.Core.Configuration;
using RimContext.Core.Discovery;
using RimContext.Core.Model;
using RimContext.Core.Output;
using RimContext.Core.Storage;

namespace RimContext.Core.Semantics;

public sealed record CSharpSemanticResult(
    IReadOnlyDictionary<string, IndexedFileRecord> FileUpdates,
    IReadOnlyList<EntityRecord> Entities,
    IReadOnlyList<RelationRecord> Relations,
    IReadOnlyList<string> RefreshedFileIds,
    bool RebuildRelations,
    IReadOnlyList<IndexDiagnostic> Diagnostics);

public static class CSharpSemanticIndexer
{
    private const string TypeKind = "csharp_type";
    private const string MemberKind = "csharp_member";
    private const string HarmonyKind = "harmony_patch";

    public static CSharpSemanticResult Empty { get; } = new(
        new Dictionary<string, IndexedFileRecord>(StringComparer.Ordinal), [], [], [], false, []);

    public static IReadOnlyList<IndexedFileRecord> SelectFilesToAnalyze(
        IReadOnlyList<IndexedFileRecord> currentFiles,
        IReadOnlyList<IndexedFileRecord> previousFiles,
        IReadOnlyList<IndexedFileRecord> changedFiles,
        IReadOnlyList<string> removedFileIds,
        bool assemblyChanged = false)
    {
        var changedSources = changedFiles
            .Where(file => file.Kind == DiscoveredFileKinds.Source)
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ThenBy(file => file.Id, StringComparer.Ordinal)
            .ToArray();
        if (!assemblyChanged)
        {
            return changedSources;
        }

        return changedSources
            .Concat(currentFiles.Where(file => file.Kind == DiscoveredFileKinds.Source))
            .GroupBy(file => file.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ThenBy(file => file.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public static CSharpSemanticResult Analyze(
        WorkspaceConfiguration configuration,
        IReadOnlyList<IndexedFileRecord> currentFiles,
        IReadOnlyList<IndexedFileRecord> filesToAnalyze,
        IReadOnlyList<IndexedFileRecord> previousFiles,
        IReadOnlyList<EntityRecord> previousEntities,
        IReadOnlyList<string> removedFileIds)
    {
        var sourceFiles = currentFiles
            .Where(file => file.Kind == DiscoveredFileKinds.Source)
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ThenBy(file => file.Id, StringComparer.Ordinal)
            .ToArray();
        var sourceIds = sourceFiles.Select(file => file.Id).ToHashSet(StringComparer.Ordinal);
        var changedIds = filesToAnalyze.Select(file => file.Id).ToHashSet(StringComparer.Ordinal);
        var removedIds = removedFileIds
            .Where(id => previousFiles.Any(file => file.Id == id && file.Kind == DiscoveredFileKinds.Source))
            .ToHashSet(StringComparer.Ordinal);

        if (filesToAnalyze.Count == 0 && removedIds.Count == 0)
        {
            return Empty;
        }

        var assemblyResolver = HarmonyAssemblyResolver.Create(configuration, currentFiles);
        var parsed = filesToAnalyze
            .Where(file => file.Kind == DiscoveredFileKinds.Source)
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => ParseFile(configuration, file))
            .ToArray();

        var types = previousEntities
            .Where(entity => entity.Kind == TypeKind)
            .SelectMany(ParseStoredTypes)
            .Where(item => sourceIds.Contains(item.FileId) &&
                           !changedIds.Contains(item.FileId) &&
                           !removedIds.Contains(item.FileId))
            .Concat(parsed.SelectMany(item => item.Types))
            .GroupBy(TypeIdentity, StringComparer.Ordinal)
            .Select(group => AggregateType(group.Key, group))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var byTypeKey = types.ToDictionary(item => item.Identity, StringComparer.Ordinal);

        var members = previousEntities
            .Where(entity => entity.Kind == MemberKind)
            .SelectMany(ParseStoredMembers)
            .Where(item => sourceIds.Contains(item.FileId) &&
                           !changedIds.Contains(item.FileId) &&
                           !removedIds.Contains(item.FileId))
            .Concat(parsed.SelectMany(item => item.Members))
            .Where(item => byTypeKey.ContainsKey(item.TypeIdentity))
            .GroupBy(item => MemberIdentity(item, byTypeKey[item.TypeIdentity].Id), StringComparer.Ordinal)
            .Select(group => AggregateMember(group.Key, group, byTypeKey[group.First().TypeIdentity]))
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var membersByType = members
            .GroupBy(item => item.TypeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var harmonyPatches = previousEntities
            .Where(entity => entity.Kind == HarmonyKind)
            .SelectMany(ParseStoredHarmonyPatches)
            .Where(item => sourceIds.Contains(item.FileId) &&
                           !changedIds.Contains(item.FileId) &&
                           !removedIds.Contains(item.FileId))
            .Concat(parsed.SelectMany(item => item.HarmonyPatches))
            .OrderBy(item => item.FilePath, StringComparer.Ordinal)
            .ThenBy(item => item.Line ?? int.MaxValue)
            .ThenBy(item => item.PatchClass, StringComparer.Ordinal)
            .ThenBy(item => item.PatchMethod, StringComparer.Ordinal)
            .ThenBy(item => item.PatchKind, StringComparer.Ordinal)
            .ToArray();

        var entities = types.Select(CreateTypeEntity)
            .Concat(members.Select(CreateMemberEntity))
            .Concat(harmonyPatches.Select(patch => CreateHarmonyEntity(
                patch,
                types,
                membersByType,
                assemblyResolver)))
            .ToArray();
        var relations = BuildRelations(types, members, byTypeKey, membersByType)
            .Concat(BuildHarmonyRelations(harmonyPatches, types, membersByType, assemblyResolver))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var updates = parsed.ToDictionary(
            item => item.File.Id,
            item => item.File with
            {
                ParseStatus = item.Diagnostic is null ? "parsed" : "error",
                Diagnostic = item.Diagnostic
            },
            StringComparer.Ordinal);
        var diagnostics = parsed
            .Where(item => item.Diagnostic is not null)
            .Select(item => new IndexDiagnostic(item.File.Path, item.Diagnostic!))
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();

        return new CSharpSemanticResult(
            updates,
            entities,
            relations,
            sourceFiles.Select(file => file.Id).ToArray(),
            true,
            diagnostics);
    }

    private static ParsedFile ParseFile(WorkspaceConfiguration configuration, IndexedFileRecord file)
    {
        try
        {
            var absolutePath = Path.Combine(
                configuration.RootPath,
                file.Path.Replace('/', Path.DirectorySeparatorChar));
            var text = File.ReadAllText(absolutePath, Encoding.UTF8);
            var tree = CSharpSyntaxTree.ParseText(
                text,
                new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse),
                file.Path,
                Encoding.UTF8);
            var root = tree.GetCompilationUnitRoot();
            var constants = StringConstants(root);
            var types = root.DescendantNodes()
                .OfType<BaseTypeDeclarationSyntax>()
                .Select(type => ParseType(file, type))
                .OrderBy(type => type.QualifiedName, StringComparer.Ordinal)
                .ThenBy(type => type.FilePath, StringComparer.Ordinal)
                .ThenBy(type => type.Line)
                .ToArray();
            var members = root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .SelectMany(type => ParseMembers(file, type))
                .OrderBy(member => member.TypeIdentity, StringComparer.Ordinal)
                .ThenBy(member => member.Signature, StringComparer.Ordinal)
                .ThenBy(member => member.FilePath, StringComparer.Ordinal)
                .ToArray();
            var harmonyPatches = root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .SelectMany(type => ParseHarmonyPatches(file, type, constants))
                .Concat(ParseHarmonyCalls(file, root, constants))
                .OrderBy(patch => patch.FilePath, StringComparer.Ordinal)
                .ThenBy(patch => patch.Line ?? int.MaxValue)
                .ThenBy(patch => patch.PatchClass, StringComparer.Ordinal)
                .ThenBy(patch => patch.PatchMethod, StringComparer.Ordinal)
                .ThenBy(patch => patch.PatchSignature, StringComparer.Ordinal)
                .ThenBy(patch => patch.PatchKind, StringComparer.Ordinal)
                .ToArray();
            var error = tree.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .OrderBy(diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition.Line)
                .ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            return new ParsedFile(
                file,
                types,
                members,
                harmonyPatches,
                error is null ? null : DiagnosticText(error));
        }
        catch (IOException)
        {
            return new ParsedFile(file, [], [], [], "The C# source file could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            return new ParsedFile(file, [], [], [], "The C# source file could not be read.");
        }
        catch (ArgumentException)
        {
            return new ParsedFile(file, [], [], [], "The C# source file could not be parsed.");
        }
    }

    private static TypeDeclaration ParseType(IndexedFileRecord file, BaseTypeDeclarationSyntax syntax)
    {
        var namespaceName = NamespaceName(syntax);
        var containing = syntax.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .Reverse()
            .Select(TypeName)
            .ToArray();
        var qualifiedName = string.Join(
            ".",
            new[] { namespaceName }
                .Where(item => item.Length > 0)
                .Concat(containing)
                .Append(TypeName(syntax)));
        var baseNames = syntax.BaseList?.Types
            .Select(item => Normalize(item.Type.ToString()))
            .Where(item => item.Length > 0)
            .ToArray() ?? [];
        var typeKind = GetTypeKind(syntax);
        var baseName = typeKind is "class" or "record" or "record_struct"
            ? baseNames.FirstOrDefault()
            : null;
        var interfaces = typeKind is "class" or "record" or "record_struct"
            ? baseNames.Skip(baseName is null ? 0 : 1).ToArray()
            : baseNames;
        var modifiers = syntax.Modifiers;
        return new TypeDeclaration(
            file.WorkspaceIdentity,
            file.Id,
            file.Path,
            LineNumber(syntax),
            namespaceName,
            syntax.Identifier.ValueText,
            qualifiedName,
            Arity(syntax),
            typeKind,
            containing.Length == 0 ? null : string.Join(
                ".",
                new[] { namespaceName }
                    .Where(item => item.Length > 0)
                    .Concat(containing)),
            baseName,
            interfaces,
            Visibility(modifiers, true, containing.Length > 0, typeKind == "interface"),
            HasModifier(modifiers, SyntaxKind.StaticKeyword),
            HasModifier(modifiers, SyntaxKind.AbstractKeyword),
            HasModifier(modifiers, SyntaxKind.SealedKeyword),
            HasModifier(modifiers, SyntaxKind.PartialKeyword),
            Attributes(syntax.AttributeLists));
    }

    private static IEnumerable<MemberDeclaration> ParseMembers(
        IndexedFileRecord file,
        TypeDeclarationSyntax type)
    {
        var namespaceName = NamespaceName(type);
        var containing = type.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .Reverse()
            .Select(TypeName)
            .ToArray();
        var qualifiedName = string.Join(
            ".",
            new[] { namespaceName }
                .Where(item => item.Length > 0)
                .Concat(containing)
                .Append(TypeName(type)));
        var typeIdentity = TypeIdentity(new TypeDeclaration(
            file.WorkspaceIdentity,
            file.Id,
            file.Path,
            LineNumber(type),
            namespaceName,
            type.Identifier.ValueText,
            qualifiedName,
            Arity(type),
            GetTypeKind(type),
            containing.Length == 0 ? null : string.Join(
                ".",
                new[] { namespaceName }
                    .Where(item => item.Length > 0)
                    .Concat(containing)),
            null,
            [],
            "internal",
            false,
            false,
            false,
            false,
            []));
        var interfaceMember = GetTypeKind(type) == "interface";

        foreach (var member in type.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    {
                        var parameters = Parameters(method.ParameterList.Parameters);
                        var name = method.ExplicitInterfaceSpecifier is null
                            ? method.Identifier.ValueText
                            : Normalize(method.ExplicitInterfaceSpecifier.Name.ToString()) +
                              "." + method.Identifier.ValueText;
                        var signature = name +
                            ((char)96).ToString() +
                            (method.TypeParameterList?.Parameters.Count ?? 0) +
                            "(" + string.Join(",", parameters) + ")";
                        yield return CreateMember(
                            file,
                            typeIdentity,
                            "method",
                            name,
                            signature,
                            method.ReturnType.ToString(),
                            parameters,
                            method.Modifiers,
                            method.AttributeLists,
                            method,
                            interfaceMember);
                        break;
                    }
                case ConstructorDeclarationSyntax constructor:
                    {
                        var parameters = Parameters(constructor.ParameterList.Parameters);
                        yield return CreateMember(
                            file,
                            typeIdentity,
                            "constructor",
                            ".ctor",
                            ".ctor(" + string.Join(",", parameters) + ")",
                            null,
                            parameters,
                            constructor.Modifiers,
                            constructor.AttributeLists,
                            constructor,
                            interfaceMember);
                        break;
                    }
                case PropertyDeclarationSyntax property:
                    yield return CreateMember(
                        file,
                        typeIdentity,
                        "property",
                        property.Identifier.ValueText,
                        property.Identifier.ValueText + ":" + Normalize(property.Type.ToString()),
                        property.Type.ToString(),
                        [],
                        property.Modifiers,
                        property.AttributeLists,
                        property,
                        interfaceMember);
                    break;
                case IndexerDeclarationSyntax indexer:
                    {
                        var parameters = Parameters(indexer.ParameterList.Parameters);
                        yield return CreateMember(
                            file,
                            typeIdentity,
                            "property",
                            "this",
                            "this(" + string.Join(",", parameters) + "):" + Normalize(indexer.Type.ToString()),
                            indexer.Type.ToString(),
                            parameters,
                            indexer.Modifiers,
                            indexer.AttributeLists,
                            indexer,
                            interfaceMember);
                        break;
                    }
                case FieldDeclarationSyntax field:
                    foreach (var variable in field.Declaration.Variables)
                    {
                        yield return CreateMember(
                            file,
                            typeIdentity,
                            "field",
                            variable.Identifier.ValueText,
                            variable.Identifier.ValueText + ":" + Normalize(field.Declaration.Type.ToString()),
                            field.Declaration.Type.ToString(),
                            [],
                            field.Modifiers,
                            field.AttributeLists,
                            field,
                            interfaceMember);
                    }

                    break;
                case EventFieldDeclarationSyntax eventField:
                    foreach (var variable in eventField.Declaration.Variables)
                    {
                        yield return CreateMember(
                            file,
                            typeIdentity,
                            "event",
                            variable.Identifier.ValueText,
                            variable.Identifier.ValueText + ":" + Normalize(eventField.Declaration.Type.ToString()),
                            eventField.Declaration.Type.ToString(),
                            [],
                            eventField.Modifiers,
                            eventField.AttributeLists,
                            eventField,
                            interfaceMember);
                    }

                    break;
                case EventDeclarationSyntax eventDeclaration:
                    yield return CreateMember(
                        file,
                        typeIdentity,
                        "event",
                        eventDeclaration.Identifier.ValueText,
                        eventDeclaration.Identifier.ValueText + ":" + Normalize(eventDeclaration.Type.ToString()),
                        eventDeclaration.Type.ToString(),
                        [],
                        eventDeclaration.Modifiers,
                        eventDeclaration.AttributeLists,
                        eventDeclaration,
                        interfaceMember);
                    break;
            }
        }
    }

    private static IReadOnlyDictionary<string, string> StringConstants(CompilationUnitSyntax root)
    {
        return root.DescendantNodes()
            .OfType<FieldDeclarationSyntax>()
            .Where(field => HasModifier(field.Modifiers, SyntaxKind.ConstKeyword))
            .Where(field => Normalize(field.Declaration.Type.ToString()) is "string" or "System.String")
            .SelectMany(field => field.Declaration.Variables)
            .Where(variable => variable.Initializer?.Value is LiteralExpressionSyntax literal &&
                               literal.IsKind(SyntaxKind.StringLiteralExpression))
            .GroupBy(variable => variable.Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => ((LiteralExpressionSyntax)group.Last().Initializer!.Value).Token.ValueText,
                StringComparer.Ordinal);
    }

    private static IEnumerable<HarmonyDeclaration> ParseHarmonyPatches(
        IndexedFileRecord file,
        TypeDeclarationSyntax type,
        IReadOnlyDictionary<string, string> constants)
    {
        var patchClass = QualifiedTypeName(type);
        var classTargets = type.AttributeLists
            .SelectMany(list => list.Attributes)
            .Where(attribute => AttributeName(attribute) == "HarmonyPatch")
            .Select(attribute => ParseHarmonyTarget(attribute, constants))
            .ToArray();
        var effectiveClassTargets = classTargets.Length == 0
            ? [HarmonyTarget.Empty]
            : classTargets;

        foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
        {
            var attributes = method.AttributeLists.SelectMany(list => list.Attributes).ToArray();
            var markerKinds = attributes
                .Select(HarmonyPatchKind)
                .Where(kind => kind is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var methodTargets = attributes
                .Where(attribute => AttributeName(attribute) == "HarmonyPatch")
                .Select(attribute => ParseHarmonyTarget(attribute, constants))
                .ToArray();
            var providerKinds = attributes
                .Select(HarmonyTargetKind)
                .Where(kind => kind is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (providerKinds.Length > 0)
            {
                foreach (var providerKind in providerKinds)
                {
                    foreach (var providerTarget in ProviderTargets(method, constants))
                    {
                        yield return CreateHarmonyDeclaration(file, patchClass, method, providerKind, providerTarget);
                    }
                }
            }

            if (markerKinds.Length == 0)
            {
                var inferred = InferPatchKind(method.Identifier.ValueText);
                if (inferred is not null && (classTargets.Length > 0 || methodTargets.Length > 0))
                {
                    markerKinds = [inferred];
                }
            }

            if (markerKinds.Length == 0)
            {
                continue;
            }

            var targets = methodTargets.Length == 0
                ? effectiveClassTargets
                : CombineHarmonyTargets(effectiveClassTargets, methodTargets);
            foreach (var patchKind in markerKinds)
            {
                foreach (var target in targets)
                {
                    yield return CreateHarmonyDeclaration(
                        file,
                        patchClass,
                        method,
                        patchKind,
                        target);
                }
            }
        }
    }

    private static IEnumerable<HarmonyDeclaration> ParseHarmonyCalls(
        IndexedFileRecord file,
        CompilationUnitSyntax root,
        IReadOnlyDictionary<string, string> constants)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                member.Name.Identifier.ValueText != "Patch")
            {
                continue;
            }

            var arguments = invocation.ArgumentList.Arguments;
            if (arguments.Count == 0)
            {
                continue;
            }

            var original = arguments[0].Expression;
            var target = original is InvocationExpressionSyntax provider &&
                         IsHarmonyProviderInvocation(provider)
                ? ParseProviderExpression(provider, constants)
                : HarmonyTarget.Empty with
                {
                    RawText = Normalize(original.ToString()),
                    Confidence = "heuristic"
                };
            for (var index = 1; index < arguments.Count; index++)
            {
                var argument = arguments[index];
                var kind = argument.NameColon?.Name.Identifier.ValueText.ToLowerInvariant() ??
                           PositionalPatchKind(index - 1);
                if (kind is null or not ("prefix" or "postfix" or "transpiler" or "finalizer"))
                {
                    continue;
                }

                var patchMethod = ParseHarmonyMethod(argument.Expression, constants);
                if (patchMethod is null)
                {
                    continue;
                }

                yield return new HarmonyDeclaration(
                    file.WorkspaceIdentity,
                    file.Id,
                    file.Path,
                    LineNumber(invocation),
                    patchMethod.PatchClass,
                    patchMethod.PatchClass + "." + patchMethod.Method,
                    patchMethod.Signature,
                    kind,
                    target.TargetType,
                    target.TargetMember,
                    target.TargetSignature,
                    target.RawText,
                    target.Confidence);
            }
        }
    }

    private static string? PositionalPatchKind(int index) => index switch
    {
        0 => "prefix",
        1 => "postfix",
        2 => "transpiler",
        3 => "finalizer",
        _ => null
    };

    private static HarmonyMethodInfo? ParseHarmonyMethod(
        ExpressionSyntax expression,
        IReadOnlyDictionary<string, string> constants)
    {
        if (expression is not ObjectCreationExpressionSyntax creation ||
            !Normalize(creation.Type.ToString()).EndsWith("HarmonyMethod", StringComparison.Ordinal))
        {
            return null;
        }

        var arguments = creation.ArgumentList?.Arguments
            .Select(argument => argument.Expression)
            .ToArray() ?? [];
        if (arguments.Length == 0)
        {
            return null;
        }

        var typeIndex = Array.FindIndex(arguments, item => item is TypeOfExpressionSyntax);
        if (typeIndex < 0 || arguments[typeIndex] is not TypeOfExpressionSyntax typeOf)
        {
            return null;
        }

        var methodName = arguments
            .Skip(typeIndex + 1)
            .Select(argument => StaticText(argument, constants))
            .FirstOrDefault(value => value is not null);
        if (methodName is null)
        {
            return null;
        }

        var signature = new List<string>();
        foreach (var argument in arguments.Skip(typeIndex + 1))
        {
            CollectTypeNames(argument, signature);
        }

        return new HarmonyMethodInfo(
            Normalize(typeOf.Type.ToString()),
            methodName,
            "(" + string.Join(",", signature) + ")");
    }

    private static IReadOnlyList<HarmonyTarget> CombineHarmonyTargets(
        IReadOnlyList<HarmonyTarget> classTargets,
        IReadOnlyList<HarmonyTarget> methodTargets)
    {
        return methodTargets
            .SelectMany(methodTarget => classTargets.Select(classTarget => MergeHarmonyTarget(classTarget, methodTarget)))
            .Distinct()
            .ToArray();
    }

    private static HarmonyTarget MergeHarmonyTarget(HarmonyTarget first, HarmonyTarget second) =>
        new(
            second.TargetType ?? first.TargetType,
            second.TargetMember ?? first.TargetMember,
            second.TargetSignature.Count == 0 ? first.TargetSignature : second.TargetSignature,
            string.Join(",", new[] { first.RawText, second.RawText }.Where(item => item is not null)),
            first.Confidence == "unresolved" || second.Confidence == "unresolved"
                ? "heuristic"
                : first.Confidence == "heuristic" || second.Confidence == "heuristic"
                    ? "heuristic"
                    : "syntax");

    private static HarmonyDeclaration CreateHarmonyDeclaration(
        IndexedFileRecord file,
        string patchClass,
        MethodDeclarationSyntax method,
        string patchKind,
        HarmonyTarget target) =>
        new(
            file.WorkspaceIdentity,
            file.Id,
            file.Path,
            LineNumber(method),
            patchClass,
            patchClass + "." + method.Identifier.ValueText,
            "(" + string.Join(",", Parameters(method.ParameterList.Parameters)) + ")",
            patchKind,
            target.TargetType,
            target.TargetMember,
            target.TargetSignature,
            target.RawText,
            target.Confidence);

    private static IReadOnlyList<HarmonyTarget> ProviderTargets(
        MethodDeclarationSyntax method,
        IReadOnlyDictionary<string, string> constants)
    {
        var expressions = new List<ExpressionSyntax>();
        if (method.ExpressionBody?.Expression is { } expressionBody)
        {
            expressions.Add(expressionBody);
        }

        if (method.Body is not null)
        {
            expressions.AddRange(method.Body.DescendantNodes()
                .OfType<ReturnStatementSyntax>()
                .Select(item => item.Expression)
                .Where(item => item is not null)
                .Cast<ExpressionSyntax>());
            expressions.AddRange(method.Body.DescendantNodes()
                .OfType<YieldStatementSyntax>()
                .Select(item => item.Expression)
                .Where(item => item is not null)
                .Cast<ExpressionSyntax>());
        }

        if (expressions.Count == 0)
        {
            return [HarmonyTarget.Empty with { Confidence = "unresolved" }];
        }

        var targets = expressions
            .SelectMany(expression => expression.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Where(IsHarmonyProviderInvocation))
            .OrderBy(item => item.SpanStart)
            .Select(item => ParseProviderExpression(item, constants))
            .ToArray();
        return targets.Length == 0
            ? [ParseProviderExpression(expressions[0], constants)]
            : targets;
    }

    private static bool IsHarmonyProviderInvocation(InvocationExpressionSyntax invocation)
    {
        var methodName = invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => string.Empty
        };
        return methodName is
            "Method" or "DeclaredMethod" or "MethodByName" or "MethodDelegate" or
            "Constructor" or "DeclaredConstructor" or "PropertyGetter" or "PropertySetter" or
            "TypeByName" or "GetMethod" or "GetRuntimeMethod";
    }

    private static HarmonyTarget ParseProviderExpression(
        ExpressionSyntax expression,
        IReadOnlyDictionary<string, string> constants)
    {
        if (expression is InvocationExpressionSyntax invocation)
        {
            var methodName = invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                SimpleNameSyntax simple => simple.Identifier.ValueText,
                _ => string.Empty
            };
            var arguments = invocation.ArgumentList.Arguments
                .Select(argument => argument.Expression)
                .ToArray();
            if (methodName is "Method" or "DeclaredMethod" or "MethodByName" or "MethodDelegate")
            {
                return ParseHarmonyTargetExpressions(arguments, constants);
            }

            if (methodName is "Constructor" or "DeclaredConstructor")
            {
                var target = ParseHarmonyTargetExpressions(arguments, constants);
                return target with { TargetMember = ".ctor" };
            }

            if (methodName is "PropertyGetter" or "PropertySetter")
            {
                var target = ParseHarmonyTargetExpressions(arguments, constants);
                var property = target.TargetMember;
                var prefix = methodName == "PropertyGetter" ? "get_" : "set_";
                return target with
                {
                    TargetMember = property is null ? null : prefix + property
                };
            }

            if (methodName == "TypeByName")
            {
                var text = arguments.FirstOrDefault() is { } typeArgument
                    ? StaticText(typeArgument, constants)
                    : null;
                return new HarmonyTarget(text, null, [], text, text is null ? "heuristic" : "syntax");
            }

            if (methodName is "GetMethod" or "GetRuntimeMethod" &&
                invocation.Expression is MemberAccessExpressionSyntax getMethod &&
                getMethod.Expression is TypeOfExpressionSyntax typeOf)
            {
                var target = ParseHarmonyTargetExpressions(arguments, constants);
                return target with
                {
                    TargetType = Normalize(typeOf.Type.ToString()),
                    TargetMember = target.TargetMember
                };
            }
        }

        return new HarmonyTarget(
            null,
            Normalize(expression.ToString()),
            [],
            Normalize(expression.ToString()),
            "heuristic");
    }

    private static HarmonyTarget ParseHarmonyTarget(
        AttributeSyntax attribute,
        IReadOnlyDictionary<string, string> constants)
    {
        var arguments = attribute.ArgumentList?.Arguments
            .Select(argument => argument.Expression)
            .ToArray() ?? [];
        return ParseHarmonyTargetExpressions(arguments, constants);
    }

    private static HarmonyTarget ParseHarmonyTargetExpressions(
        IReadOnlyList<ExpressionSyntax> arguments,
        IReadOnlyDictionary<string, string> constants)
    {
        if (arguments.Count == 0)
        {
            return HarmonyTarget.Empty with { Confidence = "unresolved" };
        }

        var targetType = (string?)null;
        var targetTypeIndex = -1;
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] is TypeOfExpressionSyntax typeOf)
            {
                targetType = Normalize(typeOf.Type.ToString());
                targetTypeIndex = index;
                break;
            }
        }

        var staticTexts = arguments
            .Select(argument => StaticText(argument, constants))
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();
        var enumValues = arguments
            .Select(MethodTypeValue)
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();
        var targetMember = targetType is not null
            ? staticTexts.FirstOrDefault()
            : staticTexts.Length > 1
                ? staticTexts[1]
                : staticTexts.FirstOrDefault();
        if (targetType is null && staticTexts.Length > 1)
        {
            targetType = staticTexts[0];
        }

        var signature = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            if (index == targetTypeIndex)
            {
                continue;
            }

            CollectTypeNames(arguments[index], signature);
        }

        var methodType = enumValues.FirstOrDefault();
        targetMember = methodType switch
        {
            "Constructor" => ".ctor",
            "StaticConstructor" => ".cctor",
            "Getter" when targetMember is not null && !targetMember.StartsWith("get_", StringComparison.Ordinal)
                => "get_" + targetMember,
            "Setter" when targetMember is not null && !targetMember.StartsWith("set_", StringComparison.Ordinal)
                => "set_" + targetMember,
            _ => targetMember
        };

        var rawText = string.Join(",", arguments.Select(argument => Normalize(argument.ToString())));
        var confidence = arguments.All(argument =>
                StaticText(argument, constants) is not null ||
                argument is TypeOfExpressionSyntax ||
                MethodTypeValue(argument) is not null ||
                ContainsOnlyTypeOf(argument))
            ? "syntax"
            : "heuristic";
        return new HarmonyTarget(targetType, targetMember, signature, rawText, confidence);
    }

    private static string? StaticText(
        ExpressionSyntax expression,
        IReadOnlyDictionary<string, string> constants)
    {
        if (expression is LiteralExpressionSyntax literal &&
            literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return literal.Token.ValueText;
        }

        if (expression is IdentifierNameSyntax identifier &&
            constants.TryGetValue(identifier.Identifier.ValueText, out var value))
        {
            return value;
        }

        if (expression is InvocationExpressionSyntax invocation &&
            invocation.Expression is IdentifierNameSyntax name &&
            name.Identifier.ValueText == "nameof" &&
            invocation.ArgumentList.Arguments.Count == 1)
        {
            return LastName(invocation.ArgumentList.Arguments[0].Expression);
        }

        return null;
    }

    private static string? MethodTypeValue(ExpressionSyntax expression)
    {
        var text = Normalize(expression.ToString());
        foreach (var value in new[] { "Constructor", "StaticConstructor", "Getter", "Setter", "Normal" })
        {
            if (text.EndsWith("." + value, StringComparison.Ordinal) ||
                string.Equals(text, value, StringComparison.Ordinal))
            {
                return value;
            }
        }

        return null;
    }

    private static void CollectTypeNames(ExpressionSyntax expression, ICollection<string> values)
    {
        switch (expression)
        {
            case TypeOfExpressionSyntax typeOf:
                values.Add(Normalize(typeOf.Type.ToString()));
                break;
            case ArrayCreationExpressionSyntax array when array.Initializer is not null:
                foreach (var item in array.Initializer.Expressions)
                {
                    CollectTypeNames(item, values);
                }

                break;
            case ImplicitArrayCreationExpressionSyntax implicitArray:
                foreach (var item in implicitArray.Initializer.Expressions)
                {
                    CollectTypeNames(item, values);
                }

                break;
        }
    }

    private static bool ContainsOnlyTypeOf(ExpressionSyntax expression) =>
        expression is TypeOfExpressionSyntax ||
        expression is ArrayCreationExpressionSyntax array &&
        array.Initializer is not null &&
        array.Initializer.Expressions.All(ContainsOnlyTypeOf) ||
        expression is ImplicitArrayCreationExpressionSyntax implicitArray &&
        implicitArray.Initializer.Expressions.All(ContainsOnlyTypeOf);

    private static string? HarmonyPatchKind(AttributeSyntax attribute)
    {
        var name = AttributeName(attribute);
        return name switch
        {
            "HarmonyPrefix" => "prefix",
            "HarmonyPostfix" => "postfix",
            "HarmonyTranspiler" => "transpiler",
            "HarmonyFinalizer" => "finalizer",
            "HarmonyPrepare" => "prepare",
            "HarmonyCleanup" => "cleanup",
            _ => null
        };
    }

    private static string? HarmonyTargetKind(AttributeSyntax attribute)
    {
        var name = AttributeName(attribute);
        return name switch
        {
            "HarmonyTargetMethod" => "target_method",
            "HarmonyTargetMethods" => "target_methods",
            _ => null
        };
    }

    private static string? InferPatchKind(string methodName) =>
        methodName.ToLowerInvariant() switch
        {
            "prefix" => "prefix",
            "postfix" => "postfix",
            "transpiler" => "transpiler",
            "finalizer" => "finalizer",
            "prepare" => "prepare",
            "cleanup" => "cleanup",
            _ => null
        };

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = Normalize(attribute.Name.ToString());
        var separator = name.LastIndexOf('.');
        if (separator >= 0)
        {
            name = name[(separator + 1)..];
        }

        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^9]
            : name;
    }

    private static string LastName(ExpressionSyntax expression) => expression switch
    {
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => Normalize(expression.ToString()).Split('.').Last()
    };

    private static string QualifiedTypeName(BaseTypeDeclarationSyntax type)
    {
        var namespaceName = NamespaceName(type);
        var containing = type.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .Reverse()
            .Select(TypeName)
            .ToArray();
        return string.Join(
            ".",
            new[] { namespaceName }
                .Where(item => item.Length > 0)
                .Concat(containing)
                .Append(TypeName(type)));
    }

    private static MemberDeclaration CreateMember(
        IndexedFileRecord file,
        string typeIdentity,
        string kind,
        string name,
        string signature,
        string? returnType,
        IReadOnlyList<string> parameters,
        SyntaxTokenList modifiers,
        SyntaxList<AttributeListSyntax> attributeLists,
        SyntaxNode syntax,
        bool interfaceMember)
    {
        var typeUsages = syntax.DescendantNodes()
            .OfType<TypeSyntax>()
            .Select(item => Normalize(item.ToString()))
            .Where(IsUsefulType)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var receiverTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["this"] = DisplayTypeName(typeIdentity)
        };
        foreach (var declaration in syntax.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            var declaredType = Normalize(declaration.Declaration.Type.ToString());
            foreach (var variable in declaration.Declaration.Variables)
            {
                var inferredType = declaredType;
                if (declaredType == "var" &&
                    variable.Initializer?.Value is ObjectCreationExpressionSyntax creation)
                {
                    inferredType = Normalize(creation.Type.ToString());
                }

                if (IsUsefulType(inferredType))
                {
                    receiverTypes[variable.Identifier.ValueText] = inferredType;
                }
            }
        }

        var memberUsages = syntax.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Select((item, index) =>
            {
                var nameSyntax = item.Name as SimpleNameSyntax;
                var invocation = item.Parent as InvocationExpressionSyntax;
                var receiver = item.Expression.ToString();
                if (receiverTypes.TryGetValue(receiver, out var knownType))
                {
                    receiver = knownType;
                }
                else if (item.Expression is ObjectCreationExpressionSyntax creation)
                {
                    receiver = Normalize(creation.Type.ToString());
                }

                return new MemberUsage(
                    receiver,
                    nameSyntax?.Identifier.ValueText ?? item.Name.ToString(),
                    invocation?.ArgumentList.Arguments.Count,
                    "access:" + index);
            })
            .ToArray();
        return new MemberDeclaration(
            file.WorkspaceIdentity,
            file.Id,
            file.Path,
            LineNumber(syntax),
            typeIdentity,
            kind,
            name,
            signature,
            Normalize(returnType ?? string.Empty),
            parameters,
            Visibility(modifiers, false, false, interfaceMember),
            HasModifier(modifiers, SyntaxKind.StaticKeyword) ||
            HasModifier(modifiers, SyntaxKind.ConstKeyword),
            HasModifier(modifiers, SyntaxKind.AbstractKeyword),
            Attributes(attributeLists),
            typeUsages,
            memberUsages);
    }

    private static TypeModel AggregateType(string identity, IEnumerable<TypeDeclaration> declarations)
    {
        var items = declarations
            .OrderBy(item => item.FilePath, StringComparer.Ordinal)
            .ThenBy(item => item.Line ?? int.MaxValue)
            .ThenBy(item => item.FileId, StringComparer.Ordinal)
            .ToArray();
        var first = items[0];
        return new TypeModel(
            CreateTypeId(identity),
            identity,
            first.Scope,
            first.Namespace,
            first.Name,
            first.QualifiedName,
            first.Arity,
            first.TypeKind,
            first.ContainingType,
            items.Select(item => item.BaseType).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
            items.SelectMany(item => item.Interfaces)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
            items.Select(item => item.Visibility).FirstOrDefault(item => item.Length > 0) ?? "internal",
            items.Any(item => item.IsStatic),
            items.Any(item => item.IsAbstract),
            items.Any(item => item.IsSealed),
            items.Any(item => item.IsPartial) || items.Length > 1,
            items.SelectMany(item => item.Attributes)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
            items);
    }

    private static MemberModel AggregateMember(
        string identity,
        IEnumerable<MemberDeclaration> declarations,
        TypeModel type)
    {
        var items = declarations
            .OrderBy(item => item.FilePath, StringComparer.Ordinal)
            .ThenBy(item => item.Line ?? int.MaxValue)
            .ThenBy(item => item.FileId, StringComparer.Ordinal)
            .ToArray();
        var first = items[0];
        return new MemberModel(
            StableEntityId.Create(
                MemberKind,
                type.Id,
                "semantic:" + StableEntityId.DigestBase32(identity)),
            identity,
            type.Id,
            type.Identity,
            first.Scope,
            first.Kind,
            first.Name,
            first.Signature,
            first.ReturnType,
            first.Parameters,
            items.Select(item => item.Visibility).FirstOrDefault(item => item.Length > 0) ?? "private",
            items.Any(item => item.IsStatic),
            items.Any(item => item.IsAbstract),
            items.SelectMany(item => item.Attributes)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
            items);
    }

    private static EntityRecord CreateTypeEntity(TypeModel type)
    {
        var first = type.Declarations[0];
        return new EntityRecord(
            type.Id,
            TypeKind,
            type.Identity,
            first.FileId,
            first.Line,
            JsonOutput.SerializePayload(new
            {
                scope = type.Scope,
                typeKind = type.TypeKind,
                name = type.Name,
                qualifiedName = type.QualifiedName,
                @namespace = type.Namespace,
                arity = type.Arity,
                containingType = type.ContainingType,
                baseType = type.BaseType,
                interfaces = type.Interfaces,
                accessibility = type.Visibility,
                isStatic = type.IsStatic,
                isAbstract = type.IsAbstract,
                isSealed = type.IsSealed,
                isPartial = type.IsPartial,
                attributes = type.Attributes,
                file = first.FilePath,
                line = first.Line,
                declarations = type.Declarations.Select(ToPayload).ToArray()
            }));
    }

    private static EntityRecord CreateMemberEntity(MemberModel member)
    {
        var first = member.Declarations[0];
        return new EntityRecord(
            member.Id,
            MemberKind,
            member.Identity,
            first.FileId,
            first.Line,
            JsonOutput.SerializePayload(new
            {
                scope = member.Scope,
                memberKind = member.Kind,
                name = member.Name,
                signature = member.Signature,
                qualifiedName = DisplayTypeName(member.TypeIdentity) + "." + member.Name,
                containingType = DisplayTypeName(member.TypeIdentity),
                containingTypeIdentity = member.TypeIdentity,
                containingTypeId = member.TypeId,
                returnType = member.ReturnType,
                parameters = member.Parameters,
                accessibility = member.Visibility,
                isStatic = member.IsStatic,
                isAbstract = member.IsAbstract,
                attributes = member.Attributes,
                file = first.FilePath,
                line = first.Line,
                declarations = member.Declarations.Select(ToPayload).ToArray()
            }));
    }

    private static EntityRecord CreateHarmonyEntity(
        HarmonyDeclaration patch,
        IReadOnlyList<TypeModel> types,
        IReadOnlyDictionary<string, MemberModel[]> membersByType,
        HarmonyAssemblyResolver assemblyResolver)
    {
        var target = ResolveHarmonyTarget(patch, types, membersByType, assemblyResolver);
        var identity = string.Join(
            "|",
            patch.FilePath,
            patch.PatchClass,
            patch.PatchMethod,
            patch.PatchSignature,
            patch.PatchKind,
            patch.TargetType ?? string.Empty,
            patch.TargetMember ?? string.Empty,
            string.Join(",", patch.TargetSignature));
        var id = StableEntityId.Create(
            HarmonyKind,
            patch.Scope,
            "semantic:" + StableEntityId.DigestBase32(identity));
        return new EntityRecord(
            id,
            HarmonyKind,
            identity,
            patch.FileId,
            patch.Line,
            JsonOutput.SerializePayload(new
            {
                scope = patch.Scope,
                patchClass = patch.PatchClass,
                patchMethod = patch.PatchMethod,
                patchSignature = patch.PatchSignature,
                patchKind = patch.PatchKind,
                targetType = patch.TargetType,
                targetMember = patch.TargetMember,
                targetSignature = patch.TargetSignature,
                target = TargetDisplay(patch.TargetType, patch.TargetMember),
                rawTarget = patch.RawText,
                targetId = target.TargetId,
                targetMemberId = target.TargetMemberId,
                file = patch.FilePath,
                line = patch.Line,
                resolutionState = target.ResolutionState,
                confidence = patch.Confidence,
                resolved = target.ResolutionState == "resolved"
            }));
    }

    private static IReadOnlyList<RelationRecord> BuildHarmonyRelations(
        IReadOnlyList<HarmonyDeclaration> patches,
        IReadOnlyList<TypeModel> types,
        IReadOnlyDictionary<string, MemberModel[]> membersByType,
        HarmonyAssemblyResolver assemblyResolver)
    {
        var relations = new List<RelationRecord>();
        foreach (var patch in patches)
        {
            var target = ResolveHarmonyTarget(patch, types, membersByType, assemblyResolver);
            var identity = string.Join(
                "|",
                patch.FilePath,
                patch.PatchClass,
                patch.PatchMethod,
                patch.PatchSignature,
                patch.PatchKind,
                patch.TargetType ?? string.Empty,
                patch.TargetMember ?? string.Empty,
                string.Join(",", patch.TargetSignature));
            var patchId = StableEntityId.Create(
                HarmonyKind,
                patch.Scope,
                "semantic:" + StableEntityId.DigestBase32(identity));
            var targetText = TargetDisplay(patch.TargetType, patch.TargetMember) ??
                             patch.RawText ??
                             string.Empty;
            if (target.TargetId is not null)
            {
                relations.Add(CreateHarmonyRelation(
                    patchId,
                    target.TargetId,
                    "harmony_target",
                    targetText,
                    patch));
            }

            if (target.TargetMemberId is not null)
            {
                relations.Add(CreateHarmonyRelation(
                    patchId,
                    target.TargetMemberId,
                    "harmony_target_member",
                    targetText,
                    patch));
            }
        }

        return relations
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static RelationRecord CreateHarmonyRelation(
        string patchId,
        string targetId,
        string kind,
        string target,
        HarmonyDeclaration patch)
    {
        var relationId = StableEntityId.Create(
            "relation",
            patchId,
            kind + "|" + patch.FilePath + "|" + patch.Line + "|" + target);
        return new RelationRecord(
            relationId,
            patchId,
            targetId,
            kind,
            patch.FileId,
            patch.Line,
            JsonOutput.SerializePayload(new
            {
                target,
                targetId,
                confidence = patch.Confidence
            }));
    }

    private static HarmonyResolution ResolveHarmonyTarget(
        HarmonyDeclaration patch,
        IReadOnlyList<TypeModel> types,
        IReadOnlyDictionary<string, MemberModel[]> membersByType,
        HarmonyAssemblyResolver assemblyResolver)
    {
        if (patch.TargetType is null)
        {
            return HarmonyResolution.Unresolved;
        }

        var byKey = types.ToDictionary(item => item.Identity, StringComparer.Ordinal);
        var targetType = ResolveType(
            byKey,
            NamespaceOfQualifiedName(patch.PatchClass),
            patch.TargetType);
        if (targetType is null)
        {
            return assemblyResolver.TryResolve(
                patch.TargetType,
                patch.TargetMember,
                patch.TargetSignature)
                ? new HarmonyResolution(null, null, "resolved")
                : new HarmonyResolution(null, null, "unresolved");
        }

        if (patch.TargetMember is null)
        {
            return new HarmonyResolution(targetType.Id, null, "resolved");
        }

        if (!membersByType.TryGetValue(targetType.Id, out var members))
        {
            return new HarmonyResolution(targetType.Id, null, "unresolved");
        }

        var candidates = members
            .Where(member => HarmonyMemberName(member.Name, patch.TargetMember))
            .Where(member => patch.TargetSignature.Count == 0 ||
                             SignatureMatches(member.Parameters, patch.TargetSignature))
            .ToArray();
        if (candidates.Length == 1)
        {
            return new HarmonyResolution(targetType.Id, candidates[0].Id, "resolved");
        }

        return assemblyResolver.TryResolve(
            patch.TargetType,
            patch.TargetMember,
            patch.TargetSignature)
            ? new HarmonyResolution(targetType.Id, null, "resolved")
            : new HarmonyResolution(targetType.Id, null, "unresolved");
    }

    private static bool HarmonyMemberName(string name, string target) =>
        string.Equals(name, target, StringComparison.Ordinal) ||
        target is ".ctor" && name == ".ctor" ||
        target is ".cctor" && name == ".cctor";

    private static bool SignatureMatches(
        IReadOnlyList<string> parameters,
        IReadOnlyList<string> targetSignature) =>
        parameters.Count == targetSignature.Count &&
        parameters.Zip(targetSignature, (actual, expected) =>
                Normalize(actual).TrimEnd('?') == Normalize(expected).TrimEnd('?'))
            .All(item => item);

    private static string? TargetDisplay(string? targetType, string? targetMember) =>
        targetType is null
            ? targetMember
            : targetMember is null
                ? targetType
                : targetType + "." + targetMember;

    private static string NamespaceOfQualifiedName(string qualifiedName)
    {
        var lastDot = qualifiedName.LastIndexOf('.');
        return lastDot < 0 ? string.Empty : qualifiedName[..lastDot];
    }

    private static IReadOnlyList<RelationRecord> BuildRelations(
        IReadOnlyList<TypeModel> types,
        IReadOnlyList<MemberModel> members,
        IReadOnlyDictionary<string, TypeModel> byKey,
        IReadOnlyDictionary<string, MemberModel[]> membersByType)
    {
        var relations = new List<RelationRecord>();
        foreach (var type in types)
        {
            foreach (var declaration in type.Declarations)
            {
                if (declaration.BaseType is not null)
                {
                    AddRelation(
                        relations,
                        type.Id,
                        ResolveType(byKey, declaration.Namespace, declaration.BaseType)?.Id,
                        "csharp_inheritance",
                        declaration.BaseType,
                        declaration);
                }

                foreach (var interfaceName in declaration.Interfaces)
                {
                    AddRelation(
                        relations,
                        type.Id,
                        ResolveType(byKey, declaration.Namespace, interfaceName)?.Id,
                        "csharp_interface_implementation",
                        interfaceName,
                        declaration);
                }

                foreach (var attribute in declaration.Attributes)
                {
                    AddRelation(
                        relations,
                        type.Id,
                        ResolveType(byKey, declaration.Namespace, attribute, true)?.Id,
                        "csharp_attribute_usage",
                        attribute,
                        declaration);
                }
            }
        }

        foreach (var member in members)
        {
            var memberNamespace = byKey.TryGetValue(member.TypeIdentity, out var containingType)
                ? containingType.Namespace
                : NamespaceOf(member.TypeIdentity);
            foreach (var declaration in member.Declarations)
            {
                foreach (var typeUsage in declaration.TypeUsages)
                {
                    var target = ResolveType(byKey, memberNamespace, typeUsage);
                    if (target is not null)
                    {
                        AddRelation(relations, member.Id, target.Id, "csharp_type_usage", typeUsage, declaration);
                    }
                }

                foreach (var attribute in declaration.Attributes)
                {
                    var target = ResolveType(byKey, memberNamespace, attribute, true);
                    if (target is not null)
                    {
                        AddRelation(relations, member.Id, target.Id, "csharp_attribute_usage", attribute, declaration);
                    }
                }

                foreach (var usage in declaration.MemberUsages)
                {
                    var receiver = ResolveType(byKey, memberNamespace, usage.Receiver);
                    if (receiver is null || !membersByType.TryGetValue(receiver.Id, out var candidates))
                    {
                        continue;
                    }

                    var matches = candidates
                        .Where(candidate => candidate.Name == usage.Name)
                        .Where(candidate => usage.ArgumentCount is null ||
                                            candidate.Parameters.Count == usage.ArgumentCount.Value)
                        .ToArray();
                    if (matches.Length == 1)
                    {
                        AddRelation(relations, member.Id, matches[0].Id, "csharp_member_usage", usage.Name, declaration);
                    }
                }
            }
        }

        return relations
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddRelation(
        ICollection<RelationRecord> relations,
        string fromId,
        string? toId,
        string kind,
        string target,
        TypeDeclaration declaration)
    {
        var locator = declaration.FilePath + "|" + declaration.Line + "|" + target;
        relations.Add(new RelationRecord(
            StableEntityId.Create("relation", fromId, kind + "|" + locator),
            fromId,
            toId,
            kind,
            declaration.FileId,
            declaration.Line,
            JsonOutput.SerializePayload(new
            {
                target,
                targetId = toId,
                confidence = toId is null ? "unresolved" : "syntax"
            })));
    }

    private static void AddRelation(
        ICollection<RelationRecord> relations,
        string fromId,
        string toId,
        string kind,
        string target,
        MemberDeclaration declaration)
    {
        var locator = declaration.FilePath + "|" + declaration.Line + "|" + target;
        relations.Add(new RelationRecord(
            StableEntityId.Create("relation", fromId, kind + "|" + locator),
            fromId,
            toId,
            kind,
            declaration.FileId,
            declaration.Line,
            JsonOutput.SerializePayload(new
            {
                target,
                targetId = toId,
                confidence = "syntax"
            })));
    }

    private static TypeModel? ResolveType(
        IReadOnlyDictionary<string, TypeModel> byKey,
        string namespaceName,
        string text,
        bool attributeFallback = false)
    {
        var target = Normalize(text).TrimEnd('?');
        while (target.EndsWith("[]", StringComparison.Ordinal))
        {
            target = target[..^2];
        }

        var generic = target.IndexOf('<');
        if (generic >= 0)
        {
            target = target[..generic];
        }

        if (target.StartsWith("global::", StringComparison.Ordinal))
        {
            target = target[8..];
        }

        var direct = byKey.Values.Where(item => item.QualifiedName == target).ToArray();
        if (direct.Length == 1)
        {
            return direct[0];
        }

        var local = byKey.Values
            .Where(item => item.Namespace == namespaceName && item.Name == target)
            .ToArray();
        if (local.Length == 1)
        {
            return local[0];
        }

        var simple = byKey.Values
            .Where(item => item.Name == target ||
                           item.QualifiedName.EndsWith("." + target, StringComparison.Ordinal))
            .ToArray();
        if (simple.Length == 1)
        {
            return simple[0];
        }

        return attributeFallback && !target.EndsWith("Attribute", StringComparison.Ordinal)
            ? ResolveType(byKey, namespaceName, target + "Attribute")
            : null;
    }

    private static IReadOnlyList<HarmonyDeclaration> ParseStoredHarmonyPatches(EntityRecord entity)
    {
        var result = new List<HarmonyDeclaration>();
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(entity.PayloadJson);
            var root = document.RootElement;
            var fileId = entity.FileId ?? StringValue(root, "fileId");
            var file = StringValue(root, "file");
            var patchClass = StringValue(root, "patchClass");
            var patchMethod = StringValue(root, "patchMethod");
            var patchKind = StringValue(root, "patchKind");
            if (fileId is null || file is null || patchClass is null ||
                patchMethod is null || patchKind is null)
            {
                return result;
            }

            result.Add(new HarmonyDeclaration(
                StringValue(root, "scope") ?? ScopeFromIdentity(entity.IdentityKey),
                fileId,
                file,
                entity.Line ?? IntegerValue(root, "line"),
                patchClass,
                patchMethod,
                StringValue(root, "patchSignature") ?? "()",
                patchKind,
                StringValue(root, "targetType"),
                StringValue(root, "targetMember"),
                StringValues(root, "targetSignature"),
                StringValue(root, "rawTarget") ?? StringValue(root, "target"),
                StringValue(root, "confidence") ?? "heuristic"));
        }
        catch (JsonException)
        {
        }
        finally
        {
            document?.Dispose();
        }

        return result;
    }

    private static IReadOnlyList<TypeDeclaration> ParseStoredTypes(EntityRecord entity)
    {
        var result = new List<TypeDeclaration>();
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(entity.PayloadJson);
            var root = document.RootElement;
            if (root.TryGetProperty("declarations", out var declarations) &&
                declarations.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in declarations.EnumerateArray())
                {
                    var parsed = ParseStoredType(item, root, entity);
                    if (parsed is not null)
                    {
                        result.Add(parsed);
                    }
                }
            }
            else
            {
                var fallback = ParseStoredType(root, root, entity);
                if (fallback is not null)
                {
                    result.Add(fallback);
                }
            }
        }
        catch (JsonException)
        {
        }
        finally
        {
            document?.Dispose();
        }

        return result;
    }

    private static TypeDeclaration? ParseStoredType(
        JsonElement item,
        JsonElement parent,
        EntityRecord entity)
    {
        var fileId = StringValue(item, "fileId") ?? entity.FileId;
        var file = StringValue(item, "file") ?? StringValue(parent, "file");
        var qualified = StringValue(item, "qualifiedName") ?? StringValue(parent, "qualifiedName");
        var name = StringValue(item, "name") ?? StringValue(parent, "name");
        if (fileId is null || file is null || qualified is null || name is null)
        {
            return null;
        }

        var itemInterfaces = StringValues(item, "interfaces");
        var parentInterfaces = StringValues(parent, "interfaces");
        var itemAttributes = StringValues(item, "attributes");
        var parentAttributes = StringValues(parent, "attributes");
        return new TypeDeclaration(
            StringValue(item, "scope") ?? StringValue(parent, "scope") ?? ScopeFromIdentity(entity.IdentityKey),
            fileId,
            file,
            IntegerValue(item, "line") ?? IntegerValue(parent, "line") ?? entity.Line,
            StringValue(item, "namespace") ?? StringValue(parent, "namespace") ?? string.Empty,
            name,
            qualified,
            IntegerValue(item, "arity") ?? IntegerValue(parent, "arity") ?? 0,
            StringValue(item, "typeKind") ?? StringValue(parent, "typeKind") ?? "class",
            StringValue(item, "containingType") ?? StringValue(parent, "containingType"),
            StringValue(item, "baseType") ?? StringValue(parent, "baseType"),
            itemInterfaces.Count == 0 ? parentInterfaces : itemInterfaces,
            StringValue(item, "accessibility") ?? StringValue(parent, "accessibility") ?? "internal",
            BooleanValue(item, "isStatic") ?? BooleanValue(parent, "isStatic") ?? false,
            BooleanValue(item, "isAbstract") ?? BooleanValue(parent, "isAbstract") ?? false,
            BooleanValue(item, "isSealed") ?? BooleanValue(parent, "isSealed") ?? false,
            BooleanValue(item, "isPartial") ?? BooleanValue(parent, "isPartial") ?? false,
            itemAttributes.Count == 0 ? parentAttributes : itemAttributes);
    }

    private static IReadOnlyList<MemberDeclaration> ParseStoredMembers(EntityRecord entity)
    {
        var result = new List<MemberDeclaration>();
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(entity.PayloadJson);
            var root = document.RootElement;
            if (root.TryGetProperty("declarations", out var declarations) &&
                declarations.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in declarations.EnumerateArray())
                {
                    var parsed = ParseStoredMember(item, root, entity);
                    if (parsed is not null)
                    {
                        result.Add(parsed);
                    }
                }
            }
            else
            {
                var fallback = ParseStoredMember(root, root, entity);
                if (fallback is not null)
                {
                    result.Add(fallback);
                }
            }
        }
        catch (JsonException)
        {
        }
        finally
        {
            document?.Dispose();
        }

        return result;
    }

    private static MemberDeclaration? ParseStoredMember(
        JsonElement item,
        JsonElement parent,
        EntityRecord entity)
    {
        var fileId = StringValue(item, "fileId") ?? entity.FileId;
        var file = StringValue(item, "file") ?? StringValue(parent, "file");
        var typeIdentity = StringValue(item, "containingTypeIdentity") ??
                           StringValue(parent, "containingTypeIdentity") ??
                           StringValue(item, "containingType") ??
                           StringValue(parent, "containingType");
        var name = StringValue(item, "name") ?? StringValue(parent, "name");
        var signature = StringValue(item, "signature") ?? StringValue(parent, "signature");
        if (fileId is null || file is null || typeIdentity is null || name is null || signature is null)
        {
            return null;
        }

        var itemAttributes = StringValues(item, "attributes");
        var parentAttributes = StringValues(parent, "attributes");
        var itemParameters = StringValues(item, "parameters");
        var parentParameters = StringValues(parent, "parameters");
        return new MemberDeclaration(
            StringValue(item, "scope") ?? StringValue(parent, "scope") ?? ScopeFromIdentity(entity.IdentityKey),
            fileId,
            file,
            IntegerValue(item, "line") ?? IntegerValue(parent, "line") ?? entity.Line,
            typeIdentity,
            StringValue(item, "memberKind") ?? StringValue(parent, "memberKind") ?? "method",
            name,
            signature,
            StringValue(item, "returnType") ?? StringValue(parent, "returnType") ?? string.Empty,
            itemParameters.Count == 0 ? parentParameters : itemParameters,
            StringValue(item, "accessibility") ?? StringValue(parent, "accessibility") ?? "private",
            BooleanValue(item, "isStatic") ?? BooleanValue(parent, "isStatic") ?? false,
            BooleanValue(item, "isAbstract") ?? BooleanValue(parent, "isAbstract") ?? false,
            itemAttributes.Count == 0 ? parentAttributes : itemAttributes,
            StringValues(item, "typeUsages"),
            ParseStoredUsages(item));
    }

    private static IReadOnlyList<MemberUsage> ParseStoredUsages(JsonElement item)
    {
        if (!item.TryGetProperty("memberUsages", out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray()
            .Select(value => new MemberUsage(
                StringValue(value, "receiver") ?? string.Empty,
                StringValue(value, "name") ?? string.Empty,
                IntegerValue(value, "argumentCount"),
                StringValue(value, "locator") ?? string.Empty))
            .Where(value => value.Name.Length > 0)
            .ToArray();
    }

    private static object ToPayload(TypeDeclaration declaration) => new
    {
        scope = declaration.Scope,
        fileId = declaration.FileId,
        file = declaration.FilePath,
        line = declaration.Line,
        @namespace = declaration.Namespace,
        name = declaration.Name,
        qualifiedName = declaration.QualifiedName,
        arity = declaration.Arity,
        typeKind = declaration.TypeKind,
        containingType = declaration.ContainingType,
        baseType = declaration.BaseType,
        interfaces = declaration.Interfaces,
        accessibility = declaration.Visibility,
        isStatic = declaration.IsStatic,
        isAbstract = declaration.IsAbstract,
        isSealed = declaration.IsSealed,
        isPartial = declaration.IsPartial,
        attributes = declaration.Attributes
    };

    private static object ToPayload(MemberDeclaration declaration) => new
    {
        scope = declaration.Scope,
        fileId = declaration.FileId,
        file = declaration.FilePath,
        line = declaration.Line,
        containingType = DisplayTypeName(declaration.TypeIdentity),
        containingTypeIdentity = declaration.TypeIdentity,
        memberKind = declaration.Kind,
        name = declaration.Name,
        signature = declaration.Signature,
        returnType = declaration.ReturnType,
        parameters = declaration.Parameters,
        accessibility = declaration.Visibility,
        isStatic = declaration.IsStatic,
        isAbstract = declaration.IsAbstract,
        attributes = declaration.Attributes,
        typeUsages = declaration.TypeUsages,
        memberUsages = declaration.MemberUsages.Select(usage => new
        {
            receiver = usage.Receiver,
            name = usage.Name,
            argumentCount = usage.ArgumentCount,
            locator = usage.Locator
        }).ToArray()
    };

    private static string TypeIdentity(TypeDeclaration declaration) =>
        TypeIdentity(declaration.Scope, declaration.QualifiedName, declaration.Arity);

    private static string TypeIdentity(string scope, string qualifiedName, int arity) =>
        scope + "\0" + qualifiedName + "\0" + arity;

    private static string MemberIdentity(MemberDeclaration declaration, string typeId) =>
        typeId + "\0" + declaration.Kind + "\0" + declaration.Signature;

    private static string CreateTypeId(string identity) =>
        StableEntityId.Create(
            TypeKind,
            ScopeFromIdentity(identity),
            "semantic:" + StableEntityId.DigestBase32(identity));

    private static string ScopeFromIdentity(string identity)
    {
        var separator = identity.IndexOf('\0');
        return separator > 0 ? identity[..separator] : identity;
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

    private static string NamespaceOf(string typeIdentity)
    {
        var separator = typeIdentity.LastIndexOf('\0');
        var qualified = separator > 0 ? typeIdentity[..separator] : typeIdentity;
        separator = qualified.LastIndexOf('.');
        return separator > 0 ? qualified[..separator] : string.Empty;
    }

    private static string NamespaceName(SyntaxNode node) => string.Join(
        ".",
        node.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse()
            .Select(item => item.Name.ToString())
            .Where(item => item.Length > 0));

    private static string TypeName(BaseTypeDeclarationSyntax type)
    {
        var arity = Arity(type);
        return type.Identifier.ValueText +
               (arity > 0 ? ((char)96).ToString() + arity : string.Empty);
    }

    private static int Arity(BaseTypeDeclarationSyntax type) =>
        type is TypeDeclarationSyntax declaration
            ? declaration.TypeParameterList?.Parameters.Count ?? 0
            : 0;

    private static string GetTypeKind(BaseTypeDeclarationSyntax type) => type switch
    {
        RecordDeclarationSyntax record when record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) => "record_struct",
        RecordDeclarationSyntax => "record",
        ClassDeclarationSyntax => "class",
        StructDeclarationSyntax => "struct",
        InterfaceDeclarationSyntax => "interface",
        EnumDeclarationSyntax => "enum",
        _ => "type"
    };

    private static IReadOnlyList<string> Attributes(SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(item => item.Attributes)
            .Select(item => Normalize(item.Name.ToString()))
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> Parameters(SeparatedSyntaxList<ParameterSyntax> parameters) =>
        parameters.Select(parameter =>
        {
            var modifier = parameter.Modifiers.Count == 0
                ? string.Empty
                : string.Join(" ", parameter.Modifiers.Select(item => item.Text)) + " ";
            return modifier + Normalize(parameter.Type?.ToString() ?? "var");
        }).ToArray();

    private static string Visibility(
        SyntaxTokenList modifiers,
        bool type,
        bool nested,
        bool interfaceMember)
    {
        if (HasModifier(modifiers, SyntaxKind.PublicKeyword)) return "public";
        if (HasModifier(modifiers, SyntaxKind.PrivateKeyword) &&
            HasModifier(modifiers, SyntaxKind.ProtectedKeyword)) return "private protected";
        if (HasModifier(modifiers, SyntaxKind.ProtectedKeyword) &&
            HasModifier(modifiers, SyntaxKind.InternalKeyword)) return "protected internal";
        if (HasModifier(modifiers, SyntaxKind.ProtectedKeyword)) return "protected";
        if (HasModifier(modifiers, SyntaxKind.PrivateKeyword)) return "private";
        if (HasModifier(modifiers, SyntaxKind.InternalKeyword)) return "internal";
        if (interfaceMember && !type) return "public";
        return type ? (nested ? "private" : "internal") : "private";
    }

    private static bool HasModifier(SyntaxTokenList modifiers, SyntaxKind kind) =>
        modifiers.Any(item => item.IsKind(kind));

    private static int? LineNumber(SyntaxNode node) =>
        node.SyntaxTree?.GetLineSpan(node.Span).StartLinePosition.Line + 1;

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool IsUsefulType(string value) => value.TrimEnd('?', '[', ']') is not (
        "" or "void" or "bool" or "byte" or "sbyte" or "char" or "decimal" or "double" or "float" or
        "int" or "uint" or "long" or "ulong" or "nint" or "nuint" or "object" or "short" or "ushort" or
        "string" or "dynamic" or "var");

    private static string DiagnosticText(Diagnostic diagnostic) =>
        "C# syntax error " + diagnostic.Id + " at line " +
        (diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1) + ".";

    private static string? StringValue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? IntegerValue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var result)
            ? result
            : null;

    private static bool? BooleanValue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static IReadOnlyList<string> StringValues(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
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

    private sealed record ParsedFile(
        IndexedFileRecord File,
        IReadOnlyList<TypeDeclaration> Types,
        IReadOnlyList<MemberDeclaration> Members,
        IReadOnlyList<HarmonyDeclaration> HarmonyPatches,
        string? Diagnostic);

    private sealed record HarmonyDeclaration(
        string Scope,
        string FileId,
        string FilePath,
        int? Line,
        string PatchClass,
        string PatchMethod,
        string PatchSignature,
        string PatchKind,
        string? TargetType,
        string? TargetMember,
        IReadOnlyList<string> TargetSignature,
        string? RawText,
        string Confidence);

    private sealed record HarmonyTarget(
        string? TargetType,
        string? TargetMember,
        IReadOnlyList<string> TargetSignature,
        string? RawText,
        string Confidence)
    {
        public static HarmonyTarget Empty { get; } = new(null, null, [], null, "syntax");
    }

    private sealed record HarmonyMethodInfo(
        string PatchClass,
        string Method,
        string Signature);

    private sealed record HarmonyResolution(
        string? TargetId,
        string? TargetMemberId,
        string ResolutionState)
    {
        public static HarmonyResolution Unresolved { get; } = new(null, null, "unresolved");
    }

    private sealed record TypeDeclaration(
        string Scope,
        string FileId,
        string FilePath,
        int? Line,
        string Namespace,
        string Name,
        string QualifiedName,
        int Arity,
        string TypeKind,
        string? ContainingType,
        string? BaseType,
        IReadOnlyList<string> Interfaces,
        string Visibility,
        bool IsStatic,
        bool IsAbstract,
        bool IsSealed,
        bool IsPartial,
        IReadOnlyList<string> Attributes);

    private sealed record MemberDeclaration(
        string Scope,
        string FileId,
        string FilePath,
        int? Line,
        string TypeIdentity,
        string Kind,
        string Name,
        string Signature,
        string ReturnType,
        IReadOnlyList<string> Parameters,
        string Visibility,
        bool IsStatic,
        bool IsAbstract,
        IReadOnlyList<string> Attributes,
        IReadOnlyList<string> TypeUsages,
        IReadOnlyList<MemberUsage> MemberUsages);

    private sealed record MemberUsage(
        string Receiver,
        string Name,
        int? ArgumentCount,
        string Locator);

    private sealed record TypeModel(
        string Id,
        string Identity,
        string Scope,
        string Namespace,
        string Name,
        string QualifiedName,
        int Arity,
        string TypeKind,
        string? ContainingType,
        string? BaseType,
        IReadOnlyList<string> Interfaces,
        string Visibility,
        bool IsStatic,
        bool IsAbstract,
        bool IsSealed,
        bool IsPartial,
        IReadOnlyList<string> Attributes,
        IReadOnlyList<TypeDeclaration> Declarations);

    private sealed record MemberModel(
        string Id,
        string Identity,
        string TypeId,
        string TypeIdentity,
        string Scope,
        string Kind,
        string Name,
        string Signature,
        string ReturnType,
        IReadOnlyList<string> Parameters,
        string Visibility,
        bool IsStatic,
        bool IsAbstract,
        IReadOnlyList<string> Attributes,
        IReadOnlyList<MemberDeclaration> Declarations);
}
