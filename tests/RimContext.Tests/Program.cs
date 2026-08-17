using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RimContext.Cli;
using RimContext.Core.Configuration;
using RimContext.Core.Contracts;
using RimContext.Core.Discovery;
using RimContext.Core.Model;
using RimContext.Core.Storage;

namespace RimContext.Tests;

internal static class Program
{
    private static int Main()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("cli startup and version", CliStartupAndVersion),
            ("json-only stdout and stderr logging", JsonOnlyStdout),
            ("unknown command and invalid input", InvalidInput),
            ("schema creation", SchemaCreation),
            ("schema reopening", SchemaReopening),
            ("incremental first index", IncrementalFirstIndex),
            ("incremental no-op reindex", IncrementalNoOp),
            ("incremental changed file", IncrementalChangedFile),
            ("incremental deleted file", IncrementalDeletedFile),
            ("incremental renamed file", IncrementalRenamedFile),
            ("stable ID determinism", StableIdDeterminism),
            ("schema-version handling", SchemaVersionHandling),
            ("workspace discovery", WorkspaceDiscoveryRules),
            ("excluded directories", ExcludedDirectories),
            ("path normalization", PathNormalization),
            ("invalid workspace input", InvalidWorkspaceInput),
            ("incremental fixture performance", IncrementalFixturePerformance),
            ("XML semantic entities and queries", XmlSemanticEntitiesAndQueries),
            ("malformed XML diagnostics", MalformedXmlDiagnostics),
            ("incremental XML cleanup", IncrementalXmlCleanup),
            ("C# semantic entities and queries", CSharpSemanticEntitiesAndQueries),
            ("Harmony indexing and queries", HarmonyIndexingAndQueries),
            ("mod project and dependency indexing", ModProjectDependencyIndexing),
            ("affected queries", AffectedQueries)
        };

        var passed = 0;
        var failed = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Body();
                passed++;
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"tests: {tests.Length}, passed: {passed}, failed: {failed}");
        return failed == 0 ? 0 : 1;
    }

    private static void CliStartupAndVersion()
    {
        var result = Run("version", "--json");
        AssertEqual(0, result.ExitCode, "version exit code");
        using var document = ParseJson(result.Stdout);
        AssertEqual("ok", document.RootElement.GetProperty("status").GetString(), "version status");
        AssertEqual("version", document.RootElement.GetProperty("command").GetString(), "version command");
        AssertEqual(IndexConstants.ToolVersion, document.RootElement.GetProperty("data").GetProperty("toolVersion").GetString(), "tool version");
    }

    private static void JsonOnlyStdout()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("Source/A.cs", "class A {}\n");
        var result = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, result.ExitCode, "index exit code");
        using var indexJson = ParseJson(result.Stdout);
        AssertEqual("ok", indexJson.RootElement.GetProperty("status").GetString(), "index status");
        var data = indexJson.RootElement.GetProperty("data");
        var files = data.GetProperty("files");
        AssertEqual(1, files.GetProperty("scanned").GetInt32(), "index scanned count");
        AssertEqual(1, files.GetProperty("added").GetInt32(), "index added count");
        AssertEqual(JsonValueKind.Number, data.GetProperty("duration_ms").ValueKind, "index duration");
        Assert(result.Stdout.TrimStart().StartsWith("{", StringComparison.Ordinal), "stdout must start with JSON");
        Assert(!result.Stdout.Contains("[info]", StringComparison.Ordinal), "logging must not leak into stdout");
        Assert(result.Stderr.Contains("[info]", StringComparison.Ordinal), "index diagnostics must be on stderr");

        var summary = Run("summary", "--root", workspace.Root, "--json");
        AssertEqual(0, summary.ExitCode, "summary exit code");
        using var summaryJson = ParseJson(summary.Stdout);
        AssertEqual("summary", summaryJson.RootElement.GetProperty("command").GetString(), "summary command");
    }

    private static void InvalidInput()
    {
        var unknown = Run("does-not-exist", "--json");
        AssertEqual(2, unknown.ExitCode, "unknown command exit code");
        using (var document = ParseJson(unknown.Stdout))
        {
            AssertEqual(ErrorCodes.InvalidArgument, document.RootElement.GetProperty("error").GetProperty("code").GetString(), "unknown command code");
        }

        var missingSelector = Run("find", "--json");
        AssertEqual(2, missingSelector.ExitCode, "missing selector exit code");
        using (var document = ParseJson(missingSelector.Stdout))
        {
            AssertEqual(ErrorCodes.InvalidArgument, document.RootElement.GetProperty("error").GetProperty("code").GetString(), "missing selector code");
        }

        var tooLarge = Run("find", "ThingDef", "--limit", "101", "--json");
        AssertEqual(2, tooLarge.ExitCode, "limit exit code");
        using var limitJson = ParseJson(tooLarge.Stdout);
        AssertEqual(ErrorCodes.LimitExceeded, limitJson.RootElement.GetProperty("error").GetProperty("code").GetString(), "limit code");
    }

    private static void SchemaCreation()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        Assert(File.Exists(configuration.StorePath), "index store should exist");
        AssertEqual(2, result.Counts.FileCount, "indexed file count");
        using var store = IndexStore.OpenReadOnly(configuration);
        AssertEqual(IndexConstants.SchemaVersion, store.Metadata.SchemaVersion, "schema version");
        AssertEqual(IndexConstants.ToolVersion, store.Metadata.ToolVersion, "tool version");
        AssertEqual(configuration.WorkspaceIdentity, store.Metadata.WorkspaceIdentity, "workspace identity");
        AssertEqual(FixedTime.ToUniversalTime().ToString("O"), store.Metadata.IndexedAtUtc, "index timestamp");
        AssertEqual(3L, store.GetCounts().EntityCount, "entity count");
        Assert(store.GetEntities().Any(entity => entity.Kind == "csharp_type"), "C# type entity should be present");
    }

    private static void SchemaReopening()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        using var first = IndexStore.OpenReadOnly(configuration);
        using var second = IndexStore.OpenReadOnly(configuration);
        AssertEqual(first.Metadata, second.Metadata, "reopened metadata");
        AssertEqual(first.GetCounts(), second.GetCounts(), "reopened counts");
    }

    private static void IncrementalFirstIndex()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        AssertEqual(2, result.Statistics.Scanned, "first scan count");
        AssertEqual(2, result.Statistics.Added, "first added count");
        AssertEqual(0, result.Statistics.Changed, "first changed count");
        AssertEqual(0, result.Statistics.Removed, "first removed count");
        AssertEqual(0, result.Statistics.Unchanged, "first unchanged count");
        Assert(result.DurationMilliseconds >= 0, "duration should be non-negative");
    }

    private static void IncrementalNoOp()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(2, result.Statistics.Scanned, "no-op scan count");
        AssertEqual(0, result.Statistics.Added, "no-op added count");
        AssertEqual(0, result.Statistics.Changed, "no-op changed count");
        AssertEqual(0, result.Statistics.Removed, "no-op removed count");
        AssertEqual(2, result.Statistics.Unchanged, "no-op unchanged count");
    }

    private static void IncrementalChangedFile()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        workspace.Write("Source/A.cs", "class A { int Changed; }\n");
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(1, result.Statistics.Changed, "changed count");
        AssertEqual(1, result.Statistics.Unchanged, "changed run unchanged count");
        AssertEqual(0, result.Statistics.Added, "changed run added count");
        AssertEqual(0, result.Statistics.Removed, "changed run removed count");

        using var store = IndexStore.OpenReadOnly(configuration);
        var source = store.GetFiles().Single(file => file.Path == "Source/A.cs");
        AssertEqual(64, source.ContentHash.Length, "changed file hash should be refreshed");
        AssertEqual(4L, store.GetCounts().EntityCount, "changed file should have current metadata and semantic entities");
    }

    private static void IncrementalDeletedFile()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        workspace.Delete("Defs/A.xml");
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(1, result.Statistics.Scanned, "delete scan count");
        AssertEqual(1, result.Statistics.Removed, "deleted count");
        AssertEqual(1, result.Statistics.Unchanged, "delete run unchanged count");

        using var store = IndexStore.OpenReadOnly(configuration);
        Assert(!store.GetFiles().Any(file => file.Path == "Defs/A.xml"), "deleted file row should be removed");
        Assert(!store.GetEntities().Any(entity => entity.IdentityKey == "Defs/A.xml"), "deleted file entity should be removed");
    }

    private static void IncrementalRenamedFile()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        workspace.Rename("Source/A.cs", "Source/B.cs");
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(1, result.Statistics.Added, "renamed added count");
        AssertEqual(1, result.Statistics.Removed, "renamed removed count");
        AssertEqual(1, result.Statistics.Unchanged, "renamed unchanged count");

        using var store = IndexStore.OpenReadOnly(configuration);
        var paths = store.GetFiles().Select(file => file.Path).ToArray();
        Assert(paths.Contains("Source/B.cs", StringComparer.Ordinal), "renamed target should be indexed");
        Assert(!paths.Contains("Source/A.cs", StringComparer.Ordinal), "renamed source should be removed");
    }

    private static void StableIdDeterminism()
    {
        var first = StableEntityId.Create("source_file", "workspace:root", "src/A.cs");
        var second = StableEntityId.Create("source_file", "workspace:root", "src/A.cs");
        AssertEqual(first, second, "same semantic identity");
        Assert(!first.Equals(StableEntityId.Create("source_file", "workspace:root", "src/B.cs"), StringComparison.Ordinal), "different path should have different ID");

        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);
        string firstFileId;
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            firstFileId = store.GetFiles().Single(file => file.Path == "Source/A.cs").Id;
        }

        workspace.Write("Unrelated.cs", "class Unrelated {}\n");
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        using var reopened = IndexStore.OpenReadOnly(configuration);
        var secondFileId = reopened.GetFiles().Single(file => file.Path == "Source/A.cs").Id;
        AssertEqual(firstFileId, secondFileId, "unrelated file changes must not change IDs");
    }

    private static void SchemaVersionHandling()
    {
        using var workspace = NewIndexedWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = configuration.StorePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var exception = AssertThrows<RimContextException>(() => IndexStore.OpenReadOnly(configuration), "schema mismatch should fail");
        AssertEqual(ErrorCodes.IndexIncompatible, exception.Error.Code, "schema mismatch code");
    }

    private static void WorkspaceDiscoveryRules()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("src/A.cs", "class A {}\n");
        workspace.Write("Defs/A.xml", "<Defs />\n");
        workspace.Write("Directory.Build.props", "<Project />\n");
        workspace.Write("notes.txt", "ignored\n");
        workspace.Write(".git/ignored.cs", "class Ignored {}\n");
        workspace.Write("bin/ignored.xml", "<Defs />\n");

        using var external = new TempWorkspace();
        external.Write("Lib.dll", "not an assembly yet\n");
        external.Write("Ignored.cs", "class Ignored {}\n");

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root, assemblyRoots: [external.Root]);
        var files = WorkspaceDiscovery.Discover(configuration);
        var paths = files.Select(file => file.DisplayPath).ToArray();
        Assert(paths.Contains("src/A.cs", StringComparer.Ordinal), "source file should be discovered");
        Assert(paths.Contains("Defs/A.xml", StringComparer.Ordinal), "XML file should be discovered");
        Assert(paths.Contains("Directory.Build.props", StringComparer.Ordinal), "build properties should be discovered");
        Assert(paths.Any(path => path.StartsWith("external/", StringComparison.Ordinal) && path.EndsWith("/Lib.dll", StringComparison.Ordinal)), "external assembly should be discovered");
        Assert(!paths.Any(path => path.Contains("ignored", StringComparison.OrdinalIgnoreCase)), "ignored/generated files should not be discovered");
        Assert(paths.SequenceEqual(paths.OrderBy(path => path, StringComparer.Ordinal)), "discovery order should be deterministic");
    }

    private static void ExcludedDirectories()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("Source/Kept.cs", "class Kept {}\n");
        workspace.Write(".git/Ignored.cs", "class Ignored {}\n");
        workspace.Write("bin/Ignored.cs", "class Ignored {}\n");
        workspace.Write("obj/Ignored.xml", "<Defs />\n");
        workspace.Write("artifacts/Ignored.cs", "class Ignored {}\n");
        workspace.Write(".rimctx/Ignored.cs", "class Ignored {}\n");

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);
        AssertEqual(1, result.Statistics.Scanned, "excluded directories must not be scanned");
        using var store = IndexStore.OpenReadOnly(configuration);
        AssertEqual("Source/Kept.cs", store.GetFiles().Single().Path, "kept file path");
    }

    private static void PathNormalization()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("Source/Nested.cs", "class Nested {}\n");
        var configuration = WorkspaceConfiguration.Resolve(Path.Combine(workspace.Root, "."));
        var files = WorkspaceDiscovery.Discover(configuration);
        AssertEqual("Source/Nested.cs", files.Single().DisplayPath, "root-relative path normalization");

        var slashId = StableEntityId.Create("source_file", "workspace:root", "Source/Nested.cs");
        var backslashId = StableEntityId.Create("source_file", "workspace:root", "Source\\Nested.cs");
        AssertEqual(slashId, backslashId, "slash normalization must preserve IDs");
    }

    private static void InvalidWorkspaceInput()
    {
        using var workspace = new TempWorkspace();
        var missingRoot = Path.Combine(workspace.Root, "does-not-exist");
        var result = Run("index", "--root", missingRoot, "--json");
        AssertEqual(4, result.ExitCode, "missing root exit code");
        using var document = ParseJson(result.Stdout);
        AssertEqual(ErrorCodes.PathNotFound, document.RootElement.GetProperty("error").GetProperty("code").GetString(), "missing root error code");
        Assert(!result.Stdout.Contains(missingRoot, StringComparison.Ordinal), "error output must not expose absolute input paths");
    }

    private static void IncrementalFixturePerformance()
    {
        using var workspace = new TempWorkspace();
        for (var index = 0; index < 64; index++)
        {
            workspace.Write($"Source/Generated{index:D3}.cs", $"class Generated{index} {{ }}\n");
        }

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var clock = Stopwatch.StartNew();
        var first = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);
        clock.Stop();
        var firstMilliseconds = clock.ElapsedMilliseconds;

        clock.Restart();
        var second = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        clock.Stop();
        var secondMilliseconds = clock.ElapsedMilliseconds;

        AssertEqual(64, first.Statistics.Added, "fixture first added count");
        AssertEqual(64, second.Statistics.Unchanged, "fixture no-op unchanged count");
        Console.WriteLine($"PERF fixture_files=64 first_ms={firstMilliseconds} second_ms={secondMilliseconds}");
    }

    private static void XmlSemanticEntitiesAndQueries()
    {
        using var workspace = NewXmlWorkspace();
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        AssertEqual(5, result.Statistics.Added, "XML fixture file count");
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            var entities = store.GetEntities();
            Assert(entities.Any(entity => entity.Kind == "def" && entity.PayloadJson.Contains("\"defName\":\"MyWeapon\"", StringComparison.Ordinal)), "simple def should be indexed");
            Assert(entities.Any(entity => entity.Kind == "def" && entity.PayloadJson.Contains("\"defType\":\"RecipeDef\"", StringComparison.Ordinal)), "multiple def types should be indexed");
            Assert(entities.Count(entity => entity.Kind == "def" && entity.PayloadJson.Contains("\"defName\":\"Duplicate\"", StringComparison.Ordinal)) == 2, "duplicate def names should retain separate entities");
            Assert(entities.Any(entity => entity.Kind == "patch_operation" && entity.PayloadJson.Contains("PatchOperationAdd", StringComparison.Ordinal)), "patch operation should be indexed");
            Assert(entities.Any(entity => entity.Kind == "mod" && entity.PayloadJson.Contains("com.test.mod", StringComparison.Ordinal)), "mod ownership should be indexed");

            var relations = store.GetRelations();
            Assert(relations.Any(relation => relation.Kind == "inheritance"), "inheritance relation should be indexed");
            Assert(relations.Any(relation => relation.Kind == "def_reference" && relation.ToId is not null), "resolved def reference should be indexed");
        }

        var definition = Run("definition", "ThingDef/MyWeapon", "--root", workspace.Root, "--json");
        AssertEqual(0, definition.ExitCode, "definition query exit code");
        using (var document = ParseJson(definition.Stdout))
        {
            var item = document.RootElement.GetProperty("results").EnumerateArray().Single();
            AssertEqual("ThingDef", item.GetProperty("defType").GetString(), "definition type");
            AssertEqual("MyWeapon", item.GetProperty("defName").GetString(), "definition name");
            AssertEqual("Mods/TestMod/Defs/Weapons.xml", item.GetProperty("file").GetString(), "definition file");
            Assert(item.GetProperty("line").GetInt32() > 0, "definition line");
            AssertEqual("com.test.mod", item.GetProperty("mod").GetString(), "definition owner");
            AssertEqual("BaseWeapon", item.GetProperty("parent").GetString(), "definition parent");
        }

        var find = Run("find", "Duplicate", "--root", workspace.Root, "--json");
        AssertEqual(0, find.ExitCode, "duplicate find exit code");
        using (var document = ParseJson(find.Stdout))
        {
            AssertEqual(2, document.RootElement.GetProperty("results").GetArrayLength(), "duplicate find result count");
        }

        var prefix = Run("find", "My", "--root", workspace.Root, "--json");
        AssertEqual(0, prefix.ExitCode, "prefix find exit code");
        using (var document = ParseJson(prefix.Stdout))
        {
            Assert(document.RootElement.GetProperty("results").EnumerateArray().Any(item => item.GetProperty("defName").GetString() == "MyWeapon"), "prefix find result");
        }

        var substring = Run("find", "Weapon", "--root", workspace.Root, "--json");
        AssertEqual(0, substring.ExitCode, "substring find exit code");
        using (var document = ParseJson(substring.Stdout))
        {
            Assert(document.RootElement.GetProperty("results").EnumerateArray().Any(item => item.GetProperty("defName").GetString() == "MyWeapon"), "substring find result");
        }

        var refs = Run("refs", "ThingDef/MyWeapon", "--root", workspace.Root, "--json");
        AssertEqual(0, refs.ExitCode, "refs query exit code");
        using (var document = ParseJson(refs.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            Assert(data.GetProperty("incoming").EnumerateArray().Any(), "incoming reference should be present");
            Assert(data.GetProperty("outgoing").EnumerateArray().Any(), "outgoing reference should be present");
            Assert(data.GetProperty("outgoing").EnumerateArray().All(item => item.GetProperty("direction").GetString() == "outgoing"), "outgoing direction");
        }
    }

    private static void MalformedXmlDiagnostics()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("Defs/Broken.xml", "<Defs><ThingDef><defName>Broken</defName>");
        var result = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, result.ExitCode, "malformed XML should not fail indexing");
        using var document = ParseJson(result.Stdout);
        AssertEqual("partial", document.RootElement.GetProperty("status").GetString(), "malformed XML status");
        var warning = document.RootElement.GetProperty("warnings").EnumerateArray().Single();
        AssertEqual("Defs/Broken.xml", warning.GetProperty("path").GetString(), "malformed XML warning path");
        Assert(warning.GetProperty("message").GetString()!.StartsWith("Malformed XML", StringComparison.Ordinal), "malformed XML warning message");

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        using var store = IndexStore.OpenReadOnly(configuration);
        var file = store.GetFiles().Single();
        AssertEqual("error", file.ParseStatus, "malformed XML parse status");
        Assert(file.Diagnostic?.StartsWith("Malformed XML", StringComparison.Ordinal) == true, "malformed XML file diagnostic");
    }

    private static void IncrementalXmlCleanup()
    {
        using var workspace = new TempWorkspace();
        workspace.Write("Defs/Changing.xml", "<Defs><ThingDef><defName>OldName</defName></ThingDef></Defs>");
        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);

        workspace.Write("Defs/Changing.xml", "<Defs><ThingDef><defName>NewName</defName></ThingDef></Defs>");
        var changed = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(1, changed.Statistics.Changed, "changed XML count");
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            var payloads = store.GetEntities().Where(entity => entity.Kind == "def").Select(entity => entity.PayloadJson).ToArray();
            Assert(payloads.Any(payload => payload.Contains("\"defName\":\"NewName\"", StringComparison.Ordinal)), "new XML def should be indexed");
            Assert(!payloads.Any(payload => payload.Contains("\"defName\":\"OldName\"", StringComparison.Ordinal)), "old XML def should be removed");
        }

        workspace.Delete("Defs/Changing.xml");
        var deleted = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(2));
        AssertEqual(1, deleted.Statistics.Removed, "deleted XML count");
        using var reopened = IndexStore.OpenReadOnly(configuration);
        Assert(!reopened.GetEntities().Any(entity => entity.Kind == "def"), "deleted XML entities should be removed");
    }

    private static void AffectedQueries()
    {
        using var workspace = new TempWorkspace();
        workspace.Write(
            "Source/Base.cs",
            """
            namespace Game;
            public class Base
            {
                public void Tick() { }
            }
            """);
        workspace.Write(
            "Source/Use.cs",
            """
            namespace Game;
            public class Use : Base
            {
                public void Run()
                {
                    var value = new Base();
                    value.Tick();
                }
            }
            """);
        workspace.Write(
            "Source/Patch.cs",
            """
            using Game;
            [HarmonyPatch(typeof(Base), "Tick")]
            public static class BasePatch
            {
                [HarmonyPostfix]
                public static void Postfix() { }
            }
            """);
        workspace.Write("Source/Isolated.cs", "namespace Isolated; public class OnlyHere { }");
        workspace.Write("Source/CycleA.cs", "namespace Cycle; public class A : B { }");
        workspace.Write("Source/CycleB.cs", "namespace Cycle; public class B : A { }");
        workspace.Write(
            "Defs/Weapons.xml",
            """
            <Defs>
              <ThingDef><defName>MyWeapon</defName></ThingDef>
            </Defs>
            """);
        workspace.Write(
            "Defs/Recipes.xml",
            """
            <Defs>
              <RecipeDef>
                <defName>MakeWeapon</defName>
                <thingDef>MyWeapon</thingDef>
              </RecipeDef>
            </Defs>
            """);

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var indexed = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);
        AssertEqual(0, indexed.Diagnostics?.Count ?? 0, "affected fixture diagnostics");

        var source = Run("affected", "Source/Base.cs", "--root", workspace.Root, "--json");
        AssertEqual(0, source.ExitCode, "source affected exit code");
        using (var document = ParseJson(source.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            AssertEqual("Source/Base.cs", data.GetProperty("changed").EnumerateArray().Single().GetString(), "source changed path");
            Assert(data.GetProperty("direct").EnumerateArray().Any(item =>
                item.GetProperty("kind").GetString() == "csharp_type" &&
                item.GetProperty("name").GetString() == "Game.Base"), "direct changed type");
            Assert(data.GetProperty("dependent").EnumerateArray().Any(item =>
                item.GetProperty("name").GetString() == "Game.Use"), "dependent shared type");
            var runtimeRisk = data.GetProperty("runtime_risk").EnumerateArray().ToArray();
            Assert(runtimeRisk.Any(item =>
                item.GetProperty("kind").GetString() == "harmony_patch" &&
                item.GetProperty("name").GetString() == "BasePatch.Postfix"), "Harmony runtime risk");
            Assert(runtimeRisk.All(item => item.TryGetProperty("reason", out var reason) &&
                                           reason.GetString()!.Contains("harmony_target", StringComparison.Ordinal)),
                "runtime risk reason");
        }

        var xml = Run("affected", "Defs/Weapons.xml", "--root", workspace.Root, "--json");
        AssertEqual(0, xml.ExitCode, "XML affected exit code");
        using (var document = ParseJson(xml.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            Assert(data.GetProperty("direct").EnumerateArray().Any(item =>
                item.GetProperty("name").GetString() == "ThingDef/MyWeapon"), "direct changed def");
            Assert(data.GetProperty("dependent").EnumerateArray().Any(item =>
                item.GetProperty("name").GetString() == "RecipeDef/MakeWeapon"), "dependent referenced def");
        }

        var absolute = Run(
            "affected",
            Path.Combine(workspace.Root, "Source", "Isolated.cs"),
            "--root",
            workspace.Root,
            "--json");
        using (var document = ParseJson(absolute.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            AssertEqual("Source/Isolated.cs", data.GetProperty("changed").EnumerateArray().Single().GetString(), "absolute path normalization");
            Assert(data.GetProperty("dependent").GetArrayLength() == 0, "isolated source has no dependents");
        }

        var multiple = Run(
            "affected",
            "Source/Isolated.cs",
            "Source/Base.cs",
            "Source/Isolated.cs",
            "--root",
            workspace.Root,
            "--json");
        using (var document = ParseJson(multiple.Stdout))
        {
            var changed = document.RootElement.GetProperty("data").GetProperty("changed").EnumerateArray().ToArray();
            AssertEqual(2, changed.Length, "multiple changed path deduplication");
            AssertEqual("Source/Base.cs", changed[0].GetString(), "multiple path ordering");
            AssertEqual("Source/Isolated.cs", changed[1].GetString(), "multiple path ordering second");
        }

        var limited = Run("affected", "Defs/Weapons.xml", "--root", workspace.Root, "--limit", "1", "--json");
        using (var document = ParseJson(limited.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            Assert(data.GetProperty("truncated").GetBoolean(), "affected result truncation");
            var resultCount = data.GetProperty("direct").GetArrayLength() +
                              data.GetProperty("dependent").GetArrayLength() +
                              data.GetProperty("runtime_risk").GetArrayLength();
            Assert(resultCount <= 1, "affected global result limit");
        }

        var cycle = Run("affected", "Source/CycleA.cs", "--root", workspace.Root, "--depth", "8", "--json");
        AssertEqual(0, cycle.ExitCode, "cyclic graph affected exit code");
        using (var document = ParseJson(cycle.Stdout))
        {
            Assert(document.RootElement.GetProperty("data").GetProperty("dependent").EnumerateArray().Any(item =>
                item.GetProperty("name").GetString() == "Cycle.B"), "cyclic graph dependent");
        }

        workspace.Delete("Source/Isolated.cs");
        var deleted = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(1, deleted.Statistics.Removed, "affected deleted file index count");
        var missing = Run("affected", "Source/Isolated.cs", "--root", workspace.Root, "--json");
        using (var document = ParseJson(missing.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            AssertEqual("Source/Isolated.cs", data.GetProperty("changed").EnumerateArray().Single().GetString(), "deleted changed path");
            AssertEqual(0, data.GetProperty("direct").GetArrayLength(), "deleted direct entities");
            AssertEqual(0, data.GetProperty("dependent").GetArrayLength(), "deleted dependent entities");
        }

        var unknown = Run("affected", "Source/Missing.cs", "--root", workspace.Root, "--json");
        using var unknownDocument = ParseJson(unknown.Stdout);
        AssertEqual(0, unknown.ExitCode, "unknown affected path exit code");
        AssertEqual(0, unknownDocument.RootElement.GetProperty("data").GetProperty("direct").GetArrayLength(), "unknown direct entities");
    }

    private static void ModProjectDependencyIndexing()
    {
        using var workspace = new TempWorkspace();
        workspace.Write(
            "Mods/A/About/About.xml",
            """
            <ModMetaData>
              <packageId>com.test.a</packageId>
              <name>Alpha</name>
              <supportedVersions><li>1.5</li><li>1.6</li></supportedVersions>
              <modDependencies><li><packageId>com.test.b</packageId></li></modDependencies>
              <loadAfter><li>com.external.after</li></loadAfter>
              <loadBefore><li>com.external.before</li></loadBefore>
              <incompatibleWith><li>com.external.incompatible</li></incompatibleWith>
            </ModMetaData>
            """);
        workspace.Write(
            "Mods/B/About/About.xml",
            """
            <ModMetaData>
              <packageId>com.test.b</packageId>
              <name>Beta</name>
            </ModMetaData>
            """);
        workspace.Write(
            "Mods/C/About/About.xml",
            """
            <ModMetaData>
              <packageId>com.test.a</packageId>
              <name>Duplicate Alpha</name>
            </ModMetaData>
            """);
        workspace.Write("Mods/A/Source/A.cs", "namespace Alpha; public class A {}\n");
        workspace.Write("Mods/B/Source/B.cs", "namespace Beta; public class B {}\n");
        workspace.Write(
            "Mods/A/A.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <RootNamespace>Alpha</RootNamespace>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../B/B.csproj" />
                <ProjectReference Include="../Missing/Missing.csproj" />
                <PackageReference Include="HarmonyLib" Version="2.3.0" />
                <Reference Include="Beta">
                  <HintPath>../B/bin/B.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);
        workspace.Write(
            "Mods/B/B.csproj",
            """
            <Project>
              <PropertyGroup>
                <TargetFrameworks>net8.0;net472</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var result = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);
        Assert(result.Diagnostics?.Count(item => item.Code == "DEPENDENCY") >= 3, "dependency diagnostics");

        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            var entities = store.GetEntities();
            AssertEqual(3, entities.Count(item => item.Kind == "mod"), "mod entity count");
            AssertEqual(2, entities.Count(item => item.Kind == "project"), "project entity count");
            Assert(entities.Any(item =>
                item.Kind == "project" &&
                item.PayloadJson.Contains("\"packageReferences\":[{\"include\":\"HarmonyLib\",\"version\":\"2.3.0\"}]", StringComparison.Ordinal)),
                "package reference payload");
            var relations = store.GetRelations();
            Assert(relations.Any(item => item.Kind == "requires"), "requires relation");
            Assert(relations.Any(item => item.Kind == "load_after"), "load after relation");
            Assert(relations.Any(item => item.Kind == "load_before"), "load before relation");
            Assert(relations.Any(item => item.Kind == "incompatible"), "incompatible relation");
            Assert(relations.Any(item => item.Kind == "project_reference"), "project reference relation");
            Assert(relations.Any(item => item.Kind == "assembly_reference"), "assembly reference relation");
            Assert(relations.Any(item => item.Kind == "owns" && item.ToId is not null), "ownership relation");
        }

        var summary = Run("summary", "--root", workspace.Root, "--json");
        AssertEqual(0, summary.ExitCode, "dependency summary exit code");
        using (var document = ParseJson(summary.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            AssertEqual(3, data.GetProperty("mods").GetInt64(), "summary mods");
            AssertEqual(2, data.GetProperty("projects").GetInt64(), "summary projects");
            AssertEqual(2, data.GetProperty("source_files").GetInt64(), "summary sources");
            AssertEqual(3, data.GetProperty("xml_files").GetInt64(), "summary XML");
            Assert(data.GetProperty("diagnostics").GetProperty("error").GetInt64() >= 2, "summary diagnostics");
        }

        var projectRefs = Run("refs", "Mods/B/B.csproj", "--root", workspace.Root, "--json");
        AssertEqual(0, projectRefs.ExitCode, "project refs exit code");
        using (var document = ParseJson(projectRefs.Stdout))
        {
            Assert(document.RootElement.GetProperty("data").GetProperty("incoming").EnumerateArray()
                .Any(item => item.GetProperty("kind").GetString() == "project_reference"),
                "project reference query");
        }

        workspace.Write(
            "Mods/B/B.csproj",
            """
            <Project>
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../A/A.csproj" /></ItemGroup>
            </Project>
            """);
        var cycle = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, cycle.ExitCode, "dependency cycle index exit code");
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            Assert(store.GetEntities().Any(item =>
                item.Kind == "diagnostic" &&
                item.PayloadJson.Contains("cycle detected", StringComparison.OrdinalIgnoreCase)),
                "project cycle diagnostic");
        }

        var modDefinition = Run("definition", "com.test.b", "--root", workspace.Root, "--json");
        AssertEqual(0, modDefinition.ExitCode, "mod definition exit code");
        using (var document = ParseJson(modDefinition.Stdout))
        {
            var item = document.RootElement.GetProperty("results").EnumerateArray().Single();
            AssertEqual("mod", item.GetProperty("kind").GetString(), "mod definition kind");
            AssertEqual("com.test.b", item.GetProperty("packageId").GetString(), "mod package ID");
            AssertEqual("Beta", item.GetProperty("name").GetString(), "mod name");
        }

        var noOp = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, noOp.ExitCode, "dependency no-op exit code");
        using (var document = ParseJson(noOp.Stdout))
        {
            Assert(document.RootElement.GetProperty("data").GetProperty("files").GetProperty("unchanged").GetInt32() > 0, "dependency no-op");
        }

        workspace.Write(
            "Mods/A/A.csproj",
            """
            <Project>
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../B/B.csproj" />
                <PackageReference Include="HarmonyLib" Version="2.3.0" />
              </ItemGroup>
            </Project>
            """);
        var changed = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, changed.ExitCode, "dependency changed index exit code");
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            Assert(!store.GetEntities().Any(item =>
                item.Kind == "diagnostic" &&
                item.PayloadJson.Contains("Missing project reference", StringComparison.Ordinal)),
                "stale project diagnostic cleanup");
        }

        workspace.Write(
            "Mods/B/B.csproj",
            """
            <Project>
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        var cycleRemoved = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, cycleRemoved.ExitCode, "dependency cycle cleanup index exit code");

        workspace.Delete("Mods/C/About/About.xml");
        var deleted = Run("index", "--root", workspace.Root, "--json");
        AssertEqual(0, deleted.ExitCode, "dependency deleted index exit code");
        var afterDelete = Run("summary", "--root", workspace.Root, "--json");
        using (var document = ParseJson(afterDelete.Stdout))
        {
            var data = document.RootElement.GetProperty("data");
            AssertEqual(2, data.GetProperty("mods").GetInt64(), "deleted mod cleanup");
            AssertEqual(0, data.GetProperty("diagnostics").GetProperty("error").GetInt64(), "duplicate cleanup");
        }
    }

    private static void HarmonyIndexingAndQueries()
    {
        using var workspace = new TempWorkspace();
        workspace.Write(
            "Source/Target.cs",
            """
            namespace Game;
            public class Target
            {
                public Target(string value) { }
                public void Do(int value) { }
                public string Value { get; set; }
            }
            """);
        Directory.CreateDirectory(Path.Combine(workspace.Root, "Assemblies"));
        File.Copy(
            typeof(Program).Assembly.Location,
            Path.Combine(workspace.Root, "Assemblies", "Reference.dll"),
            overwrite: true);
        workspace.Write(
            "Source/Patches.cs",
            """
            using Game;
            [HarmonyPatch(typeof(Game.Target), nameof(Game.Target.Do), new[] { typeof(int) })]
            public static class TargetPatch
            {
                [HarmonyPostfix]
                public static void Postfix() { }

                [HarmonyPatch(typeof(Game.Target), "Value", MethodType.Getter)]
                [HarmonyPrefix]
                public static void Getter() { }

                [HarmonyPatch(typeof(Game.Target), "Value", MethodType.Setter)]
                [HarmonyPostfix]
                public static void Setter() { }

                [HarmonyPatch(typeof(Game.Target), MethodType.Constructor)]
                [HarmonyPrefix]
                public static void ConstructorPatch() { }

                [HarmonyPrepare]
                public static bool Prepare() => true;

                [HarmonyCleanup]
                public static void Cleanup() { }

                [HarmonyTargetMethod]
                public static MethodBase TargetMethod() =>
                    AccessTools.Method(typeof(Game.Target), nameof(Game.Target.Do));

                [HarmonyTargetMethods]
                public static IEnumerable<MethodBase> TargetMethods() =>
                    new[] { AccessTools.Method(typeof(Game.Target), nameof(Game.Target.Do)) };
            }

            public static class DirectCall
            {
                public static void Apply(Harmony harmony)
                {
                    harmony.Patch(
                        AccessTools.Method(typeof(Game.Target), nameof(Game.Target.Do)),
                        postfix: new HarmonyMethod(typeof(DirectCall), nameof(DirectCall.Postfix)));
                }

                public static void Postfix() { }
            }

            [HarmonyPatch(typeof(RimContext.Tests.Program), "Main")]
            public static class AssemblyPatch
            {
                [HarmonyPrefix]
                public static void Prefix() { }
            }

            [HarmonyPatch(typeof(MissingType), "Nope")]
            public static class UnresolvedPatch
            {
                [HarmonyPrefix]
                public static void Prefix() { }
            }
            """);

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            var patches = store.GetEntities().Where(item => item.Kind == "harmony_patch").ToArray();
            Assert(patches.Length >= 6, "Harmony patch entities");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"patchKind\":\"postfix\"", StringComparison.Ordinal) &&
                item.PayloadJson.Contains("\"target\":\"Game.Target.Do\"", StringComparison.Ordinal) &&
                item.PayloadJson.Contains("\"targetSignature\":[\"int\"]", StringComparison.Ordinal) &&
                item.PayloadJson.Contains("\"resolved\":true", StringComparison.Ordinal)), "resolved postfix");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"target\":\"Game.Target.get_Value\"", StringComparison.Ordinal)), "property getter");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"target\":\"Game.Target.set_Value\"", StringComparison.Ordinal)), "property setter");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"target\":\"Game.Target..ctor\"", StringComparison.Ordinal)), "constructor patch");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"patchKind\":\"target_methods\"", StringComparison.Ordinal)), "target methods");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"patchKind\":\"prepare\"", StringComparison.Ordinal)), "prepare patch");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"patchKind\":\"cleanup\"", StringComparison.Ordinal)), "cleanup patch");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"patchClass\":\"DirectCall\"", StringComparison.Ordinal) &&
                item.PayloadJson.Contains("\"patchKind\":\"postfix\"", StringComparison.Ordinal)), "direct Harmony call");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"patchClass\":\"AssemblyPatch\"", StringComparison.Ordinal) &&
                item.PayloadJson.Contains("\"resolutionState\":\"resolved\"", StringComparison.Ordinal)),
                "assembly target resolution");
            Assert(patches.Any(item =>
                item.PayloadJson.Contains("\"resolutionState\":\"unresolved\"", StringComparison.Ordinal)), "unresolved target");
            Assert(store.GetRelations().Any(item => item.Kind == "harmony_target_member"), "Harmony member relation");
        }

        var target = Run("harmony", "Game.Target.Do", "--root", workspace.Root, "--json");
        AssertEqual(0, target.ExitCode, "Harmony target query exit code");
        using (var document = ParseJson(target.Stdout))
        {
            var result = document.RootElement.GetProperty("results").EnumerateArray().Single();
            AssertEqual("Game.Target.Do", result.GetProperty("target").GetString(), "Harmony target");
            Assert(result.GetProperty("patches").EnumerateArray().Any(item =>
                item.GetProperty("kind").GetString() == "postfix" &&
                item.GetProperty("resolved").GetBoolean()), "Harmony postfix query");
        }

        var refs = Run("refs", "Game.Target.Do", "--root", workspace.Root, "--json");
        AssertEqual(0, refs.ExitCode, "Harmony refs exit code");
        using (var document = ParseJson(refs.Stdout))
        {
            Assert(document.RootElement.GetProperty("data").GetProperty("incoming").EnumerateArray()
                .Any(item => item.GetProperty("kind").GetString() == "harmony_target_member"),
                "Harmony target reference");
        }

        var file = Run(
            "harmony",
            "--file",
            "Source/Patches.cs",
            "--root",
            workspace.Root,
            "--json");
        AssertEqual(0, file.ExitCode, "Harmony file query exit code");
        using (var document = ParseJson(file.Stdout))
        {
            Assert(document.RootElement.GetProperty("results").GetArrayLength() >= 4, "Harmony file results");
        }

        workspace.Write(
            "Source/Patches.cs",
            """
            [HarmonyPatch(typeof(Game.Target), "Do")]
            public static class ReplacementPatch
            {
                [HarmonyPrefix]
                public static void Prefix() { }
            }
            """);
        var changed = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(1, changed.Statistics.Changed, "Harmony incremental changed count");
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            var patches = store.GetEntities().Where(item => item.Kind == "harmony_patch").ToArray();
            AssertEqual(1, patches.Length, "stale Harmony entities removed");
            Assert(patches[0].PayloadJson.Contains("\"patchClass\":\"ReplacementPatch\"", StringComparison.Ordinal),
                "replacement Harmony entity");
            AssertEqual(
                2,
                store.GetRelations().Count(item => item.Kind is "harmony_target" or "harmony_target_member"),
                "stale Harmony relations removed");
        }
    }

    private static void CSharpSemanticEntitiesAndQueries()
    {
        using var workspace = new TempWorkspace();
        workspace.Write(
            "Source/Types.cs",
            """
            namespace Test;
            public interface IThing { }
            [Obsolete]
            public partial class MyClass : BaseClass, IThing
            {
                public int Field;
                private static string Property { get; set; }
                public void Run(int value) { }
                public void Run(string value) { }
                public static void Static() { }
                public class Nested { }
            }
            public enum Mode { One, Two }
            """);
        workspace.Write(
            "Source/MyClass.Part.cs",
            """
            namespace Test;
            public partial class MyClass
            {
                public void Part() { }
            }
            """);
        workspace.Write(
            "Source/Use.cs",
            """
            namespace Test;
            public class Use : MyClass
            {
                public void Call()
                {
                    var value = new MyClass();
                    value.Run(1);
                    MyClass.Static();
                }
            }
            """);

        var configuration = WorkspaceConfiguration.Resolve(workspace.Root);
        var first = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime);
        AssertEqual(3, first.Statistics.Added, "C# fixture file count");
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            var entities = store.GetEntities();
            Assert(entities.Any(item => item.Kind == "csharp_type" &&
                item.PayloadJson.Contains("Test.MyClass", StringComparison.Ordinal)), "qualified class");
            Assert(entities.Any(item => item.Kind == "csharp_type" &&
                item.PayloadJson.Contains("Test.MyClass.Nested", StringComparison.Ordinal)), "nested class");
            Assert(entities.Any(item => item.Kind == "csharp_type" &&
                item.PayloadJson.Contains("\"typeKind\":\"enum\"", StringComparison.Ordinal)), "enum");
            AssertEqual(2, entities.Count(item => item.Kind == "csharp_member" &&
                item.PayloadJson.Contains("\"signature\":\"Run", StringComparison.Ordinal)), "overloaded methods");
            Assert(entities.Any(item => item.Kind == "csharp_member" &&
                item.PayloadJson.Contains("\"name\":\"Part\"", StringComparison.Ordinal)), "partial member");
            Assert(store.GetRelations().Any(item => item.Kind == "csharp_interface_implementation"), "interface relation");
            Assert(store.GetRelations().Any(item => item.Kind == "csharp_inheritance"), "inheritance relation");
            Assert(store.GetRelations().Any(item => item.Kind == "csharp_member_usage"), "member relation");
        }

        var find = Run("find", "MyClass", "--root", workspace.Root, "--json");
        AssertEqual(0, find.ExitCode, "C# find exit code");
        using (var document = ParseJson(find.Stdout))
        {
            var item = document.RootElement.GetProperty("results").EnumerateArray()
                .First(item => item.GetProperty("kind").GetString() == "csharp_type");
            AssertEqual("Test.MyClass", item.GetProperty("name").GetString(), "C# find name");
            AssertEqual(6, item.GetProperty("members").GetInt32(), "C# member count");
        }

        var definition = Run("definition", "Test.MyClass", "--root", workspace.Root, "--json");
        AssertEqual(0, definition.ExitCode, "C# definition exit code");
        using (var document = ParseJson(definition.Stdout))
        {
            AssertEqual("csharp_type", document.RootElement.GetProperty("results").EnumerateArray()
                .Single().GetProperty("kind").GetString(), "C# definition kind");
        }

        var refs = Run("refs", "Test.MyClass", "--root", workspace.Root, "--json");
        AssertEqual(0, refs.ExitCode, "C# refs exit code");
        using (var document = ParseJson(refs.Stdout))
        {
            Assert(document.RootElement.GetProperty("data").GetProperty("incoming").EnumerateArray()
                .Any(item => item.GetProperty("kind").GetString() == "csharp_inheritance"), "C# incoming inheritance");
        }

        var outgoingRefs = Run("refs", "Test.MyClass", "--direction", "out", "--root", workspace.Root, "--json");
        AssertEqual(0, outgoingRefs.ExitCode, "C# outgoing refs exit code");
        using (var document = ParseJson(outgoingRefs.Stdout))
        {
            AssertEqual(0, document.RootElement.GetProperty("data").GetProperty("incoming").GetArrayLength(),
                "outgoing refs should omit incoming");
            Assert(document.RootElement.GetProperty("data").GetProperty("outgoing").GetArrayLength() > 0,
                "C# outgoing relations");
        }

        var file = Run("file", "Source/MyClass.Part.cs", "--root", workspace.Root, "--json");
        AssertEqual(0, file.ExitCode, "C# file exit code");
        using (var document = ParseJson(file.Stdout))
        {
            var data = document.RootElement.GetProperty("results").EnumerateArray().Single();
            AssertEqual(3, data.GetProperty("entityCount").GetInt32(), "partial file entity count");
            Assert(data.GetProperty("entities").EnumerateArray().Any(item =>
                item.GetProperty("name").GetString() == "Part"), "partial file member");
        }

        workspace.Write("Source/MyClass.Part.cs", "namespace Test; public partial class MyClass { public void Changed() { } }\n");
        var changed = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(1));
        AssertEqual(1, changed.Statistics.Changed, "C# incremental changed count");
        using (var store = IndexStore.OpenReadOnly(configuration))
        {
            var payloads = store.GetEntities().Where(item => item.Kind == "csharp_member")
                .Select(item => item.PayloadJson).ToArray();
            Assert(payloads.Any(item => item.Contains("\"name\":\"Changed\"", StringComparison.Ordinal)), "changed member");
            Assert(!payloads.Any(item => item.Contains("\"name\":\"Part\"", StringComparison.Ordinal)), "stale partial member removed");
        }

        workspace.Write("Source/Broken.cs", "public class Broken {");
        var malformed = new WorkspaceIndexer().Build(configuration, indexedAtUtc: FixedTime.AddMinutes(2));
        Assert(malformed.Diagnostics?.Any(item => item.Path == "Source/Broken.cs") == true, "C# diagnostic");
    }

    private static TempWorkspace NewIndexedWorkspace()
    {
        var workspace = new TempWorkspace();
        workspace.Write("Source/A.cs", "class A {}\n");
        workspace.Write("Defs/A.xml", "<Defs />\n");
        return workspace;
    }

    private static TempWorkspace NewXmlWorkspace()
    {
        var workspace = new TempWorkspace();
        workspace.Write(
            "Mods/TestMod/About/About.xml",
            """
            <ModMetaData>
              <packageId>com.test.mod</packageId>
              <name>Test Mod</name>
            </ModMetaData>
            """);
        workspace.Write(
            "Mods/TestMod/Defs/Weapons.xml",
            """
            <Defs>
              <ThingDef ParentName="BaseWeapon">
                <defName>MyWeapon</defName>
                <thingDef>BaseWeapon</thingDef>
              </ThingDef>
              <ThingDef>
                <defName>BaseWeapon</defName>
              </ThingDef>
            </Defs>
            """);
        workspace.Write(
            "Mods/TestMod/Defs/Recipes.xml",
            """
            <Defs>
              <RecipeDef>
                <defName>MakeWeapon</defName>
                <thingDef>MyWeapon</thingDef>
              </RecipeDef>
              <ThingDef>
                <defName>Duplicate</defName>
              </ThingDef>
            </Defs>
            """);
        workspace.Write(
            "Mods/TestMod/Defs/Other.xml",
            """
            <Defs>
              <ThingDef>
                <defName>Duplicate</defName>
              </ThingDef>
            </Defs>
            """);
        workspace.Write(
            "Mods/TestMod/Patches/Weapons.xml",
            """
            <Patch>
              <Operation Class="PatchOperationAdd">
                <xpath>/Defs/ThingDef[defName="MyWeapon"]/label</xpath>
                <value>patched weapon</value>
              </Operation>
            </Patch>
            """);
        return workspace;
    }

    private static CliResult Run(params string[] args)
    {
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = CliApplication.Run(args, stdout, stderr);
        return new CliResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static JsonDocument ParseJson(string output)
    {
        Assert(!string.IsNullOrWhiteSpace(output), "CLI stdout should not be empty");
        return JsonDocument.Parse(output);
    }

    private static T AssertThrows<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException(message);
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected '{expected}', actual '{actual}'.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "rimctx-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            System.IO.File.WriteAllText(path, content);
        }

        public void Delete(string relativePath)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            System.IO.File.Delete(path);
        }

        public void Rename(string relativePath, string newRelativePath)
        {
            var source = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var destination = Path.Combine(Root, newRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            System.IO.File.Move(source, destination);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static readonly DateTimeOffset FixedTime = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
}
