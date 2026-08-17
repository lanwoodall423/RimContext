using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RimContext.Core.Contracts;
using RimContext.Core.Model;

namespace RimContext.Core.Output;

public sealed record JsonOutputOptions(
    bool Compact = true,
    int? MaxBytes = null,
    bool HumanReadable = false);

public sealed record JsonQueryMetadata(int Count, bool Truncated);

public sealed class JsonEnvelope
{
    public string SchemaVersion { get; init; } = IndexConstants.SchemaVersionText;

    public string Status { get; init; } = "ok";

    public string Command { get; init; } = "unknown";

    public object? Results { get; init; }

    public object? Data { get; init; }

    public JsonQueryMetadata? Meta { get; init; }

    public IReadOnlyList<JsonWarning>? Warnings { get; init; }

    public string? Code { get; init; }

    public string? Message { get; init; }

    public string? Path { get; init; }

    public object? Details { get; init; }
}

public sealed record JsonWarning(string Code, string Message, string? Path = null);

public static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static JsonEnvelope Success(
        string command,
        object? data = null,
        object? results = null,
        JsonQueryMetadata? meta = null) => new()
        {
            Command = command,
            Data = data,
            Results = results,
            Meta = meta
        };

    public static JsonEnvelope Partial(
        string command,
        object? data = null,
        object? results = null,
        IReadOnlyList<JsonWarning>? warnings = null,
        JsonQueryMetadata? meta = null) => new()
        {
            Command = command,
            Status = "partial",
            Data = data,
            Results = results,
            Warnings = warnings,
            Meta = meta
        };

    public static JsonEnvelope Error(string command, RimContextError error) => new()
    {
        Command = command,
        Status = "error",
        Code = error.Code,
        Message = error.Message,
        Path = error.Path,
        Details = error.Details
    };

    public static void Write(
        TextWriter writer,
        JsonEnvelope envelope,
        JsonOutputOptions? outputOptions = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(envelope);
        writer.WriteLine(Serialize(envelope, outputOptions));
    }

    public static string Serialize(JsonEnvelope envelope, JsonOutputOptions? outputOptions = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var options = outputOptions ?? new JsonOutputOptions();
        if (options.HumanReadable && options.MaxBytes is null)
        {
            return JsonSerializer.Serialize(envelope, HumanOptions);
        }

        var node = JsonSerializer.SerializeToNode(envelope, Options) ?? new JsonObject();
        if (options.Compact)
        {
            CompactNode(node);
        }

        return SerializeWithinLimit(node, envelope, options.MaxBytes);
    }

    public static string SerializePayload(object value) => JsonSerializer.Serialize(value, Options);

    private static readonly JsonSerializerOptions HumanOptions = new(Options)
    {
        WriteIndented = true
    };

    private static string SerializeWithinLimit(JsonNode node, JsonEnvelope envelope, int? maxBytes)
    {
        var json = node.ToJsonString(Options);
        if (maxBytes is null || GetByteCount(json) <= maxBytes.Value)
        {
            return json;
        }

        MarkTruncated(node);
        while (GetByteCount(json) > maxBytes.Value)
        {
            var array = FindTrimmableArrays(node)
                .OrderBy(item => item.Priority)
                .ThenBy(item => item.Path, StringComparer.Ordinal)
                .FirstOrDefault(item => item.Array.Count > 0);
            if (array is null)
            {
                break;
            }

            array.Array.RemoveAt(array.Array.Count - 1);
            CompactNode(node);
            json = node.ToJsonString(Options);
        }

        if (GetByteCount(json) <= maxBytes.Value)
        {
            return json;
        }

        var fallback = new JsonObject
        {
            ["status"] = envelope.Status == "error" ? "error" : "partial",
            ["code"] = envelope.Status == "error" ? envelope.Code : "OUTPUT_LIMIT",
            ["message"] = envelope.Status == "error"
                ? envelope.Message
                : "Response exceeded --max-bytes; result arrays were omitted.",
            ["command"] = envelope.Command,
            ["truncated"] = true
        };
        return fallback.ToJsonString(Options);
    }

    private static void CompactNode(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var propertyName in jsonObject.Select(property => property.Key).ToArray())
            {
                if (!jsonObject.TryGetPropertyValue(propertyName, out var value) || value is null)
                {
                    jsonObject.Remove(propertyName);
                    continue;
                }

                CompactNode(value);
                if (value is JsonArray array && array.Count == 0)
                {
                    jsonObject.Remove(propertyName);
                }
            }

            RemoveRedundantNames(jsonObject);
            return;
        }

        if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray.Where(item => item is not null).Cast<JsonNode>().ToArray())
            {
                CompactNode(item);
            }
        }
    }

    private static void RemoveRedundantNames(JsonObject jsonObject)
    {
        if (GetString(jsonObject, "name") is { } name &&
            string.Equals(name, GetString(jsonObject, "qualifiedName"), StringComparison.Ordinal))
        {
            jsonObject.Remove("qualifiedName");
        }
    }

    private static void MarkTruncated(JsonNode node)
    {
        if (node is not JsonObject root)
        {
            return;
        }

        if (root["meta"] is not JsonObject meta)
        {
            meta = new JsonObject();
            root["meta"] = meta;
        }

        meta["truncated"] = true;
        if (root["data"] is JsonObject data && data.ContainsKey("truncated"))
        {
            data["truncated"] = true;
        }
    }

    private static IReadOnlyList<TrimmableArray> FindTrimmableArrays(JsonNode node)
    {
        var arrays = new List<TrimmableArray>();
        CollectArrays(node, string.Empty, arrays);
        return arrays;
    }

    private static void CollectArrays(JsonNode node, string path, ICollection<TrimmableArray> arrays)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject)
            {
                if (property.Value is JsonArray array)
                {
                    var arrayPath = string.IsNullOrEmpty(path)
                        ? property.Key
                        : path + "." + property.Key;
                    if (!string.Equals(property.Key, "changed", StringComparison.Ordinal))
                    {
                        arrays.Add(new TrimmableArray(
                            array,
                            arrayPath,
                            ArrayPriority(property.Key)));
                    }

                    CollectArrays(array, arrayPath, arrays);
                }
                else if (property.Value is not null)
                {
                    CollectArrays(property.Value, path + "." + property.Key, arrays);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray.Where(item => item is not null).Cast<JsonNode>())
            {
                CollectArrays(item, path, arrays);
            }
        }
    }

    private static int ArrayPriority(string name) => name switch
    {
        "attributes" or "interfaces" or "targetSignature" or "supportedVersions" or
            "modDependencies" or "loadAfter" or "loadBefore" or "incompatibleWith" or
            "projectReferences" or "packageReferences" or "assemblyReferences" => 0,
        "runtime_risk" => 10,
        "outgoing" => 20,
        "incoming" => 21,
        "dependent" => 30,
        "patches" => 35,
        "entities" => 40,
        "results" => 50,
        "direct" => 60,
        _ => 5
    };

    private static string? GetString(JsonObject value, string name) =>
        value[name] is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var result)
            ? result
            : null;

    private static int GetByteCount(string value) =>
        System.Text.Encoding.UTF8.GetByteCount(value);

    private sealed record TrimmableArray(JsonArray Array, string Path, int Priority);
}
